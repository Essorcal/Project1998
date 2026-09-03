<#
.SYNOPSIS
Put a worker clone on a fresh pr/* branch from upstream master, guarded, with the branch already on the fork.

.DESCRIPTION
Each worker (Claude, Fable, Codex) has its own standalone clone, e.g. C:\Repo\NexusTK-claude and
C:\Repo\NexusTK-codex, with remotes origin (the Essorcal fork) and upstream (project1998). The
coordinator runs this once per brief, before the worker session opens, because the worker itself is
not allowed to switch branches (worker_guard blocks checkout/switch in worker mode).

What it does, in order:
  1. Refuses if the clone has uncommitted changes, or if its current branch has commits that are on
     neither the fork nor upstream master. Nothing is ever discarded here.
  2. Installs the worker_guard overlay (.claude/settings.local.json) if it is missing or stale.
  3. Fetches origin and upstream, then `git checkout -B <Branch> <Base>` and `git push -u origin
     <Branch>`, so the branch exists on the fork from the first commit and a bare `git push` works.
  4. Writes <clone>\.git\guard-mode = worker.

-SetMode alone flips the guard mode (worker | review | off) without touching branches. The
coordinator uses -SetMode review at the cross-review milestone and -SetMode worker afterwards.
-DryRun runs every check and prints what step 3 would do, without changing anything.
-Switch <branch> moves the clone to a branch it already has (a fix round on an earlier PR) with the
same clean-and-pushed checks and without -B, so no commits can be lost.

Close any agent session that has the clone open before running with -Branch. A live cwd holds the
directory; the only reliable closed-session test is that a rename of the directory succeeds.

.EXAMPLE
Scripts\Prep-WorkerClone.ps1 -Clone C:\Repo\NexusTK-claude -Branch pr/move-under-lock
Scripts\Prep-WorkerClone.ps1 -Clone C:\Repo\NexusTK-codex -SetMode review
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Clone,
    [string]$Branch,
    [string]$Base = 'upstream/master',
    [ValidateSet('worker', 'review', 'off')][string]$SetMode,
    [string]$Switch,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$Overlay = Join-Path $env:USERPROFILE '.claude\hooks\worker.settings.local.json'

function Invoke-Git {
    param([string[]]$GitArgs)
    $out = & git -C $Clone @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed (exit $LASTEXITCODE)" }
    return ($out -join "`n")
}

try {
    if (-not (Test-Path (Join-Path $Clone '.git'))) { throw "$Clone is not a git checkout" }
    $gitDir = (Invoke-Git @('rev-parse', '--git-dir')).Trim()
    if (-not [System.IO.Path]::IsPathRooted($gitDir)) { $gitDir = Join-Path $Clone $gitDir }
    $originUrl = (Invoke-Git @('remote', 'get-url', 'origin')).Trim()
    $upstreamUrl = (Invoke-Git @('remote', 'get-url', 'upstream')).Trim()
    Write-Host "clone:    $Clone"
    Write-Host "origin:   $originUrl"
    Write-Host "upstream: $upstreamUrl"

    # 2. guard overlay
    $target = Join-Path $Clone '.claude\settings.local.json'
    if (-not (Test-Path $Overlay)) { throw "guard overlay template missing: $Overlay" }
    if (-not (Test-Path $target) -or ((Get-Content $Overlay -Raw) -ne (Get-Content $target -Raw))) {
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $Overlay $target -Force
        Write-Host "guard:    overlay installed at $target"
    } else { Write-Host "guard:    overlay present" }

    if ($SetMode) {
        Set-Content -Path (Join-Path $gitDir 'guard-mode') -Value $SetMode -Encoding ascii
        Write-Host "mode:     $SetMode"
        if (-not $Branch) { exit 0 }
    }
    if ($Switch) {
        # Move a worker back to a branch it already has (a fix round on an earlier PR). Never -B: that would
        # reset the branch to the base and lose its commits. Refuses unless the branch exists locally and the
        # clone is clean with its current branch on the fork.
        & git -C $Clone rev-parse --verify --quiet "refs/heads/$Switch" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "-Switch: branch '$Switch' does not exist in $Clone" }
        $dirty = Invoke-Git @('status', '--porcelain')
        if ($dirty.Trim()) { throw "clone is dirty; commit or hand-clean first:`n$dirty" }
        $cur = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
        Invoke-Git @('fetch', 'origin') | Out-Null
        & git -C $Clone rev-parse --verify --quiet "origin/$cur" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "current branch '$cur' is not on the fork; push it first" }
        $ahead = [int](Invoke-Git @('rev-list', '--count', "origin/$cur..HEAD")).Trim()
        if ($ahead -ne 0) { throw "current branch '$cur' has $ahead unpushed commit(s); push first" }
        Invoke-Git @('checkout', $Switch) | Out-Null
        Set-Content -Path (Join-Path $gitDir 'guard-mode') -Value 'worker' -Encoding ascii
        Write-Host "switched: $cur -> $Switch @ $((Invoke-Git @('rev-parse', '--short', 'HEAD')).Trim())"
        Write-Host "mode:     worker"
        exit 0
    }
    if (-not $Branch) { throw 'give -Branch pr/<slug>, -Switch <existing branch>, or -SetMode alone' }
    if ($Branch -notmatch '^pr/[a-z0-9][a-z0-9-]*$') { throw "branch '$Branch' does not match pr/<kebab-slug>" }

    # 1. nothing to lose
    $dirty = Invoke-Git @('status', '--porcelain')
    if ($dirty.Trim()) { throw "clone is dirty; commit or hand-clean first:`n$dirty" }
    Invoke-Git @('fetch', 'origin') | Out-Null
    Invoke-Git @('fetch', 'upstream') | Out-Null
    $current = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
    $head = (Invoke-Git @('rev-parse', 'HEAD')).Trim()
    & git -C $Clone merge-base --is-ancestor $head $Base 2>$null
    $inBase = ($LASTEXITCODE -eq 0)
    $onFork = $false
    if ($current -ne 'HEAD') {
        & git -C $Clone rev-parse --verify --quiet "origin/$current" | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $ahead = [int](Invoke-Git @('rev-list', '--count', "origin/$current..HEAD")).Trim()
            $onFork = ($ahead -eq 0)
        }
    }
    if (-not ($inBase -or $onFork)) {
        throw "current branch '$current' ($($head.Substring(0,7))) has commits that are on neither $Base nor origin/$current; push or merge them first"
    }
    Write-Host "leaving:  $current @ $($head.Substring(0,7)) ($(if ($inBase) { "merged into $Base" } else { 'on the fork' }))"

    # 3. new branch, on the fork from the start
    $baseSha = (Invoke-Git @('rev-parse', $Base)).Trim()
    if ($DryRun) {
        Write-Host "dry-run:  would checkout -B $Branch $Base ($($baseSha.Substring(0,7))), push -u origin $Branch, set mode worker"
        exit 0
    }
    Invoke-Git @('checkout', '-B', $Branch, $Base) | Out-Null
    Invoke-Git @('push', '-u', 'origin', $Branch) | Out-Null
    Set-Content -Path (Join-Path $gitDir 'guard-mode') -Value 'worker' -Encoding ascii
    Write-Host "branch:   $Branch @ $($baseSha.Substring(0,7)) (= $Base), tracking origin/$Branch"
    Write-Host "mode:     worker"
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 2
}
