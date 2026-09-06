<#
.SYNOPSIS
Prepare a claimed, clean clone without replacing existing branches.
.DESCRIPTION
Run this canonical copy from C:\Repo\NexusTK, after Workflow.ps1 claim.
The registry verifies the assignment owner, exact checkout, remotes, dependencies and ports.
New branches require a clean checkout whose current HEAD is on the base or the fork.
Existing branch names are refused; use -Switch to resume one. -DryRun changes nothing,
including refs, guard files and remotes; its checks use cached remote refs.
Explicit -Base origin/main supports the test-client repo, which has no upstream remote.
Use -GuardProfile None for Codex; Claude hooks do not enforce Codex actions.
-SetMode alone flips the clone's guard mode (worker | review | off) and touches nothing else:
no claim, no registry preflight, no branch, no hook overlay. That is the cross-review milestone
(the xreview skill's -SetMode review / -SetMode worker) and the coordinator-only escape hatch the
worker_guard hook names (-SetMode off). -AssignmentId and -Owner are required only when -Branch or
-Switch prepares a branch; given with -SetMode they are verified against the registry as well.
.EXAMPLE
Scripts\Prep-WorkerClone.ps1 -Clone C:\Repo\Project1998\NexusTK-codex -AssignmentId server-101-r1 -Owner sprint7 -Branch pr/outbound-drain -GuardProfile None
.EXAMPLE
Scripts\Prep-WorkerClone.ps1 -Clone C:\Repo\Project1998\NexusTK-review -SetMode review
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Clone,
    [string]$AssignmentId,
    [string]$Owner,
    [string]$Registry = (Join-Path (Split-Path $PSScriptRoot -Parent) '..\Project1998\workflow\registry.json'),
    [string]$Branch,
    [string]$Base = 'upstream/master',
    [string]$Switch,
    [ValidateSet('worker', 'review', 'off')][string]$SetMode,
    [ValidateSet('Claude', 'None')][string]$GuardProfile = 'Claude',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param([string[]]$GitArgs)
    $out = & git -c "safe.directory=$Clone" -C $Clone @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed ($LASTEXITCODE)" }
    return ($out -join "`n").Trim()
}

try {
    if (($Branch -and $Switch) -or (-not $Branch -and -not $Switch -and -not $SetMode)) {
        throw 'Give -Branch, -Switch, or -SetMode; -Branch and -Switch are mutually exclusive.'
    }
    if ($Branch -and $Branch -notmatch '^pr/[a-z0-9][a-z0-9-]*$') {
        throw 'New branches must be named pr/<kebab-slug>.'
    }
    $Clone = (Resolve-Path -LiteralPath $Clone).ProviderPath.TrimEnd('\')
    $modeOnly = -not $Branch -and -not $Switch
    if (($AssignmentId -and -not $Owner) -or ($Owner -and -not $AssignmentId)) {
        throw '-AssignmentId and -Owner go together.'
    }
    if (-not $modeOnly -and -not $AssignmentId) {
        throw 'Preparing a branch (-Branch or -Switch) needs -AssignmentId and -Owner from Workflow.ps1 claim. A mode flip (-SetMode alone) does not.'
    }
    $preflightRan = $false
    if ($AssignmentId) {
        $preflightRan = $true
        & (Join-Path $PSScriptRoot 'Workflow.ps1') --registry $Registry preflight --resource (Split-Path $Clone -Leaf) --checkout $Clone --id $AssignmentId --owner $Owner
        if ($LASTEXITCODE -ne 0) { throw 'Assignment preflight failed; nothing was prepared.' }
    }
    $gitDir = Invoke-Git @('rev-parse', '--absolute-git-dir')
    if ($modeOnly) {
        if ($DryRun) {
            Write-Host "Dry run: would set $Clone guard mode to $SetMode (currently $(if (Test-Path -LiteralPath (Join-Path $gitDir 'guard-mode')) { (Get-Content -LiteralPath (Join-Path $gitDir 'guard-mode') -Raw).Trim() } else { 'worker (no file)' })). Nothing else would change."
            exit 0
        }
        Set-Content -LiteralPath (Join-Path $gitDir 'guard-mode') -Value $SetMode -Encoding ascii
        Write-Host "Mode: $Clone guard mode set to $SetMode. Branch, hooks and registry untouched."
        exit 0
    }
    $target = Join-Path $Clone '.claude\settings.local.json'
    $overlay = Join-Path $env:USERPROFILE '.claude\hooks\worker.settings.local.json'
    if ($GuardProfile -eq 'Claude' -and -not (Test-Path -LiteralPath $overlay)) {
        throw "Claude guard template missing: $overlay"
    }
    if ($Branch) {
        & git -c "safe.directory=$Clone" -C $Clone show-ref --verify --quiet "refs/heads/$Branch"
        if ($LASTEXITCODE -eq 0) { throw "Branch $Branch already exists; use -Switch. Nothing is reset." }
    }
    if ($Switch) { $null = Invoke-Git @('rev-parse', '--verify', "refs/heads/$Switch") }
    if ($Branch -or $Switch) {
        if (-not $DryRun) {
            $null = Invoke-Git @('fetch', 'origin')
            if (((Invoke-Git @('remote')) -split "\n") -contains 'upstream') {
                $null = Invoke-Git @('fetch', 'upstream')
            }
        }
        $head = Invoke-Git @('rev-parse', 'HEAD')
        $current = Invoke-Git @('branch', '--show-current')
        $inBase = $false
        if ($Branch) {
            $baseSha = Invoke-Git @('rev-parse', '--verify', "$Base^{commit}")
            & git -c "safe.directory=$Clone" -C $Clone merge-base --is-ancestor $head $baseSha
            $inBase = $LASTEXITCODE -eq 0
            & git -c "safe.directory=$Clone" -C $Clone show-ref --verify --quiet "refs/remotes/origin/$Branch"
            if ($LASTEXITCODE -eq 0) { throw "Origin already has $Branch; choose a new branch or fetch and resume it." }
        }
        $onFork = $false
        if ($current) {
            & git -c "safe.directory=$Clone" -C $Clone show-ref --verify --quiet "refs/remotes/origin/$current"
            if ($LASTEXITCODE -eq 0) {
                $onFork = (Invoke-Git @('rev-list', '--count', "origin/$current..HEAD")) -eq '0'
            }
        }
        if (-not ($inBase -or $onFork)) { throw 'Current HEAD is neither on the requested base nor fully pushed to its fork branch. Preserve it before switching.' }
    }
    if ($DryRun) {
        Write-Host "Dry run: assignment verified; would prepare $Clone (branch=$Branch switch=$Switch mode=$SetMode guard=$GuardProfile). Cached remote refs only; no files or refs changed."
        exit 0
    }
    if ($Branch) {
        $null = Invoke-Git @('checkout', '-b', $Branch, $baseSha)
        $null = Invoke-Git @('push', '-u', 'origin', $Branch)
    } elseif ($Switch) {
        $null = Invoke-Git @('checkout', $Switch)
    }
    if ($GuardProfile -eq 'Claude') {
        if (-not (Test-Path -LiteralPath $target) -or (Get-Content -LiteralPath $overlay -Raw) -ne (Get-Content -LiteralPath $target -Raw)) {
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item -LiteralPath $overlay -Destination $target -Force
        }
        # Local integration config must not make a newly prepared test-client clone dirty.
        $exclude = Join-Path $gitDir 'info\exclude'
        New-Item -ItemType Directory -Force (Split-Path $exclude) | Out-Null
        $rule = '/.claude/settings.local.json'
        if (-not (Test-Path -LiteralPath $exclude) -or -not ((Get-Content -LiteralPath $exclude) -contains $rule)) {
            Add-Content -LiteralPath $exclude -Value $rule -Encoding ascii
        }
    }
    $mode = if ($SetMode) { $SetMode } else { 'worker' }
    Set-Content -LiteralPath (Join-Path $gitDir 'guard-mode') -Value $mode -Encoding ascii
    Write-Host "Prepared: $Clone @ $(Invoke-Git @('rev-parse', '--short', 'HEAD')); assignment=$AssignmentId mode=$mode"
    Write-Host 'Re-run preflight with the expected branch before dispatch; the assignment remains claimed until release.'
    exit 0
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    if ($preflightRan) { Write-Host 'The claim is retained. Inspect any partial preparation before retrying; no automatic rollback or reset is performed.' }
    exit 2
}
