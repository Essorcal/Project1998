#!/usr/bin/env bash
#
# The host-side half of the deploy. Piped in over SSH by .github/workflows/ci.yml and run on the VPS:
#
#   ssh user@host "bash -s -- <sha> <delay-minutes> <code-changed>" < deploy/host-deploy.sh
#
# It never restarts anything itself. For a code change it stages the new build and asks the RUNNING server to
# restart on its own schedule (data/restart_at), so players get the full warning ladder; for a content change
# it asks for a hot reload (data/reload_now) and nobody is kicked at all.
#
# LAYOUT ON THE HOST
#
#   /opt/nexus/
#     NexusServer.sln        <- empty marker file; this is what makes RepoPaths.Root() resolve to /opt/nexus,
#                               so both processes agree on ONE data/ directory regardless of where they start
#     current -> releases/<sha>
#     releases/<sha>/game/   <- published game binaries
#     releases/<sha>/login/  <- published login binaries
#     data/                  <- game content + nexus.db + chars/ + logs.  THE ONLY WRITABLE PATH.
#
# Release directories must NOT contain Server/ or Shared/ folders — those are the other RepoPaths.Root()
# marker, and one inside a release would make the root resolve to the release instead of /opt/nexus, giving
# each deploy its own empty data/ directory.

set -euo pipefail

SHA="${1:?usage: host-deploy.sh <sha> <delay-minutes> <code-changed>}"
DELAY="${2:-30}"
CODE_CHANGED="${3:-true}"

ROOT=/opt/nexus
RELEASES="$ROOT/releases"
RELEASE="$RELEASES/$SHA"
DATA="$ROOT/data"
TARBALL="/tmp/nexus-$SHA.tar.gz"
KEEP_RELEASES=5

log() { printf '[deploy] %s\n' "$*"; }

[ -f "$TARBALL" ] || { echo "[deploy] missing $TARBALL" >&2; exit 1; }

# ---- 1. unpack the release -------------------------------------------------------------------------------
log "unpacking $SHA"
rm -rf "$RELEASE"
mkdir -p "$RELEASE"
tar xzf "$TARBALL" -C "$RELEASE"
rm -f "$TARBALL"

# The root marker, in case this is a first deploy.
touch "$ROOT/NexusServer.sln"
mkdir -p "$DATA"

# ---- 2. sync content into the shared data directory ------------------------------------------------------
# The bundle carries data/ as committed. The host's copy also holds LIVE STATE that a deploy must never touch:
# the SQLite db (accounts, board posts), the per-player character store, and the logs. Hence the excludes —
# and hence no --delete, which would take chars/ with it.
if [ -d "$RELEASE/data" ]; then
  log "syncing content into $DATA"
  rsync -a \
    --exclude 'chars/' \
    --exclude 'nexus.db' --exclude 'nexus.db-wal' --exclude 'nexus.db-shm' --exclude 'nexus.db-journal' \
    --exclude '*.log' --exclude '*.log.*' \
    --exclude 'restart_at' --exclude 'reload_now' \
    "$RELEASE/data/" "$DATA/"
  # The shipped copy has served its purpose; drop it so a release dir is just binaries and `du` stays honest.
  rm -rf "$RELEASE/data"
fi

# ---- 3. the two lanes ------------------------------------------------------------------------------------
if [ "$CODE_CHANGED" != "true" ]; then
  # CONTENT LANE. The running binaries are already correct; only the CSVs and Lua moved. Ask for a hot
  # reload and leave the world up. The symlink is deliberately NOT flipped — there is no new build to flip to.
  log "content-only change — requesting hot reload, no restart"
  : > "$DATA/reload_now"
  rm -rf "$RELEASE"
  exit 0
fi

# CODE LANE. Flip the symlink NOW, restart LATER. Swapping the pointer under a running process is safe: the
# live server already has its assemblies mapped and keeps running the old inode until it exits. That ordering
# is what makes the restart itself the deploy, and it means a crash during the warning window comes back up on
# the new build rather than the old one.
log "staging release $SHA"
ln -sfn "$RELEASE" "$ROOT/current"

# Ask the RUNNING game server to restart on its own schedule. RestartSchedule polls this file every ~6s,
# consumes it, and announces at 30/20/15/10/5/2/1 minutes before exiting into systemd's Restart=always.
# An absolute deadline, not a countdown — see RestartSchedule's class doc.
DEADLINE=$(( ($(date +%s) + $(awk "BEGIN{printf \"%d\", $DELAY * 60}")) * 1000 ))
printf '%s|deploying %s\n' "$DEADLINE" "${SHA:0:7}" > "$DATA/restart_at"
log "game restart scheduled in ${DELAY}m (deadline ${DEADLINE})"

# The login server has no players to warn, but it must NOT be left running an older build than the game —
# the handoff packet is a contract between the two. Restart it on the same deadline via a transient timer.
# Requires a sudoers entry; see deploy/README.md §3.
if [ "${DELAY%.*}" -gt 0 ] 2>/dev/null; then
  sudo systemd-run --on-active="${DELAY}m" --unit="nexus-login-redeploy" \
    systemctl restart nexus-login >/dev/null
else
  sudo systemctl restart nexus-login
fi
log "login restart armed"

# ---- 4. prune ---------------------------------------------------------------------------------------------
# Keep the last few releases so a rollback is a symlink flip, not a rebuild. Never delete what `current`
# points at, even if it somehow falls outside the newest N.
CURRENT_TARGET=$(readlink -f "$ROOT/current" || true)
# shellcheck disable=SC2012
ls -1dt "$RELEASES"/*/ 2>/dev/null | tail -n +$((KEEP_RELEASES + 1)) | while read -r old; do
  [ "$(readlink -f "$old")" = "$CURRENT_TARGET" ] && continue
  log "pruning $(basename "$old")"
  rm -rf "$old"
done

log "done"
