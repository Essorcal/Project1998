#!/usr/bin/env bash
# Drives the deploy job's superseded check against a stubbed `gh`.
#
# The check lives in .github/workflows/ci.yml, in the "Skip if a newer commit has already staged" step, between
# the "# >>> superseded-guard" and "# <<< superseded-guard" lines. This script lifts that block out of the
# workflow, so the function tested here is byte for byte the one the deploy runs, and calls it once per case
# with a fake GitHub API behind `gh`. It needs bash 4+ and coreutils: Git Bash on Windows, or ubuntu-latest.
#
#   bash Tests/DeployGuard/superseded-guard.test.sh
#
# The stub returns what `gh api ... --jq` would print, so the jq filters themselves are not exercised here;
# they were checked against the live API when the step was written (see the PR). The one live dependency this
# script does check is that the installed gh still has --allow-escape-sequences, which the log fetch passes.
#
# Exit 0 when every case passes, 1 otherwise.

set -u

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ci="$here/../../.github/workflows/ci.yml"
fail=0
cases=0

check() {  # name, condition result (0 = pass), detail
    cases=$((cases + 1))
    if [ "$2" -eq 0 ]; then
        echo "ok    $1"
    else
        echo "FAIL  $1: $3"
        fail=$((fail + 1))
    fi
}

# ------------------------------------------------------------------------------------------------------------
# The workflow wiring the function depends on. Each of these drifting makes the check quietly useless (it
# would deploy every time) or, worse, lets a skipped deploy run its later steps anyway.

[ -f "$ci" ] || { echo "FAIL  ci.yml not found at $ci"; exit 1; }
block=$(awk '/^ *# >>> superseded-guard *\r?$/ {on = 1; next} /^ *# <<< superseded-guard *\r?$/ {on = 0} on' "$ci" | tr -d '\r')
[ -n "$block" ]; check "the guard block is found between its markers in ci.yml" $? "no lines between the markers"

step() {  # print one deploy step's lines, from its "- name:" to the next step
    awk -v want="$1" '
        /^      - (name|uses):/ { on = (index($0, "- name: " want) > 0) }
        /^  [a-z]/ { on = 0 }
        on' "$ci" | tr -d '\r'
}
for s in "Upload the release" "Stage and schedule" "Set up SSH"; do
    step "$s" | grep -Fq "if: steps.superseded.outputs.skip != 'true'"
    check "the \"$s\" step is skipped when the guard says skip" $? "its if: line is missing"
done
awk '/uses: actions\/download-artifact/ {getline; print}' "$ci" | grep -Fq "if: steps.superseded.outputs.skip != 'true'"
check "the artifact download is skipped when the guard says skip" $? "its if: line is missing"
[ "$(grep -c '^      - name: Stage and schedule' "$ci")" -eq 1 ]
check "exactly one step is named \"Stage and schedule\" (the guard reads that name from the jobs API)" $? "renamed or duplicated"
step "Stage and schedule" | grep -Fq 'grep -Fq "staging release $SHA" deploy.log' \
    && step "Stage and schedule" | grep -Fq "grep -Fq 'game restart scheduled' deploy.log"
check "the stage step still reads the two host lines the guard takes as proof" $? "the stage step's host signature changed"
step "Skip if a newer commit has already staged" | grep -Eq '^ +continue-on-error: true$' \
    && step "Skip if a newer commit has already staged" | grep -Eq '^ +timeout-minutes: [0-9]+$'
check "the guard step has continue-on-error and a timeout, so neither a crash nor a hang blocks a deploy" $? "one is missing"

# The log fetch passes --allow-escape-sequences (gh refuses to print a job log without it). An older gh would
# reject the flag, the fetch would fail, and every deploy would fall back to "deploy": safe, and useless. So a
# missing flag is a warning here, never a failure: this script gates the build job, and every deploy needs that
# job, so failing on a runner-image change would stop all deploys over something that only disables the guard.
if gh_bin=$(type -P gh); then
    gh_version=$("$gh_bin" --version | head -n 1)
    if "$gh_bin" api --help 2>/dev/null | grep -Fq -- '--allow-escape-sequences'; then
        echo "ok    the installed gh ($gh_version) accepts --allow-escape-sequences"
    else
        echo "::warning::The installed gh ($gh_version) no longer lists --allow-escape-sequences. The deploy's superseded check cannot read job logs with it, so it will deploy every time (safe, but the guard is off) until the log fetch in ci.yml is updated."
        echo "warn  the installed gh ($gh_version) lacks --allow-escape-sequences (a warning, not a failure)"
    fi
else
    echo "note  gh is not installed here, so its --allow-escape-sequences flag was not checked"
fi

eval "$block" || { echo "FAIL  the guard block does not parse"; exit 1; }
declare -F superseded_guard > /dev/null
check "the block defines superseded_guard" $? "function missing"

# ------------------------------------------------------------------------------------------------------------
# The fake API. OUT[path] is what `gh api path --jq ...` prints; RC[path] is its exit status, or a space
# separated list consumed one per call (the last one repeats). A path with neither is a 404, printed the way
# gh prints one: an error body on stdout and a message on stderr. Calls land in a file because the guard runs
# gh inside $(...), where a variable written by the stub would not survive.

state=$(mktemp -d)
trap 'rm -rf "$state"' EXIT
declare -A OUT RC

gh() {
    if [ "${1-}" != api ]; then echo "stub gh: unexpected call: $*" >&2; return 99; fi
    local path=$2 n codes rc
    printf '%s\n' "$path" >> "$state/calls"
    if [ -z "${OUT[$path]+set}" ] && [ -z "${RC[$path]+set}" ]; then
        echo '{"message":"Not Found","status":"404"}'
        echo "gh: Not Found (HTTP 404)" >&2
        return 1
    fi
    n=$(grep -cxF -- "$path" "$state/calls")
    read -r -a codes <<< "${RC[$path]:-0}"
    if [ "$n" -le "${#codes[@]}" ]; then rc=${codes[$((n - 1))]}; else rc=${codes[$((${#codes[@]} - 1))]}; fi
    if [ "$rc" -ne 0 ]; then
        echo '{"message":"Server Error","status":"502"}'
        echo "gh: Server Error (HTTP 502)" >&2
        return "$rc"
    fi
    printf '%s\n' "${OUT[$path]-}"
}
GUARD_SLEEP=fake_sleep
fake_sleep() { echo x >> "$state/sleeps"; }

REPO=project1998/Project1998
p_tip="repos/$REPO/commits/master"
p_runs="repos/$REPO/actions/workflows/ci.yml/runs?branch=master&per_page=50"
p_cmp() { echo "repos/$REPO/compare/$1...$2"; }
p_jobs() { echo "repos/$REPO/actions/runs/$1/jobs"; }
p_log() { echo "repos/$REPO/actions/jobs/$1/logs"; }
jobs_line() { echo "$1 $2"; }   # what the jobs --jq filter prints: "<deploy job id> <status>/<conclusion>|none"

ESC=$'\033'
# The echoed script: what every deploy log that ran these steps contains whatever the host did. Both the stage
# step's greps and this guard's own lines, with the colour code the runner adds and, for a runner that ever
# drops it, without. None of it may count as proof.
ECHOED=$(
    {
        printf '%s\n' "  && grep -Fq \"staging release \$SHA\" deploy.log \\" "  && grep -Fq 'game restart scheduled' deploy.log \\"
        printf '%s\n' "$block"
    } | while IFS= read -r line; do
        printf '2026-09-25T20:07:31.2272710Z %s[36;1m%s%s[0m\n' "$ESC" "$line" "$ESC"
        printf '2026-09-25T20:07:31.2272710Z [36;1m%s[0m\n' "$line"
    done
)
echoed_script() { printf '%s\n' "$ECHOED"; }
# A code-lane deploy log in which the host staged $1 (the host lines of run 36183622957, job 108232297541).
staged_log() {
    echoed_script
    printf '%s\n' \
        "2026-09-25T20:07:31.9498837Z [***] unpacking $1" \
        "2026-09-25T20:07:33.1306999Z [***] mirroring content into /opt/project1998/game-data" \
        "2026-09-25T20:07:33.5645596Z [***] staging release $1" \
        "2026-09-25T20:07:33.5762134Z [***] game restart scheduled in 3m (deadline 1790367033000)" \
        "2026-09-25T20:07:33.6010255Z Running timer as unit: p1998-login-re***.timer" \
        "2026-09-25T20:07:33.6030706Z [***] login restart armed" \
        "2026-09-25T20:07:33.6822159Z [***] done"
}
# Staged, then the stage step went red on the login-timer collision (run 36177648282, job 108212859455).
staged_red_log() {
    echoed_script
    printf '%s\n' \
        "2026-09-25T19:09:16.6812532Z [***] unpacking $1" \
        "2026-09-25T19:09:18.3168478Z [***] staging release $1" \
        "2026-09-25T19:09:18.3307475Z [***] game restart scheduled in 3m (deadline 1790363538000)" \
        "2026-09-25T19:09:18.3468276Z Failed to start transient timer unit: Unit p1998-login-re***.timer was already loaded or has a fragment file." \
        "2026-09-25T19:09:18.9252781Z ##[error]Collided with the pending login restart, and this commit is not master's tip ($2): it may have staged an older build than production had."
}
# The stage step ran and failed before the host staged anything: only the echoed script and the error.
unstaged_log() {
    echoed_script
    printf '%s\n' \
        "2026-09-25T20:07:44.1000000Z ssh: connect to host *** port 22: Connection refused" \
        "2026-09-25T20:07:44.2000000Z ##[error]Process completed with exit code 255."
}
# Host lines naming a different commit than the run's own (a log that is not proof for $1).
other_sha_log() {
    printf '%s\n' \
        "2026-09-25T20:07:33.5645596Z [***] staging release $2" \
        "2026-09-25T20:07:33.5762134Z [***] game restart scheduled in 3m (deadline 1790367033000)"
}
# A deploy whose host output has the staging line but no restart booked.
no_restart_log() {
    echoed_script
    printf '%s\n' \
        "2026-09-25T20:07:33.1306999Z [***] mirroring content into /opt/project1998/game-data" \
        "2026-09-25T20:07:33.5645596Z [***] staging release $1"
}

reset_api() {
    unset OUT RC
    declare -gA OUT=() RC=()
    : > "$state/calls"
    : > "$state/sleeps"
}

# run_case NAME WANT_SKIP WANT_NEWER ANNOTATION TEXT_REGEX
#   ANNOTATION is notice, warning or none: the one kind of workflow command the output must carry (none = no
#   ::notice:: and no ::warning:: at all). TEXT_REGEX must match somewhere in the output.
run_case() {
    local name=$1 want_skip=$2 want_newer=$3 kind=$4 text=$5 out why=""
    GUARD_SKIP=unset GUARD_NEWER=unset
    superseded_guard > "$state/out" 2> "$state/err" || true
    out=$(cat "$state/out")
    [ "$GUARD_SKIP" = "$want_skip" ] || why+=" skip=$GUARD_SKIP, wanted $want_skip;"
    [ "$GUARD_NEWER" = "$want_newer" ] || why+=" newer='$GUARD_NEWER', wanted '$want_newer';"
    case $kind in
        notice)  grep -q '^::notice::' <<< "$out" && ! grep -q '^::warning::' <<< "$out" || why+=" wanted exactly a ::notice::;" ;;
        warning) grep -q '^::warning::' <<< "$out" && ! grep -q '^::notice::' <<< "$out" || why+=" wanted exactly a ::warning::;" ;;
        none)    ! grep -q '^::' <<< "$out" || why+=" wanted no annotation;" ;;
    esac
    grep -Eq -- "$text" <<< "$out" || why+=" output does not match /$text/;"
    [ -z "$why" ]
    check "$name" $? "${why# } output: $(tr '\n' '|' <<< "$out")"
}
calls() { grep -c . "$state/calls"; }
sleeps() { grep -c . "$state/sleeps"; }

# ------------------------------------------------------------------------------------------------------------
# Incident 1 (2026-09-25 19:09): #289's 84992e9 and #290's 1e95345, master's tip. Runs and deploy job ids are
# the real ones; compare lists the real commits 84992e9..1e95345.

OLD=84992e929acee286bae96fc07b9792aa03432238     # run 36177648282, deploy job 108212859455
TIP=1e95345654eaec96ea1eaf29a2cf26caa9454ac1     # run 36177655032, deploy job 108212779155
MID_PR=902e34b331173b4ce4777d7673b922237a1ad6b8  # a #290 branch commit: newer, but never a master push
PREV=0601e34d888b18e481ebf7d322dfd93160afe1de    # run 36094676883, deploy job 107944819452
CMP_AHEAD="ahead
13514ce65ebe4b2b1bb3526c84bd29190e269d4c
53d83cf7c865be4c72fb71bae54d2f7abfcc2513
$MID_PR
$TIP"
RUNS="36177655032 $TIP
36177648282 $OLD
36094676883 $PREV"

base_incident1() {  # the API as 84992e9's deploy found it at 19:09:07, before any per-case change
    reset_api
    SHA=$OLD
    OUT[$p_tip]=$TIP
    OUT[$(p_cmp $OLD $TIP)]=$CMP_AHEAD
    OUT[$p_runs]=$RUNS
    OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 completed/success)
    OUT[$(p_log 108212779155)]=$(staged_log $TIP)
}

base_incident1
run_case "incident 1 replay: 84992e9 skips, 1e95345 (tip) already staged" true $TIP notice \
    "Superseded: $TIP is newer than $OLD .*run 36177655032"

# ---- rule: tip equals SHA
base_incident1; SHA=$TIP
run_case "tip equals SHA: deploy" false "" none "master's tip"
[ "$(calls)" -eq 1 ]; check "tip equals SHA: one API call, nothing else looked up" $? "$(calls) calls"

# ---- rule: not an ancestor of the tip
for st in diverged behind identical; do
    base_incident1; OUT[$(p_cmp $OLD $TIP)]="$st"
    run_case "not behind the tip (compare: $st): deploy as before" false "" none "not behind master's tip .*compare: $st"
done

# ---- rule: tip ahead, and the state of the newer commit's deploy
base_incident1; OUT[$(p_jobs 36177655032)]=""
run_case "tip ahead, its run still building (no deploy job yet): deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 none)
run_case "tip ahead, its deploy queued behind this one: deploy (it stages after)" false "" none "No master commit newer"
grep -Fq "$(p_log 108212779155)" "$state/calls"; [ $? -ne 0 ]
check "tip ahead, deploy queued: no log fetched for a stage that never ran" $? "log was fetched"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 in_progress/null)
run_case "tip ahead, its stage step reported in progress: deploy" false "" none "No master commit newer"

base_incident1
run_case "tip ahead, its deploy succeeded (staged): skip" true $TIP notice "Superseded: $TIP"

base_incident1; OUT[$(p_log 108212779155)]=$(staged_red_log $TIP ffffffffffffffffffffffffffffffffffffffff)
OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 completed/failure)
run_case "tip ahead, its stage went red AFTER staging: skip (it is on the host)" true $TIP notice "Superseded: $TIP"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 none)
run_case "tip ahead, its build failed (deploy job skipped, no steps): deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 completed/skipped)
run_case "tip ahead, its stage step skipped (the build failed, or this guard skipped it): deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 completed/failure)
OUT[$(p_log 108212779155)]=$(unstaged_log)
run_case "tip ahead, its stage failed BEFORE staging: deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 none)
run_case "tip ahead, its deploy cancelled while pending (no steps ran): deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 completed/cancelled)
OUT[$(p_log 108212779155)]=$(unstaged_log)
run_case "tip ahead, its stage cancelled mid-step before staging: deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_jobs 36177655032)]=$(jobs_line 108212779155 completed/cancelled)
run_case "tip ahead, its stage cancelled mid-step after staging: skip" true $TIP notice "Superseded: $TIP"

base_incident1; OUT[$p_runs]="36177648282 $OLD
36094676883 $PREV"
run_case "tip ahead, no run found for it: deploy" false "" none "No master commit newer"

base_incident1; OUT[$p_runs]=""
run_case "tip ahead, no runs listed at all: deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_log 108212779155)]=$(no_restart_log $TIP)
run_case "a newer log with the staging line but no restart booked (not the code lane's signature): deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_log 108212779155)]=$(printf '%s\n' "2026-09-25T20:07:33.1Z [***] mirroring content into /opt/project1998/game-data" "2026-09-25T20:07:33.2Z [***] content reloaded")
run_case "a newer content-lane deploy (no release staged): deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_log 108212779155)]=$(echoed_script)
run_case "a newer log holding only the echoed scripts (both phrases, no host lines): deploy" false "" none "No master commit newer"

base_incident1; OUT[$(p_log 108212779155)]=$(other_sha_log $TIP $OLD)
run_case "a newer log whose host lines name another commit: deploy" false "" none "No master commit newer"

# The proof is pinned to the whole SHA, start to end of the line: a commit that shares all but the last
# character, or the SHA with anything after it, is another commit's line and proves nothing.
base_incident1; OUT[$(p_log 108212779155)]=$(staged_log "${TIP:0:39}$([ "${TIP:39:1}" = 0 ] && echo 1 || echo 0)")
run_case "a newer log naming a SHA that shares its first 39 characters: deploy" false "" none "No master commit newer"
base_incident1; OUT[$(p_log 108212779155)]=$(staged_log "${TIP}0")
run_case "a newer log naming the SHA with a character after it: deploy" false "" none "No master commit newer"

base_incident1; OUT[$p_runs]="99 $MID_PR
$RUNS"; OUT[$(p_jobs 99)]=""
run_case "a newer non-master commit with a run proves nothing by itself; the tip still does: skip" true $TIP notice "Superseded: $TIP"

base_incident1; OUT[$p_runs]="36177648282 $OLD
36094676883 $PREV"; OUT[$(p_jobs 36177648282)]=$(jobs_line 108212859455 completed/success)
OUT[$(p_log 108212859455)]=$(staged_log $OLD)
run_case "this commit's own earlier staging is not a newer one: deploy" false "" none "No master commit newer"

# ---- rule: every lookup that fails deploys, with a warning
base_incident1; RC[$p_tip]=1
run_case "tip lookup error: deploy, warn" false "" warning "master's tip could not be read"
base_incident1; OUT[$p_tip]='{"message":"Bad credentials","status":"401"}'
run_case "tip lookup answers an error body with exit 0: deploy, warn" false "" warning "master's tip could not be read"
base_incident1; OUT[$p_tip]=${TIP:0:12}
run_case "tip lookup answers a short SHA: deploy, warn" false "" warning "master's tip could not be read"
base_incident1; RC[$(p_cmp $OLD $TIP)]=1
run_case "compare error: deploy, warn" false "" warning "comparison with master's tip $TIP failed"
base_incident1; OUT[$(p_cmp $OLD $TIP)]='{"message":"Not Found"}'
run_case "compare answers something that is not a status: deploy, warn" false "" warning "answered"
base_incident1; OUT[$(p_cmp $OLD $TIP)]="ahead"
run_case "compare says ahead but lists no commits: deploy, warn" false "" warning "could not be read"
base_incident1; OUT[$(p_cmp $OLD $TIP)]="ahead
${TIP:0:7}"
run_case "compare lists a malformed commit: deploy, warn" false "" warning "could not be read"
base_incident1; SHA=${OLD:0:7}
run_case "malformed SHA for this run: deploy, warn" false "" warning "is not a full SHA"
base_incident1; SHA=""
run_case "empty SHA for this run: deploy, warn" false "" warning "is not a full SHA"
base_incident1; RC[$p_runs]=1
run_case "runs lookup error: deploy, warn" false "" warning "runs could not be listed"
base_incident1; OUT[$p_runs]="36177655032 not-a-sha"
run_case "a malformed run line: deploy, warn" false "" warning "was listed as"
base_incident1; RC[$(p_jobs 36177655032)]=1
run_case "jobs lookup error: deploy, warn" false "" warning "jobs of run 36177655032"
base_incident1; OUT[$(p_jobs 36177655032)]="null completed/success"
run_case "a malformed deploy job id: deploy, warn" false "" warning "deploy job of run 36177655032"
base_incident1; RC[$(p_log 108212779155)]=1
run_case "log lookup error on every try: deploy, warn" false "" warning "deploy log of run 36177655032"
[ "$(sleeps)" -eq 4 ] && [ "$(grep -cxF "$(p_log 108212779155)" "$state/calls")" -eq 5 ]
check "log lookup error: five tries, four waits between them" $? "$(sleeps) waits"
base_incident1; RC[$(p_log 108212779155)]="1 1 0"
run_case "log lookup fails twice, then answers: skip" true $TIP notice "Superseded: $TIP"
base_incident1; OUT[$(p_log 108212779155)]=""
run_case "log lookup answers an empty log: deploy, warn" false "" warning "deploy log of run 36177655032"

# An error on a newer run is not outweighed by a proof further down the list: the guard stops at the error.
base_incident1; OUT[$p_runs]="36999999999 $TIP
$RUNS"; RC[$(p_jobs 36999999999)]=1
run_case "an error before a proof still deploys" false "" warning "jobs of run 36999999999"

# ------------------------------------------------------------------------------------------------------------
# Incident 2 (2026-09-25 20:07): fad26aa (#291), 978e3d6 (#292), cdc2eeb (#293), a0f6645 (#294), merged 9 s
# apart. fad26aa's deploy started 20:07:37: cdc2eeb had staged at 20:07:33, 978e3d6's deploy was pending (it
# was cancelled at 20:07:47), and a0f6645, master's tip, was still building (its build ended 20:07:46).

I2_OLD=fad26aae5e29f8af1015e8ebe828ad540990f171   # run 36183611949, deploy job 108232382524
I2_978=978e3d68febfe03e32e07a9ea06c309c1b4b5f8b   # run 36183617521, deploy job 108232394619
I2_CDC=cdc2eebe5212cb687af661713418a10818ea341c   # run 36183622957, deploy job 108232297541
I2_TIP=a0f6645c71fad0fc873802e45342707cc20f7e00   # run 36183629245, deploy job 108232451012
I2_CMP="ahead
e62cf6e7511db0587f1c90552ae2f9d3b536b020
18095431ff748bf43ca0f366427248306d2bbd90
4ca4628cfd61e699935958e4d7452d4fb444de50
b9d094cd4354aa15539d52eba4064361e3d2a9e7
1d9d75d54de414b9a69069fe83747bddd79e9d1a
148253e7428b441a8c90a220e75e48c6f57ca4b1
2551abc89b48c0916e8d8f4ad7c45131c601dd98
028e6fe9c932ab08f580c792933ddbedd1238152
$I2_978
$I2_CDC
$I2_TIP"
base_incident2() {
    reset_api
    SHA=$I2_OLD
    OUT[$p_tip]=$I2_TIP
    OUT[$(p_cmp $I2_OLD $I2_TIP)]=$I2_CMP
    OUT[$p_runs]="36183629245 $I2_TIP
36183622957 $I2_CDC
36183617521 $I2_978
36183611949 $I2_OLD
36177655032 $TIP"
    OUT[$(p_jobs 36183629245)]=""                                               # still building
    OUT[$(p_jobs 36183622957)]=$(jobs_line 108232297541 completed/success)
    OUT[$(p_log 108232297541)]=$(staged_log $I2_CDC)
    OUT[$(p_jobs 36183617521)]=$(jobs_line 108232394619 none)                    # pending in the group
}
base_incident2
run_case "incident 2 replay: fad26aa skips, cdc2eeb already staged while the tip still builds" true $I2_CDC notice \
    "Superseded: $I2_CDC is newer than $I2_OLD .*run 36183622957"

# The rest of that burst. 978e3d6's deploy was cancelled while it waited; had it reached the group, it too
# should skip. a0f6645 is the tip and deploys.
base_incident2; SHA=$I2_978; OUT[$(p_cmp $I2_978 $I2_TIP)]="ahead
$I2_CDC
$I2_TIP"
run_case "incident 2: 978e3d6, had it run, skips too" true $I2_CDC notice "Superseded: $I2_CDC"
base_incident2; SHA=$I2_TIP
run_case "incident 2: a0f6645, the tip, deploys" false "" none "master's tip"

# The case the tip-run-state rule gets wrong: the tip's build fails after this deploy has looked. With
# cdc2eeb not yet staged either, this deploy must go ahead, or production keeps something older than fad26aa.
base_incident2; OUT[$(p_jobs 36183622957)]=""
run_case "incident 2 variant: nothing newer staged yet, tip still building: deploy" false "" none "No master commit newer"

# ------------------------------------------------------------------------------------------------------------
# A check killed mid-lookup, as the step's timeout-minutes kills it, writes no skip output, so every later step
# runs: the deploy goes ahead. This runs the step's whole script, marker to its $GITHUB_OUTPUT line, in a fresh
# bash with a gh on PATH that hangs, under a 1-second timeout. The control is the same script with a gh that
# answers at once, which does write its output.

awk '/^ *# >>> superseded-guard *\r?$/ {on = 1} on {print} on && /GITHUB_OUTPUT/ {exit}' "$ci" | tr -d '\r' > "$state/step.sh"
mkdir -p "$state/bin"
printf '#!/usr/bin/env bash\nsleep 5\n' > "$state/bin/gh"
chmod +x "$state/bin/gh"
: > "$state/ghout"
PATH="$state/bin:$PATH" SHA=$TIP REPO=$REPO GITHUB_OUTPUT="$state/ghout" timeout 1 bash "$state/step.sh" > /dev/null 2>&1
rc=$?
[ "$rc" -eq 124 ] && ! grep -q '^skip=' "$state/ghout"
check "a guard killed by its timeout writes no skip output (every later step runs)" $? "rc=$rc, output: $(tr '\n' '|' < "$state/ghout")"
printf '#!/usr/bin/env bash\necho %s\n' "$TIP" > "$state/bin/gh"
: > "$state/ghout"
PATH="$state/bin:$PATH" SHA=$TIP REPO=$REPO GITHUB_OUTPUT="$state/ghout" timeout 10 bash "$state/step.sh" > /dev/null 2>&1
rc=$?
[ "$rc" -eq 0 ] && grep -qx 'skip=false' "$state/ghout"
check "control: the same script, not killed, writes its output" $? "rc=$rc, output: $(tr '\n' '|' < "$state/ghout")"

echo
if [ "$fail" -eq 0 ]; then
    echo "PASS: $cases checks"
    exit 0
fi
echo "FAILED: $fail of $cases checks"
exit 1
