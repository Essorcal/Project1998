<#
.SYNOPSIS
Check whether a Project1998 issue is free to start, and claim it on the team's project board.

.DESCRIPTION
More than one developer pulls from the same board, and on 2026-09-03 an issue was already being
worked while its card still showed Open and unassigned. So "free" is decided from three sources,
not one: the issue's assignees and state, the board card's Status and assignees, and recent
comments on the issue ("working on this", "taking this", ...). Any one of them can mark it TAKEN.

Without -Claim the script only reports and sets the exit code. With -Claim on a FREE (or already
MINE) issue it moves the card to In progress, sets Start date to today, and assigns you, in that
order, so the board reflects the brief at the moment the brief goes out. -Release undoes a claim
(card back to Open, assignee removed) when a brief is cancelled.

The board's field and option ids are constants below (project "Main", users/project1998/projects/1).
gh needs the project scope:  gh auth refresh -s read:project,project

.EXAMPLE
Scripts\Board-Claim.ps1 -Issue 30            # report only
Scripts\Board-Claim.ps1 -Issue 30 -Claim     # claim it
Scripts\Board-Claim.ps1 -Issue 30 -Release   # undo

.OUTPUTS
Exit 0 = FREE, MINE, or claimed/released. Exit 1 = TAKEN. Exit 2 = error.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$Issue,
    [switch]$Claim,
    [switch]$Release,
    [string]$Assignee,
    [string]$Repo = 'project1998/Project1998',
    [string]$Owner = 'project1998',
    [int]$ProjectNumber = 1,
    [int]$RecentDays = 7
)

$ErrorActionPreference = 'Stop'

$ProjectId        = 'PVT_kwHOEsay2c4BiSJ-'
$StatusField      = 'PVTSSF_lAHOEsay2c4BiSJ-zhhKxag'
$StatusOpen       = 'f75ad846'
$StatusInProgress = '47fc9ee4'
$StartDateField   = 'PVTF_lAHOEsay2c4BiSJ-zhhKxdM'
$BusyStatuses     = @('In progress', 'Merged', 'QA', 'Done')
$ClaimWords       = '(working on|taking|picking (this|it) up|started (on )?this|on it|in progress|have this|got this|i''ll take)'

function Invoke-Gh {
    param([string[]]$GhArgs)
    $out = & gh @GhArgs
    if ($LASTEXITCODE -ne 0) { throw "gh $($GhArgs -join ' ') failed (exit $LASTEXITCODE)" }
    return ($out -join "`n")
}

try {
    if (-not $Assignee) { $Assignee = (Invoke-Gh @('api', 'user', '-q', '.login')).Trim() }

    $issueJson = Invoke-Gh @('issue', 'view', "$Issue", '-R', $Repo, '--json', 'number,title,state,url,assignees,labels,comments')
    $iss = $issueJson | ConvertFrom-Json

    $boardJson = Invoke-Gh @('project', 'item-list', "$ProjectNumber", '--owner', $Owner, '--limit', '200', '--format', 'json')
    $board = $boardJson | ConvertFrom-Json
    $card = $null
    foreach ($item in $board.items) {
        if ($item.content -and $item.content.number -eq $Issue -and ($item.content.repository -like "*$($Repo.Split('/')[1])")) { $card = $item; break }
    }

    $issueAssignees = @()
    if ($iss.assignees) { $issueAssignees = @($iss.assignees | ForEach-Object { $_.login }) }
    $cardAssignees = @()
    if ($card -and $card.assignees) { $cardAssignees = @($card.assignees) }
    $cardStatus = if ($card) { $card.status } else { '(not on board)' }

    $reasons = @()
    if ($iss.state -ne 'OPEN') { $reasons += "issue is $($iss.state)" }
    $others = @($issueAssignees | Where-Object { $_ -ne $Assignee })
    if ($others.Count -gt 0) { $reasons += "issue assigned to $($others -join ', ')" }
    $cardOthers = @($cardAssignees | Where-Object { $_ -ne $Assignee })
    if ($cardOthers.Count -gt 0) { $reasons += "card assigned to $($cardOthers -join ', ')" }
    $mine = ($issueAssignees -contains $Assignee) -or ($cardAssignees -contains $Assignee)
    if (($BusyStatuses -contains $cardStatus) -and -not $mine) { $reasons += "card status is '$cardStatus'" }

    $since = (Get-Date).AddDays(-$RecentDays)
    $recent = @()
    if ($iss.comments) {
        foreach ($c in $iss.comments) {
            $when = [datetime]$c.createdAt
            if ($when -lt $since) { continue }
            $who = $c.author.login
            $line = ($c.body -replace '\s+', ' ')
            if ($line.Length -gt 110) { $line = $line.Substring(0, 110) + '...' }
            $recent += "$($when.ToString('yyyy-MM-dd')) $who`: $line"
            if ($who -ne $Assignee -and $c.body -imatch $ClaimWords) { $reasons += "comment by $who on $($when.ToString('yyyy-MM-dd')) reads as a claim" }
        }
    }

    Write-Host "#$Issue  $($iss.title)"
    Write-Host "  url:             $($iss.url)"
    Write-Host "  issue state:     $($iss.state)"
    Write-Host "  issue assignees: $(if ($issueAssignees.Count) { $issueAssignees -join ', ' } else { '(none)' })"
    Write-Host "  labels:          $(($iss.labels | ForEach-Object { $_.name }) -join ' ')"
    Write-Host "  board status:    $cardStatus"
    Write-Host "  card assignees:  $(if ($cardAssignees.Count) { $cardAssignees -join ', ' } else { '(none)' })"
    Write-Host "  recent comments ($RecentDays d): $(if ($recent.Count) { '' } else { '(none)' })"
    foreach ($r in $recent) { Write-Host "    $r" }

    if ($Release) {
        if (-not $card) { throw "no board card for #$Issue to release" }
        Invoke-Gh @('project', 'item-edit', '--project-id', $ProjectId, '--id', $card.id, '--field-id', $StatusField, '--single-select-option-id', $StatusOpen) | Out-Null
        try { Invoke-Gh @('project', 'item-edit', '--project-id', $ProjectId, '--id', $card.id, '--field-id', $StartDateField, '--clear') | Out-Null } catch { Write-Host "  (start date not cleared: $($_.Exception.Message))" }
        if ($issueAssignees -contains $Assignee) { Invoke-Gh @('issue', 'edit', "$Issue", '-R', $Repo, '--remove-assignee', $Assignee) | Out-Null }
        Write-Host "VERDICT: RELEASED (card Open, $Assignee unassigned)"
        exit 0
    }

    if ($reasons.Count -gt 0) {
        Write-Host "VERDICT: TAKEN"
        foreach ($r in $reasons) { Write-Host "  - $r" }
        Write-Host "Pick something else, or ask Caleb if the signal is stale."
        exit 1
    }

    if (-not $Claim) {
        Write-Host ("VERDICT: " + $(if ($mine) { 'MINE (already claimed by ' + $Assignee + ')' } else { 'FREE' }))
        exit 0
    }

    if (-not $card) {
        $added = Invoke-Gh @('project', 'item-add', "$ProjectNumber", '--owner', $Owner, '--url', $iss.url, '--format', 'json') | ConvertFrom-Json
        $cardId = $added.id
        Write-Host "  added to board as $cardId"
    } else { $cardId = $card.id }

    Invoke-Gh @('project', 'item-edit', '--project-id', $ProjectId, '--id', $cardId, '--field-id', $StatusField, '--single-select-option-id', $StatusInProgress) | Out-Null
    Invoke-Gh @('project', 'item-edit', '--project-id', $ProjectId, '--id', $cardId, '--field-id', $StartDateField, '--date', (Get-Date -Format 'yyyy-MM-dd')) | Out-Null
    if (-not ($issueAssignees -contains $Assignee)) { Invoke-Gh @('issue', 'edit', "$Issue", '-R', $Repo, '--add-assignee', $Assignee) | Out-Null }
    Write-Host "VERDICT: CLAIMED (In progress, start $(Get-Date -Format 'yyyy-MM-dd'), assignee $Assignee)"
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 2
}
