<#
.SYNOPSIS
Start, inspect or stop a local NexusTK server pair, with the checkout and commit stamped on it.

.DESCRIPTION
run-server.bat starts the login and game servers as two visible consoles and records nothing about
which checkout or commit it launched; every window is titled the same, and the tiers a bot needs come
from state/ files or from whatever P1998_TESTERS / P1998_GMS the launching shell happened to have. On a
machine where several clones and several people (or agents) share the one port pair, that leaves four
questions to answer by hand before anyone can use a server: is one up, whose is it, which commit is it,
and what tiers does it grant.

This script performs the same launch as the bat, and answers those questions:

  * It refuses to start while the login or game ports are held, and says who holds them (PID, command
    line, and the session file's checkout/commit/start time when the holder was started by this script).
    It never stops anything by itself.
  * Each console is titled "LOGIN 2000/2001 - <clone> @ <short commit>" / "GAME 2005/2006 - ...", so the
    window says what it is. A "+" after the commit means the working tree had uncommitted changes.
  * -Testers / -Gms go into the environment of the launched processes only (as P1998_TESTERS and
    P1998_GMS, which the game server unions with state/*_accounts.txt). The calling shell is untouched.
  * It writes run/session.json in the checkout: { pid_login, pid_game, checkout, commit, branch, ports,
    testers, gms, started }. -Status reads it back; -Stop closes exactly those two processes, waits for
    the ports to free, and removes it.

It is not a resident process: it launches the consoles and exits, and the consoles stay visible, which
is the rule this repo runs by. Nothing here runs a server hidden or in the background.

HOW THE LAUNCH WORKS. Each console runs a tiny generated batch file (run/serve-login.cmd, run/serve-game.cmd)
under "cmd /k". The batch sets the title and the environment, then runs the same "dotnet run --no-build"
line as run-server.bat. Two reasons for the batch rather than one long "cmd /k" command line: cmd appends
' - "<command line>"' to the window title while a command typed at its prompt runs, but not while a batch
file runs, so this is what keeps the title readable; and it puts the tier variables in the console's
environment without ever setting them in the caller's shell.

WHICH PIDS ARE RECORDED. The processes that actually hold the ports (LoginServer.exe / Server.exe, the
apphosts that "dotnet run" starts), because those are what -Status must compare the port owners against
and what -Stop must be sure it is closing. Each is verified to be a descendant of the console this script
opened before it is written down.

HOW -Stop CLOSES THEM. Ctrl+C first, not TerminateProcess: the game server's Ctrl+C / ProcessExit handler
(Server/Net.cs) flushes connected players before exiting, and a hard kill would skip it. The signal is
delivered by a short-lived helper powershell that attaches to the server's console (the caller's own
console must not be detached for this; that would break an interactive shell). If a process is still
alive after 20 s it is terminated. The cmd window that hosted the batch is then closed too -- after the
server exits it is only a prompt sitting at "Terminate batch job (Y/N)?".

PORTS. Login binds PortBase and PortBase+1 (4.95 / 5.33), game binds PortBase+5 and PortBase+6, which is
run-server.bat's 2000/2001 + 2005/2006 at the default. -PortBase is accepted now so the script does not
change shape later, but until Shared/ChannelPorts derives the handoff from the login port (#84) only the
default base gives a working handoff: a login on 3000 still redirects clients to 2005.

.PARAMETER Checkout
The clone to run, inspect or stop. Defaults to the repository this script lives in.

.PARAMETER Testers
Account names to grant the tester tier for this run (P1998_TESTERS in the launched processes only).

.PARAMETER Gms
Account names to grant the GM tier for this run (P1998_GMS in the launched processes only).

.PARAMETER PortBase
First login port. Default 2000. See PORTS above.

.PARAMETER Status
Report what is running: the session file if its processes are alive, otherwise "nothing running" plus
any process listening on the ports (PID, command line, and its checkout when it can be inferred).

.PARAMETER Stop
Close the two processes named in the checkout's run/session.json, wait for the ports to free, and delete
the file. Anything not named in the file is left alone.

.EXAMPLE
Scripts\Serve.ps1 -Checkout C:\Repo\NexusTK-codex -Testers botone -Gms botone

.EXAMPLE
Scripts\Serve.ps1 -Status

.EXAMPLE
Scripts\Serve.ps1 -Checkout C:\Repo\NexusTK-codex -Stop

.NOTES
Windows PowerShell 5.1 compatible. Exit codes: 0 done; 1 usage, build or launch failure (or nothing to
stop); 2 refused because the ports are held or this checkout's pair is already running.
#>
[CmdletBinding()]
param(
    [string]$Checkout,
    [string[]]$Testers = @(),
    [string[]]$Gms = @(),
    [ValidateRange(1024, 65000)]
    [int]$PortBase = 2000,
    [switch]$Status,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'

$SessionRel  = 'run\session.json'
$LoginBatRel = 'run\serve-login.cmd'
$GameBatRel  = 'run\serve-game.cmd'

# ---------------------------------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------------------------------

function Resolve-Checkout([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { $Path = Split-Path -Parent $PSScriptRoot }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Checkout not found: $Path"
    }
    $full = (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $full 'Project1998.sln') -PathType Leaf)) {
        throw "Not a Project1998 checkout (no Project1998.sln): $full"
    }
    return $full
}

function Get-PortPlan([int]$Base) {
    return [pscustomobject]@{ Login = @(($Base), ($Base + 1)); Game = @(($Base + 5), ($Base + 6)) }
}

function Get-Proc([int]$ProcessId) {
    if ($ProcessId -le 0) { return $null }
    $p = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $null }
    return $p
}

function Test-Alive([int]$ProcessId) {
    if ($ProcessId -le 0) { return $false }
    return ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue))
}

# Every (port, owning PID) pair currently LISTENING on any of the given ports.
function Get-Listeners([int[]]$Ports) {
    $conns = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
               Where-Object { $Ports -contains [int]$_.LocalPort })
    $seen = @{}
    foreach ($c in $conns) {
        $key = "$($c.LocalPort)/$($c.OwningProcess)"
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = [pscustomobject]@{ Port = [int]$c.LocalPort; ProcessId = [int]$c.OwningProcess }
        }
    }
    return @($seen.Values | Sort-Object Port, ProcessId)
}

function Get-ShortCommit([string]$Commit) {
    if ([string]::IsNullOrWhiteSpace($Commit)) { return 'unknown' }
    if ($Commit.Length -gt 7) { return $Commit.Substring(0, 7) }
    return $Commit
}

function Get-GitInfo([string]$Root) {
    $ErrorActionPreference = 'Continue'   # git writes to stderr on a non-repo; that must not be fatal here
    $info = [ordered]@{ Commit = 'unknown'; Short = 'unknown'; Branch = 'unknown'; Dirty = $false }
    if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) { return [pscustomobject]$info }
    $c = & git -C $Root rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $c) { $info.Commit = ([string]$c).Trim(); $info.Short = Get-ShortCommit $info.Commit }
    $b = & git -C $Root rev-parse --abbrev-ref HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $b) { $info.Branch = ([string]$b).Trim() }
    $d = @(& git -C $Root status --porcelain --untracked-files=no 2>$null)
    if ($LASTEXITCODE -eq 0) { $info.Dirty = ($d.Count -gt 0) }
    return [pscustomobject]$info
}

# The deployment root a running process belongs to: walk up from its executable until a directory holding
# a .sln, the same rule Shared/RepoPaths.Root() applies from inside the process.
function Get-ProcessCheckout($Proc) {
    if ($null -eq $Proc -or [string]::IsNullOrWhiteSpace($Proc.ExecutablePath)) { return $null }
    $dir = Split-Path -Parent $Proc.ExecutablePath
    while (-not [string]::IsNullOrEmpty($dir)) {
        $sln = @(Get-ChildItem -LiteralPath $dir -Filter '*.sln' -File -ErrorAction SilentlyContinue)
        if ($sln.Count -gt 0) { return $dir }
        $dir = Split-Path -Parent $dir
    }
    return $null
}

function Read-Session([string]$Root) {
    $file = Join-Path $Root $SessionRel
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    try {
        return (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json)
    } catch {
        Write-Warning "Unreadable session file $file ($($_.Exception.Message))"
        return $null
    }
}

function Write-Session([string]$Root, $Doc) {
    $file = Join-Path $Root $SessionRel
    $dir = Split-Path -Parent $file
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $json = $Doc | ConvertTo-Json -Depth 4
    # No BOM: the file is meant to be read by other tools (a test client, another script), and a UTF-8
    # BOM is the one thing a plain json parser reliably rejects.
    [System.IO.File]::WriteAllText($file, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding $false))
    return $file
}

function Remove-SessionFiles([string]$Root) {
    foreach ($rel in @($SessionRel, $LoginBatRel, $GameBatRel)) {
        $f = Join-Path $Root $rel
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    }
}

# PID-reuse guard for -Status and -Stop: a PID from the session file only counts as ours if it is still a
# Project1998 server binary running out of the session's checkout.
function Test-SessionServer($Proc, [string]$Root) {
    if ($null -eq $Proc) { return $false }
    if ($Proc.Name -ine 'Server.exe' -and $Proc.Name -ine 'LoginServer.exe') { return $false }
    $exe = $Proc.ExecutablePath
    if ([string]::IsNullOrWhiteSpace($exe)) { $exe = $Proc.CommandLine }
    if ([string]::IsNullOrWhiteSpace($exe)) { return $false }
    return $exe.StartsWith($Root + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

# Walk the parent chain from a process. Stops at a parent that was created AFTER its child, which is the
# signature of a reused PID. Returns the ancestors, nearest first.
function Get-Ancestors([int]$ProcessId, [int]$MaxHops = 8) {
    $out = @()
    $cur = Get-Proc $ProcessId
    $hops = 0
    while ($null -ne $cur -and $hops -lt $MaxHops) {
        $parent = Get-Proc $cur.ParentProcessId
        if ($null -eq $parent) { break }
        if ($parent.CreationDate -gt $cur.CreationDate) { break }
        $out += $parent
        $cur = $parent
        $hops++
    }
    return @($out)
}

function Test-Descendant([int]$ProcessId, [int]$AncestorId) {
    foreach ($a in (Get-Ancestors $ProcessId)) { if ([int]$a.ProcessId -eq $AncestorId) { return $true } }
    return $false
}

# The cmd.exe that is running one of our generated batch files, found above a server process.
function Find-ConsoleHost([int]$ProcessId, [string]$BatchPath) {
    foreach ($a in (Get-Ancestors $ProcessId)) {
        if ($a.Name -ieq 'cmd.exe' -and -not [string]::IsNullOrEmpty($a.CommandLine) -and
            $a.CommandLine.IndexOf($BatchPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $a
        }
    }
    return $null
}

# One block per listening process (a server holds two ports), with what can be learned about it.
function Format-Listeners([object[]]$Listeners) {
    $byPid = @{}
    $order = @()
    foreach ($l in @($Listeners)) {
        $id = [int]$l.ProcessId
        if (-not $byPid.ContainsKey($id)) { $byPid[$id] = @(); $order += $id }
        $byPid[$id] += [int]$l.Port
    }
    $lines = @()
    foreach ($id in $order) {
        $ports = @($byPid[$id] | Sort-Object) -join '/'
        $p = Get-Proc $id
        if ($null -eq $p) {
            $lines += "  port $ports`: PID $id (process already gone)"
            continue
        }
        $lines += "  port $ports`: PID $($p.ProcessId) $($p.Name), started $($p.CreationDate.ToString('yyyy-MM-dd HH:mm:ss'))"
        $lines += "      $($p.CommandLine)"
        $root = Get-ProcessCheckout $p
        if ($null -ne $root) {
            $s = Read-Session $root
            if ($null -ne $s -and ([int]$s.pid_login -eq [int]$p.ProcessId -or [int]$s.pid_game -eq [int]$p.ProcessId)) {
                $lines += "      session: $($s.checkout) ($($s.branch) @ $(Get-ShortCommit $s.commit)), started $($s.started)"
                $lines += "      testers: [$(@($s.testers) -join ', ')]  gms: [$(@($s.gms) -join ', ')]"
            } else {
                $lines += "      checkout: $root (no session file - started by run-server.bat or by hand)"
            }
        }
    }
    return $lines
}

function Split-Names([string[]]$Names) {
    $out = @()
    foreach ($n in @($Names)) {
        foreach ($part in ([string]$n).Split(',')) {
            $t = $part.Trim()
            if ($t.Length -gt 0 -and -not ($out -contains $t)) { $out += $t }
        }
    }
    return @($out)
}

# ---------------------------------------------------------------------------------------------------
# dotnet: the same probe order as run-server.bat, minus the installer. A dotnet.exe only counts if it
# reports an 8.x SDK; a runtime-only install answers `where dotnet` and then cannot build.
# ---------------------------------------------------------------------------------------------------

function Test-DotnetSdk([string]$Exe) {
    if ([string]::IsNullOrWhiteSpace($Exe)) { return $false }
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { return $false }
    $ErrorActionPreference = 'Continue'
    $sdks = @(& $Exe --list-sdks 2>$null)
    if ($LASTEXITCODE -ne 0) { return $false }
    foreach ($s in $sdks) { if ([string]$s -match '^8\.') { return $true } }
    return $false
}

function Find-Dotnet([string]$Root) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:P1998_DOTNET)) { $candidates += $env:P1998_DOTNET }
    $candidates += (Join-Path $Root '.dotnet\dotnet.exe')
    foreach ($c in @(Get-Command dotnet.exe -All -ErrorAction SilentlyContinue)) { $candidates += $c.Source }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    foreach ($c in $candidates) {
        if (Test-DotnetSdk $c) { return (Resolve-Path -LiteralPath $c).ProviderPath }
    }
    return $null
}

# ---------------------------------------------------------------------------------------------------
# Launch pieces
# ---------------------------------------------------------------------------------------------------

function Write-LaunchBatch([string]$Path, [string]$Title, [string]$Dotnet, [string]$Project, [int[]]$Ports,
                           [string[]]$TesterNames, [string[]]$GmNames) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $lines = @(
        '@echo off',
        'rem Generated by Scripts\Serve.ps1. Rewritten on every start, removed by -Stop.',
        "title $Title",
        'set "DOTNET_NOLOGO=1"',
        'set "DOTNET_GENERATE_ASPNET_CERTIFICATE=0"',
        "set `"DOTNET_ROOT=$(Split-Path -Parent $Dotnet)`""
    )
    if (@($TesterNames).Count -gt 0) { $lines += "set `"P1998_TESTERS=$($TesterNames -join ',')`"" }
    if (@($GmNames).Count -gt 0)     { $lines += "set `"P1998_GMS=$($GmNames -join ',')`"" }
    $lines += "`"$Dotnet`" run --no-build --project `"$Project`" -- --ports $($Ports -join ',')"
    Set-Content -LiteralPath $Path -Value $lines -Encoding Oem
}

# A visible console running the batch. "/s /k" with the path double-quoted twice is cmd's documented way
# to keep a quoted path intact whatever characters it contains.
function Start-Console([string]$BatchPath, [string]$WorkingDirectory) {
    $args = "/s /k `"`"$BatchPath`"`""
    return (Start-Process -FilePath $env:ComSpec -ArgumentList $args -WorkingDirectory $WorkingDirectory -PassThru)
}

# Wait for a listener on the port that descends from our console. Result.Outcome is one of:
#   'ok'       Result.ProcessId is the server
#   'stranger' something else took the port first (Result.ProcessId)
#   'exited'   our console's command finished without binding it - read the window
#   'timeout'
function Wait-ForListener([int]$Port, [int]$ConsolePid, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $startedAt = Get-Date
    while ((Get-Date) -lt $deadline) {
        foreach ($l in (Get-Listeners @($Port))) {
            if (Test-Descendant -ProcessId $l.ProcessId -AncestorId $ConsolePid) {
                return [pscustomobject]@{ Outcome = 'ok'; ProcessId = $l.ProcessId }
            }
            return [pscustomobject]@{ Outcome = 'stranger'; ProcessId = $l.ProcessId }
        }
        if (-not (Test-Alive $ConsolePid)) { return [pscustomobject]@{ Outcome = 'exited'; ProcessId = 0 } }
        if (((Get-Date) - $startedAt).TotalSeconds -gt 3) {
            $kids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ConsolePid" -ErrorAction SilentlyContinue |
                      Where-Object { $_.Name -ine 'conhost.exe' })
            if ($kids.Count -eq 0) { return [pscustomobject]@{ Outcome = 'exited'; ProcessId = 0 } }
        }
        Start-Sleep -Milliseconds 500
    }
    return [pscustomobject]@{ Outcome = 'timeout'; ProcessId = 0 }
}

# Deliver Ctrl+C to the console a process is attached to. Runs in a helper powershell because the
# sender has to detach from its own console to attach to the target's, and detaching THIS process would
# take an interactive caller's shell down with it. Returns the Win32 error, 0 on success.
function Send-CtrlC([int]$ProcessId) {
    $code = @'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class P1998CtrlC {
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool FreeConsole();
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GenerateConsoleCtrlEvent(uint ev, uint grp);
  public static int Send(uint pid) {
    FreeConsole();
    if (!AttachConsole(pid)) return Marshal.GetLastWin32Error();
    SetConsoleCtrlHandler(IntPtr.Zero, true);
    int err = GenerateConsoleCtrlEvent(0, 0) ? 0 : Marshal.GetLastWin32Error();
    System.Threading.Thread.Sleep(300);
    FreeConsole();
    return err;
  }
}
"@
exit [P1998CtrlC]::Send(TARGETPID)
'@
    $code = $code.Replace('TARGETPID', [string]$ProcessId)
    $enc = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($code))
    $ps = Join-Path $PSHOME 'powershell.exe'
    $ErrorActionPreference = 'Continue'
    & $ps -NoProfile -NonInteractive -EncodedCommand $enc 2>$null | Out-Null
    return $LASTEXITCODE
}

# ---------------------------------------------------------------------------------------------------
# -Status
# ---------------------------------------------------------------------------------------------------

function Show-Status([string]$Root, $Plan) {
    $file = Join-Path $Root $SessionRel
    $allPorts = @($Plan.Login + $Plan.Game)
    $listeners = @(Get-Listeners $allPorts)
    $ours = @()

    $s = Read-Session $Root
    if ($null -ne $s) {
        $lp = Get-Proc ([int]$s.pid_login)
        $gp = Get-Proc ([int]$s.pid_game)
        $loginOk = Test-SessionServer $lp $s.checkout
        $gameOk  = Test-SessionServer $gp $s.checkout
        if ($loginOk) { $ours += [int]$s.pid_login }
        if ($gameOk)  { $ours += [int]$s.pid_game }
        if ($loginOk -and $gameOk) {
            Write-Host "Running ($file):"
        } else {
            Write-Host "Stale session file ($file) - Serve.ps1 -Stop clears it:"
        }
        $lState = 'gone'; if ($loginOk) { $lState = 'alive' } elseif ($null -ne $lp) { $lState = "PID reused by $($lp.Name), not ours" }
        $gState = 'gone'; if ($gameOk)  { $gState = 'alive' } elseif ($null -ne $gp) { $gState = "PID reused by $($gp.Name), not ours" }
        Write-Host "  LOGIN $(@($s.ports.login) -join '/')  PID $($s.pid_login)  $lState"
        Write-Host "  GAME  $(@($s.ports.game) -join '/')  PID $($s.pid_game)  $gState"
        Write-Host "  checkout: $($s.checkout)  ($($s.branch) @ $(Get-ShortCommit $s.commit))"
        Write-Host "  started:  $($s.started)"
        Write-Host "  testers:  [$(@($s.testers) -join ', ')]  gms: [$(@($s.gms) -join ', ')]"
    } else {
        Write-Host "Nothing running from $Root via Serve.ps1 (no $SessionRel)."
    }

    $others = @($listeners | Where-Object { $ours -notcontains $_.ProcessId })
    if ($others.Count -eq 0) {
        if ($ours.Count -eq 0) { Write-Host "Ports $($allPorts -join '/') are free." }
    } else {
        if ($ours.Count -gt 0) { Write-Host "Also listening on the ports (not this session):" }
        else { Write-Host "Listening on the ports:" }
        foreach ($line in (Format-Listeners $others)) { Write-Host $line }
    }
}

# ---------------------------------------------------------------------------------------------------
# -Stop
# ---------------------------------------------------------------------------------------------------

function Invoke-Stop([string]$Root, $Plan) {
    $file = Join-Path $Root $SessionRel
    $s = Read-Session $Root
    if ($null -eq $s) {
        Write-Host "No $SessionRel in $Root - nothing was started from there by Serve.ps1, so nothing to stop."
        $listeners = @(Get-Listeners @($Plan.Login + $Plan.Game))
        if ($listeners.Count -gt 0) {
            Write-Host "Listening on the ports (left alone):"
            foreach ($line in (Format-Listeners $listeners)) { Write-Host $line }
        }
        return 1
    }

    $targets = @(
        [pscustomobject]@{ Label = 'LOGIN'; ProcessId = [int]$s.pid_login; Batch = (Join-Path $s.checkout $LoginBatRel) },
        [pscustomobject]@{ Label = 'GAME';  ProcessId = [int]$s.pid_game;  Batch = (Join-Path $s.checkout $GameBatRel) }
    )
    $failed = $false
    $ourPids = @()   # the session's PIDs that really are its servers; a reused PID is not waited for
    foreach ($t in $targets) {
        $p = Get-Proc $t.ProcessId
        if ($null -eq $p) {
            Write-Host "$($t.Label) PID $($t.ProcessId): already gone."
            continue
        }
        if (-not (Test-SessionServer $p $s.checkout)) {
            Write-Host "$($t.Label) PID $($t.ProcessId) is now $($p.Name) ($($p.CommandLine)) - not this session's server; leaving it alone."
            continue
        }
        $ourPids += $t.ProcessId
        $console = Find-ConsoleHost -ProcessId $t.ProcessId -BatchPath $t.Batch
        $err = Send-CtrlC $t.ProcessId
        if ($err -ne 0) { Write-Host "$($t.Label) PID $($t.ProcessId): Ctrl+C could not be delivered (Win32 error $err); terminating instead." }
        Wait-Process -Id $t.ProcessId -Timeout 20 -ErrorAction SilentlyContinue
        if (Test-Alive $t.ProcessId) {
            Write-Host "$($t.Label) PID $($t.ProcessId) did not exit on Ctrl+C within 20 s; terminating."
            Stop-Process -Id $t.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $t.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
        }
        if (Test-Alive $t.ProcessId) {
            Write-Host "$($t.Label) PID $($t.ProcessId) is still running."
            $failed = $true
            continue
        }
        Write-Host "$($t.Label) PID $($t.ProcessId): stopped."
        if ($null -ne $console -and (Test-Alive ([int]$console.ProcessId))) {
            Stop-Process -Id ([int]$console.ProcessId) -Force -ErrorAction SilentlyContinue
        }
    }

    # Wait for the ports to free. Only OUR pids count: a listener belonging to someone else is reported,
    # not waited for.
    $allPorts = @($Plan.Login + $Plan.Game)
    $deadline = (Get-Date).AddSeconds(15)
    $held = @()
    do {
        $held = @(Get-Listeners $allPorts | Where-Object { $ourPids -contains $_.ProcessId })
        if ($held.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    if ($held.Count -gt 0 -or $failed) {
        Write-Host "Ports still held by this session's processes; keeping $file."
        foreach ($line in (Format-Listeners $held)) { Write-Host $line }
        return 1
    }

    Remove-SessionFiles $Root
    if ($ourPids.Count -gt 0) { Write-Host "Ports $($allPorts -join '/') released by this session; removed $file." }
    else { Write-Host "None of this session's processes were running; removed $file." }
    $others = @(Get-Listeners $allPorts)
    if ($others.Count -gt 0) {
        Write-Host "Still listening on the ports (not this session, left alone):"
        foreach ($line in (Format-Listeners $others)) { Write-Host $line }
    }
    return 0
}

# ---------------------------------------------------------------------------------------------------
# Start
# ---------------------------------------------------------------------------------------------------

function Invoke-Start([string]$Root, $Plan, [string[]]$TesterNames, [string[]]$GmNames) {
    $allPorts = @($Plan.Login + $Plan.Game)

    # 1. Already running from this checkout?
    $s = Read-Session $Root
    if ($null -ne $s) {
        $lp = Get-Proc ([int]$s.pid_login)
        $gp = Get-Proc ([int]$s.pid_game)
        if ((Test-SessionServer $lp $s.checkout) -or (Test-SessionServer $gp $s.checkout)) {
            Write-Host "A pair from this checkout is already running (Serve.ps1 -Stop first):"
            Show-Status $Root $Plan
            return 2
        }
        Write-Host "Clearing a stale $SessionRel (its processes are gone)."
        Remove-SessionFiles $Root
    }

    # 2. Ports held by anyone? Then say who, and do nothing.
    $listeners = @(Get-Listeners $allPorts)
    if ($listeners.Count -gt 0) {
        Write-Host "Not starting: ports in use."
        foreach ($line in (Format-Listeners $listeners)) { Write-Host $line }
        Write-Host "Nothing was started or stopped. Stop that pair yourself (Serve.ps1 -Checkout <its checkout> -Stop if it was started by this script)."
        return 2
    }

    # 3. Identify the build.
    $git = Get-GitInfo $Root
    $clone = Split-Path -Leaf $Root
    $stamp = $git.Short
    if ($git.Dirty) { $stamp += '+' }
    $loginTitle = "LOGIN $($Plan.Login -join '/') - $clone @ $stamp"
    $gameTitle  = "GAME $($Plan.Game -join '/') - $clone @ $stamp"

    # 4. Build once, up front, for the same two reasons as run-server.bat: fail fast, and no two `dotnet
    #    run`s racing on Shared's obj cache.
    $dotnet = Find-Dotnet $Root
    if ($null -eq $dotnet) {
        Write-Host "No .NET 8 SDK found (P1998_DOTNET, $Root\.dotnet, PATH, %LOCALAPPDATA%\Microsoft\dotnet, %ProgramFiles%\dotnet)."
        Write-Host "Run run-server.bat once: it can fetch a private SDK into .dotnet\ beside the source."
        return 1
    }
    $savedNologo = $env:DOTNET_NOLOGO
    $savedCert   = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
    try {
        $env:DOTNET_NOLOGO = '1'
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
        Write-Host "Building $Root\Project1998.sln with $dotnet ..."
        $ErrorActionPreference = 'Continue'
        & $dotnet build (Join-Path $Root 'Project1998.sln') -v:m -nologo
        $buildCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
    } finally {
        $env:DOTNET_NOLOGO = $savedNologo
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $savedCert
    }
    if ($buildCode -ne 0) {
        Write-Host "BUILD FAILED (exit $buildCode) - server not started."
        return 1
    }

    # 5. Tiers: what the launched processes will see. The caller's own P1998_* stay part of that
    #    environment, as they would under run-server.bat, so union them in and record the result.
    $effTesters = Split-Names (@($env:P1998_TESTERS) + $TesterNames)
    $effGms     = Split-Names (@($env:P1998_GMS) + $GmNames)

    # 6. Launch: login first, then game, each in its own visible console.
    $loginBat = Join-Path $Root $LoginBatRel
    $gameBat  = Join-Path $Root $GameBatRel
    Write-LaunchBatch -Path $loginBat -Title $loginTitle -Dotnet $dotnet -Project (Join-Path $Root 'LoginServer') `
                      -Ports $Plan.Login -TesterNames $effTesters -GmNames $effGms
    Write-LaunchBatch -Path $gameBat  -Title $gameTitle  -Dotnet $dotnet -Project (Join-Path $Root 'Server') `
                      -Ports $Plan.Game  -TesterNames $effTesters -GmNames $effGms

    Write-Host "Starting $loginTitle ..."
    $loginConsole = Start-Console -BatchPath $loginBat -WorkingDirectory $Root
    Write-Host "Starting $gameTitle ..."
    $gameConsole  = Start-Console -BatchPath $gameBat  -WorkingDirectory $Root

    # 7. Find the two server processes by the ports they bind, and make sure they are ours.
    $login = Wait-ForListener -Port $Plan.Login[0] -ConsolePid $loginConsole.Id -TimeoutSec 60
    $game  = Wait-ForListener -Port $Plan.Game[0]  -ConsolePid $gameConsole.Id  -TimeoutSec 180
    $problem = $false
    foreach ($pair in @(@('LOGIN', $login, $Plan.Login[0]), @('GAME', $game, $Plan.Game[0]))) {
        $label = $pair[0]; $r = $pair[1]; $port = $pair[2]
        switch ($r.Outcome) {
            'ok'       { }
            'stranger' { Write-Host "$label port $port was bound by PID $($r.ProcessId), which is not the console this script opened."; $problem = $true }
            'exited'   { Write-Host "$label console finished without binding port $port - read its window."; $problem = $true }
            default    { Write-Host "$label did not bind port $port in time - read its window."; $problem = $true }
        }
    }
    if ($problem) {
        Write-Host "No session file written. The consoles are left open for you to read; close them by hand."
        return 1
    }

    # 8. Record it.
    $doc = [ordered]@{
        pid_login = [int]$login.ProcessId
        pid_game  = [int]$game.ProcessId
        checkout  = $Root
        commit    = $git.Commit
        branch    = $git.Branch
        ports     = [ordered]@{ login = @($Plan.Login); game = @($Plan.Game) }
        testers   = @($effTesters)
        gms       = @($effGms)
        started   = (Get-Date).ToString('o')
    }
    $file = Write-Session $Root $doc
    Write-Host "Started from $Root ($($git.Branch) @ $stamp):"
    Write-Host "  LOGIN $($Plan.Login -join '/')  PID $($login.ProcessId)"
    Write-Host "  GAME  $($Plan.Game -join '/')  PID $($game.ProcessId)"
    Write-Host "  testers: [$($effTesters -join ', ')]  gms: [$($effGms -join ', ')]"
    Write-Host "  session: $file"
    return 0
}

# ---------------------------------------------------------------------------------------------------
# Main. Dot-sourcing the script (". Scripts\Serve.ps1") loads the functions without running anything,
# which is how the pieces are exercised without a server.
# ---------------------------------------------------------------------------------------------------

if ($MyInvocation.InvocationName -eq '.') { return }

if ($Status -and $Stop) { Write-Host "Use one of -Status or -Stop."; exit 1 }
if (($Status -or $Stop) -and (@($Testers).Count -gt 0 -or @($Gms).Count -gt 0)) {
    Write-Host "-Testers / -Gms only apply when starting."; exit 1
}

try {
    $root = Resolve-Checkout $Checkout
} catch {
    Write-Host $_.Exception.Message
    exit 1
}
$plan = Get-PortPlan $PortBase

if ($Status) { Show-Status $root $plan; exit 0 }
if ($Stop)   { exit (Invoke-Stop $root $plan) }
exit (Invoke-Start $root $plan (Split-Names $Testers) (Split-Names $Gms))
