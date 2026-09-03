<#
.SYNOPSIS
Start a server pair from a checkout, run the test client's bot scripts against it, stop the pair, and
print one pass/fail table.

.DESCRIPTION
A reviewer's only way to check a claim like "HandleWalk behaviour unchanged" today is to start a server
by hand, drive a bot script by hand, and read the console. This turns that into one command: it starts
LOGIN/GAME the same way a developer would (Scripts\Serve.ps1, visible consoles, never headless), waits
for the game port to answer its status probe, builds and runs every script in
project1998-testclient\scripts\ (or whatever -Scripts names) with --json, and reports exit code / passed
expects / failed expects / wall clock per script. Exit 0 only if every script exited 0 with zero failed
expects (TestClient.Cli\Program.cs .NOTES: 0 pass, 1 an expect failed, 2 usage, 3 the backend could not
run the script) -- this script does not massage that away.

WHAT IT CALLS, AND WHY. Everything about starting, identifying and stopping the pair is
Scripts\Serve.ps1's job, not reimplemented here: this script only adds -Checkout/-PortBase/-Testers/-Gms
and reads its exit code -- and, since a scalar exit code alone was once not proof enough (Serve.ps1 used
to report success after a build failure; fixed alongside this script, see Serve.ps1's own history),
additionally requires that -Checkout's run\session.json exists and was written by THIS call before
treating the pair as started. -Testers/-Gms both go to every name in -Bots (default botone,bottwo)
because the test client's scripts use tester-tier GM commands (@warp, @npc, ...) AND gm-tier ones (@item,
@take, @hp, @clearinv, @self, ...) to grant themselves the state they run against, and three of the suite
scripts (trade-roundtrip.txt, two-bots-one-tile.txt, two-bots-see-each-other.txt) declare a second bot
with its own `bot <alias> <user> <pass>` line and need that second account to hold both tiers too -- see
"Findings" in this PR's report for which script needs which. Only -Bots[0]/-Passes[0]
(the PRIMARY) is passed to TestClient.Cli's --user/--pass; a script's own `bot` line makes the second
connection itself, using whichever of -Bots that line names.

Readiness is judged by Server/StatusResponder.cs's probe: a plain HTTP GET to the GAME port
(PortBase+5, "login on the base always hands its client to base+5" -- Scripts\Serve.ps1 header, PORTS)
gets a one-shot JSON reply once Session.RunAsync's accept loop is live (Server/Session.cs, the
`!IsLoginPort && StatusResponder.LooksLikeHttp` branch). On this codebase's current bind order
(Server/Program.cs loads game-data before opening the listener, and Serve.ps1's own Wait-ForListener does
not return until that listener exists) a plain "is the TCP port open" check would currently be just as
reliable -- this probe is not defending against a content-load race that the bind order already rules
out. It is used anyway because it is a genuine round trip through the same Session/accept-loop code a
real client depends on, not just the listening socket underneath it, and it costs one connection.

WHY THE TEST CLIENT IS BUILT AFTER THE PAIR IS UP, FROM A PER-CHECKOUT COPY. Building before the refusal
checks meant a held port or an already-running pair still paid for a full test-client build; now the build
only runs once this checkout's own pair is confirmed started. project1998-testclient\TestClient.Cli\bin is
one shared directory for every checkout that builds against it, and two checkouts building concurrently
were seen clobbering each other's copy of Protocol.Tk495.dll there -- an MSBuild global property
(-p:BaseOutputPath) forced onto the whole graph was tried first and rejected: it also redirects
Protocol.Tk495\Shared's own output (they are pulled in transitively, not listed in the .sln, but a global
property applies to every project MSBuild visits), and colliding two projects' IntermediateOutputPath this
way is what MSBuild's project-reference resolution reads as a circular dependency (MSB4006), not a
survivable side effect. Instead, TestClient\ and TestClient.Cli\ (source only) are copied fresh each run
into -Checkout\run\test-branch-testclient\, alongside Directory.Build.props so the copy still resolves
$(P1998Repo) and TreatWarningsAsErrors the same way; TestClient.Cli's ProjectReference to TestClient is the
relative `..\TestClient\TestClient.csproj` (still valid, the copy keeps them siblings) and TestClient's own
reference to Protocol.Tk495 goes through $(P1998Repo) (still resolves to -Checkout, untouched, never
copied), so nothing about either reference needs rewriting. The copy's own bin\/obj\ are then private by
construction -- ordinary per-project defaults, no path override needed -- and -Checkout\run\ is already
gitignored (Shared/RepoPaths.cs: "run/ ... Not backed up") and, since Serve.ps1 allows only one pair per
checkout at a time, naturally private to this run.

WHY A WALL-CLOCK GUARD EXISTS ALONGSIDE THE CLIENT'S OWN --timeout-ms. Every `expect` in TestClient
already gives up on its own timeout (ScriptRunner.AwaitObs links a CancellationTokenSource to
--timeout-ms), so a healthy client process always returns. -ScriptTimeoutSec is the harness's own backstop
for the case that isn't healthy -- a wedged `dotnet run` that never gets that far -- and is enforced with
`taskkill /T /F` (the child p1998-test.exe apphost is a grandchild of the `dotnet` process this script
starts, so a plain Kill() on .NET Framework, which has no Kill(bool) overload, would leave it running).

WHY Ctrl+C AND Ctrl+Break ARE CAUGHT. Without a handler, an interrupt during a run left the pair up with
no session file this script would recognise as its own, and PowerShell's default reaction to the two keys
differs: a plain Ctrl+C in a script blocked inside a native/managed wait (Process.WaitForExit, a socket
read) is not observed until that wait returns on its own, and an unhandled Ctrl+Break terminates the
process outright, bypassing try/finally entirely. This script installs the same kind of low-level
SetConsoleCtrlHandler Scripts\Serve.ps1 already uses to deliver Ctrl+C/Ctrl+Break to the servers
(Send-ConsoleBreak), except here it is this script's OWN console being watched: the handler only sets a
flag and claims the event as handled (suppressing the default termination for both keys), and the waits
that can run long -- the readiness poll and a script's run -- are polling loops that check the flag every
200-500ms and cut the wait short. The initial `Serve.ps1 -Start` call and the test-client build are each
one blocking call this script cannot subdivide further; an interrupt during either is honoured as soon as
that call returns, before the next step starts.

.PARAMETER Checkout
The clone to run the pair from and test. Passed straight through to Serve.ps1 -Checkout.

.PARAMETER PortBase
First login port, 1024..65000 (Serve.ps1 -PortBase). Default 3000, not 2000: 2000 is the base a developer
normally has a pair on (README.md "Quick start"), and this script's whole point is to run unattended
alongside that.

.PARAMETER Scripts
Which scripts to run: explicit paths, a glob, or a mix (both accepted as a comma-separated -Scripts list,
matching Serve.ps1 -Testers/-Gms). Default: every *.txt directly under -TestClient's scripts\ directory
(project1998-testclient's own convention: scripts\pending\ holds scripts expected to fail and is skipped
by that same default glob because it only matches scripts\ directly, not subdirectories).

.PARAMETER TestClient
The project1998-testclient checkout. Default C:\Repo\project1998-testclient (its README's own assumed
path: "every agent works on the one machine where C:\Repo\NexusTK exists"). Read-only: this script never
edits or commits there (it builds there, into a directory under -Checkout, not under -TestClient).

.PARAMETER Bots
Every account name to grant both tester and GM tier for this run (Serve.ps1 -Testers/-Gms, every name).
-Bots[0] is the PRIMARY: the one TestClient.Cli logs in as (--user). Default botone,bottwo -- the test
suite's two standing accounts (project1998-testclient README, "Bot accounts"); any script that needs a
second bot names it with its own `bot <alias> <user> <pass>` line, and that name must be in this list.

.PARAMETER Passes
Password for each name in -Bots, same order, same count. Default bot1pass,bot2pass (project1998-testclient
README; passwords need a digit).

.PARAMETER KeepRunning
Skip the -Stop this script would otherwise run once every script has finished (or the ready-probe timed
out, or the run was interrupted), so a developer can keep poking at the pair. Stop it yourself afterwards:
Scripts\Serve.ps1 -Checkout <Checkout> -PortBase <PortBase> -Stop

.PARAMETER Json
Also write the per-script results (plus the run's checkout/portBase/timestamps) as JSON to this path, for
a reviewer to attach to a PR.

.PARAMETER ReadyTimeoutSec
How long to poll the status probe before giving up. Default 60.

.PARAMETER ScriptTimeoutSec
Wall-clock ceiling per script process, independent of the client's own --timeout-ms (see DESCRIPTION).
Default 120.

.EXAMPLE
Scripts\Test-Branch.ps1 -Checkout C:\Repo\NexusTK-sonnet

.EXAMPLE
Scripts\Test-Branch.ps1 -Checkout C:\Repo\NexusTK-sonnet -Scripts C:\scratch\one-liner.txt -ReadyTimeoutSec 5

.NOTES
Windows PowerShell 5.1 compatible. Exit codes: 0 every script exited 0 with zero failed expects; 1 a
script exited nonzero or reported a failed expect, the ready-probe timed out, the run was interrupted
(Ctrl+C/Ctrl+Break), a test-client build failure, or Serve.ps1 -Start reported success (exit 0) but did
not actually write a fresh run\session.json -- this also covers Serve.ps1's own exit 1 (a build failure in
-Checkout, passed through as-is); 2 Serve.ps1 refused to start (ports held, or this checkout already has a
pair running -- its own exit 2, passed through as-is) and this script's own usage errors (a bad
-Checkout/-TestClient/-Scripts/-PortBase/-Bots/-Passes, TestClient.Cli project missing, dotnet not found).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Checkout,
    [int]$PortBase = 3000,
    [string[]]$Scripts,
    [string]$TestClient = 'C:\Repo\project1998-testclient',
    [string[]]$Bots = @('botone', 'bottwo'),
    [string[]]$Passes = @('bot1pass', 'bot2pass'),
    [switch]$KeepRunning,
    [string]$Json,
    [int]$ReadyTimeoutSec = 60,
    [int]$ScriptTimeoutSec = 120
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------------------------------

# Windows PowerShell 5.1 runs on .NET Framework, whose ProcessStartInfo has no ArgumentList collection
# (that is a .NET Core addition) -- only the single Arguments string, so every invocation below builds
# its command line by hand. Quoting follows the CommandLineToArgvW rule every Windows process (including
# dotnet.exe) parses its command line by: a run of backslashes is only doubled when it is immediately
# followed by a quote (either an embedded one or the closing one this function adds), never otherwise --
# so a bare trailing backslash in an unquoted-looking argument does not eat the closing quote.
function Format-Args([string[]]$Parts) {
    return (($Parts | ForEach-Object {
        $s = [string]$_
        # An empty string must still become an explicit "" -- left unquoted it contributes nothing to the
        # joined line at all (just extra whitespace between neighbours), silently dropping the argument and
        # shifting every positional argument after it.
        if ($s -eq '' -or $s -match '[\s"]') {
            $escaped = [regex]::Replace($s, '(\\*)"', '$1$1\"')
            $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
            '"' + $escaped + '"'
        } else { $s }
    }) -join ' ')
}

# taskkill /T: the process this script starts is `dotnet`, which for `dotnet run` launches the built
# apphost (p1998-test.exe, TestClient.Cli's <AssemblyName>) as a CHILD of it -- Kill() alone (the only
# overload on .NET Framework; Kill(bool entireProcessTree) is .NET Core-only) would leave that child
# running and the bot connected. $ErrorActionPreference = 'Continue' for the duration of this one native
# call, Scripts\Serve.ps1's own pattern (Get-GitInfo, Test-DotnetSdk, Send-ConsoleBreak): under 'Stop',
# ANY stderr line from a native command -- including taskkill's normal "process not found" when the tree
# already exited on its own between the timeout check and this call -- becomes a terminating exception
# that would unwind out of the caller and lose the rest of the table.
function Stop-ProcessTree([int]$ProcessId) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & taskkill /PID $ProcessId /T /F 2>$null | Out-Null } catch { }
    $ErrorActionPreference = $prevEap
}

# The status probe (Server/StatusResponder.cs): a bare HTTP GET on the GAME port, answered once
# Session.RunAsync's accept loop is live, before the client's first real frame. One-shot -- the server
# closes the connection after replying (Connection: close), so a fresh TcpClient every attempt. Checks
# TestBranchCtrl.Interrupted each pass so a Ctrl+C/Ctrl+Break during a long wait cuts it short instead of
# running out the full -ReadyTimeoutSec.
function Wait-GameReady([string]$HostName, [int]$Port, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ([TestBranchCtrl]::Interrupted) { return $null }
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $iar = $client.BeginConnect($HostName, $Port, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(1000) -and $client.Connected) {
                $client.EndConnect($iar)
                $stream = $client.GetStream()
                $stream.ReadTimeout = 2000
                $req = [System.Text.Encoding]::ASCII.GetBytes("GET / HTTP/1.0`r`n`r`n")
                $stream.Write($req, 0, $req.Length)
                $buf = New-Object byte[] 4096
                $read = $stream.Read($buf, 0, $buf.Length)
                $resp = [System.Text.Encoding]::ASCII.GetString($buf, 0, $read)
                $client.Close()
                if ($resp -match '"up"\s*:\s*true') { return $resp }
            } else {
                $client.Close()
            }
        } catch { }
        if ([TestBranchCtrl]::Interrupted) { return $null }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# logs/server.log, logs/login.log (Shared/RepoPaths.cs LogsDir/AttachFile calls in Server/Program.cs,
# LoginServer/Program.cs) -- the only record of a console's output once this script has moved on, since
# the consoles Serve.ps1 opens are not this script's stdout.
function Show-LogTail([string]$Root, [int]$Lines = 25) {
    foreach ($name in @('server.log', 'login.log')) {
        $p = Join-Path $Root "logs\$name"
        if (Test-Path -LiteralPath $p) {
            Write-Host "--- tail of $p ---"
            Get-Content -LiteralPath $p -Tail $Lines | ForEach-Object { Write-Host "  $_" }
        } else {
            Write-Host "(no $p)"
        }
    }
}

# Run one TestClient.Cli invocation and reduce its --json lines to what the table needs. Pass/fail
# counting matches TestClient\ScriptRunner.cs Check()/Note(): a held expect is noted "ok   <text>", a
# failed one "FAIL <text>" (note the trailing space -- the run summary "FAILED (n)" has no space after
# "FAIL" at that position, so it never miscounts as a per-expect failure; a two-bot failure is
# "FAIL [<alias>] <text>", still matched by the same prefix). WaitForExit is polled in short slices
# (rather than one $TimeoutSec-long blocking call) so an interrupt is noticed within a fraction of a
# second instead of only after the whole timeout elapses.
function Invoke-TestScript([string]$Dotnet, [string]$CliProject,
                            [string]$ScriptPath, [string]$LoginHost, [int]$LoginPort, [string]$User, [string]$UserPass,
                            [int]$TimeoutSec) {
    $argLine = Format-Args @('run', '--no-build', '--project', $CliProject,
                              '--', $ScriptPath, '--host', $LoginHost, '--port', "$LoginPort",
                              '--user', $User, '--pass', $UserPass, '--json')
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Dotnet
    $psi.Arguments = $argLine
    $psi.WorkingDirectory = Split-Path -Parent $CliProject
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)
    # Started as Tasks (not a blocking ReadToEnd) before the wait loop: the classic redirected-stream
    # deadlock is exactly "process blocks writing because nobody is reading while we block waiting for
    # exit", and starting the reads first avoids it.
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $exited = $false
    $interrupted = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.WaitForExit(200)) { $exited = $true; break }
        if ([TestBranchCtrl]::Interrupted) { $interrupted = $true; break }
    }
    $timedOut = (-not $exited) -and (-not $interrupted)
    if ($timedOut -or $interrupted) {
        Stop-ProcessTree -ProcessId $proc.Id
        $proc.WaitForExit(5000) | Out-Null
    }
    $sw.Stop()
    $stdout = ''
    $stderr = ''
    try { $stdout = $stdoutTask.Result } catch { }
    try { $stderr = $stderrTask.Result } catch { }

    $passed = 0
    $failed = 0
    $firstFailure = ''
    $lastNote = ''
    foreach ($line in ($stdout -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $obj = $null
        try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($null -eq $obj -or $obj.kind -ne 'note') { continue }
        $text = [string]$obj.text
        $lastNote = $text
        if ($text.StartsWith('ok   ')) { $passed++ }
        elseif ($text.StartsWith('FAIL ')) {
            $failed++
            if ($firstFailure -eq '') { $firstFailure = $text.Substring(5) }
        }
    }

    $exitCode = if ($timedOut -or $interrupted) { $null } else { $proc.ExitCode }
    if ($interrupted) {
        $firstFailure = "interrupted ($([TestBranchCtrl]::LastSignal)) after $([Math]::Round($sw.Elapsed.TotalSeconds, 1))s; last note: $lastNote"
    } elseif ($firstFailure -eq '' -and $failed -eq 0 -and $exitCode -ne 0) {
        if ($timedOut) { $firstFailure = "no result within ${TimeoutSec}s (killed); last note: $lastNote" }
        elseif ($lastNote -ne '') { $firstFailure = "(no FAIL line, exit $exitCode) $lastNote" }
        else {
            $errLine = ($stderr -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
            $firstFailure = "(no FAIL line, exit $exitCode) $errLine"
        }
    } elseif ($exitCode -eq 0 -and $passed -eq 0 -and $failed -eq 0) {
        # No `expect` ran at all -- ScriptRunner.RunAsync returns 0 for an empty (or all-comment) program
        # without ever connecting, which would otherwise read as an ordinary silent pass.
        $firstFailure = 'WARNING: 0 expects ran - nothing was verified'
    }

    return [pscustomobject]@{
        Script       = (Split-Path -Leaf $ScriptPath)
        ExitCode     = $exitCode
        TimedOut     = $timedOut
        Interrupted  = $interrupted
        Passed       = $passed
        Failed       = $failed
        WallClockSec = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        FirstFailure = $firstFailure
    }
}

# ---------------------------------------------------------------------------------------------------
# Resolve inputs
# ---------------------------------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $Checkout -PathType Container)) { Write-Host "Checkout not found: $Checkout"; exit 2 }
$CheckoutFull = (Resolve-Path -LiteralPath $Checkout).ProviderPath.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $CheckoutFull 'Project1998.sln') -PathType Leaf)) {
    Write-Host "Not a Project1998 checkout (no Project1998.sln): $CheckoutFull"; exit 2
}

if (-not (Test-Path -LiteralPath $TestClient -PathType Container)) { Write-Host "Test client not found: $TestClient"; exit 2 }
$TestClientFull = (Resolve-Path -LiteralPath $TestClient).ProviderPath.TrimEnd('\')
$CliProject = Join-Path $TestClientFull 'TestClient.Cli'
if (-not (Test-Path -LiteralPath $CliProject -PathType Container)) { Write-Host "TestClient.Cli project not found under $TestClientFull"; exit 2 }
$Sln = Join-Path $TestClientFull 'project1998-testclient.sln'
if (-not (Test-Path -LiteralPath $Sln -PathType Leaf)) { Write-Host "project1998-testclient.sln not found under $TestClientFull"; exit 2 }

$ServePs1 = Join-Path $PSScriptRoot 'Serve.ps1'
if (-not (Test-Path -LiteralPath $ServePs1 -PathType Leaf)) { Write-Host "Scripts\Serve.ps1 not found beside this script ($PSScriptRoot)"; exit 2 }

if ($PortBase -lt 1024 -or $PortBase -gt 65000) {
    # Serve.ps1 checks this too and would refuse with the same message; checked here as well so a bad
    # -PortBase is reported before this script spends time on anything else.
    Write-Host "-PortBase must be 1024..65000 (Serve.ps1's own bound: the pair binds base, base+1, base+5, base+6)."
    exit 2
}

if (-not $Bots -or $Bots.Count -eq 0) { Write-Host "-Bots needs at least one account name."; exit 2 }
if ($Passes.Count -ne $Bots.Count) {
    Write-Host "-Bots has $($Bots.Count) name(s) but -Passes has $($Passes.Count); give one password per bot, same order."
    exit 2
}

$scriptFiles = New-Object System.Collections.Generic.List[string]
if ($Scripts -and $Scripts.Count -gt 0) {
    foreach ($s in $Scripts) {
        if (Test-Path -LiteralPath $s -PathType Leaf) {
            $scriptFiles.Add((Resolve-Path -LiteralPath $s).ProviderPath)
        } else {
            $hits = @(Get-ChildItem -Path $s -File -ErrorAction SilentlyContinue)
            if ($hits.Count -eq 0) { Write-Host "No script matches '$s'."; exit 2 }
            foreach ($h in $hits) { $scriptFiles.Add($h.FullName) }
        }
    }
} else {
    # Directly under scripts\ only -- project1998-testclient's own scripts\pending\ holds scripts expected
    # to fail until a named server issue is fixed, and its README says its default glob skips them the
    # same way: *.txt (no recurse) never reaches a subdirectory.
    $default = Join-Path $TestClientFull 'scripts'
    $hits = @(Get-ChildItem -Path $default -Filter '*.txt' -File -ErrorAction SilentlyContinue | Sort-Object Name)
    if ($hits.Count -eq 0) { Write-Host "No *.txt scripts under $default."; exit 2 }
    foreach ($h in $hits) { $scriptFiles.Add($h.FullName) }
}

$dotnetCmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnetCmd) { Write-Host "dotnet not found on PATH."; exit 2 }
$Dotnet = $dotnetCmd.Source

$LoginPort = $PortBase
$GamePort = $PortBase + 5   # Scripts\Serve.ps1 header, PORTS: game binds PortBase+5 and PortBase+6.
$PrimaryBot = $Bots[0]
$PrimaryPass = $Passes[0]
# Per-checkout copy of TestClient/TestClient.Cli (see DESCRIPTION for why this replaced an
# -p:BaseOutputPath override): built and run from here, never from $TestClientFull.
$BuildRoot = Join-Path $CheckoutFull 'run\test-branch-testclient'
$CopiedCliProject = Join-Path $BuildRoot 'TestClient.Cli'
$SessionFile = Join-Path $CheckoutFull 'run\session.json'

Write-Host "Test-Branch: checkout=$CheckoutFull  portBase=$PortBase  testClient=$TestClientFull  bots=$($Bots -join ',')  primary=$PrimaryBot"
Write-Host "Scripts ($($scriptFiles.Count)):"
foreach ($f in $scriptFiles) { Write-Host "  $f" }

# ---------------------------------------------------------------------------------------------------
# Ctrl+C / Ctrl+Break: a low-level console control handler, the same mechanism (SetConsoleCtrlHandler)
# Scripts\Serve.ps1's Send-ConsoleBreak uses to deliver these signals TO the servers -- here it watches
# THIS script's own console instead. Windows runs the handler on its own thread the moment either key is
# pressed, independent of what the main thread is blocked in, so it is safe to install once and then just
# poll the flag from wherever this script is waiting. Returning $true claims the event as handled, which
# is what stops Ctrl+Break's default action (silent process termination, bypassing try/finally) as well
# as Ctrl+C's.
#
# Installed only now, after every usage check above has had its chance to exit -- and uninstalled in the
# outer finally below no matter how this script ends. A handler left installed is process-global and
# outlives this script in the console that ran it: a caller's interactive shell that ran Test-Branch to a
# plain usage exit would otherwise keep swallowing Ctrl+C and Ctrl+Break for everything typed there
# afterwards, since SetConsoleCtrlHandler's registration has nothing to do with this script's own scope.
#
# SetConsoleCtrlHandler(IntPtr.Zero, false) first: a process placed in a NEW process group (which is what
# it takes to target this script's console programmatically, e.g. to test it, rather than typing into an
# already-open one) inherits an "ignore Ctrl+C" default (documented CreateProcess behaviour) that a plain
# handler registration does not clear on its own -- this line is what makes a synthetic Ctrl+C reach
# OnCtrl at all in that scenario. It is a no-op (returns true, changes nothing) in an ordinary interactive
# console, which never had that default set.
$ctrlSrc = @'
using System;
using System.Runtime.InteropServices;
public static class TestBranchCtrl {
    public delegate bool Handler(uint ctrlType);
    private static Handler _handler;
    public static volatile bool Interrupted = false;
    public static volatile string LastSignal = "";
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(Handler h, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
    public static bool Install() {
        SetConsoleCtrlHandler(IntPtr.Zero, false);   // clear an inherited ignore, see the PowerShell comment above
        _handler = new Handler(OnCtrl);
        return SetConsoleCtrlHandler(_handler, true);
    }
    public static bool Uninstall() {
        if (_handler == null) return true;
        bool ok = SetConsoleCtrlHandler(_handler, false);
        _handler = null;
        return ok;
    }
    private static bool OnCtrl(uint ctrlType) {
        // 0 = CTRL_C_EVENT, 1 = CTRL_BREAK_EVENT (Server/Net.cs and Scripts\Serve.ps1's
        // Send-ConsoleBreak use the same two values against the servers).
        Interrupted = true;
        LastSignal = ctrlType == 0 ? "Ctrl+C" : ctrlType == 1 ? "Ctrl+Break" : ("signal " + ctrlType);
        return true;
    }
}
'@
$ctrlInstalled = $false
try {
    Add-Type -TypeDefinition $ctrlSrc -Language CSharp -ErrorAction Stop
    $ctrlInstalled = [TestBranchCtrl]::Install()
} catch {
    Write-Host "Could not install a Ctrl+C/Ctrl+Break handler ($($_.Exception.Message)); an interrupt may not stop the pair cleanly."
}

# ---------------------------------------------------------------------------------------------------
# Start, wait for readiness, build the test client, run scripts, always stop (unless -KeepRunning)
# ---------------------------------------------------------------------------------------------------

$started = $false
$exitCode = 0
try {
try {
    # F1: a session file that predates this call must not be mistaken for proof that THIS call started
    # anything -- record what was there (or that nothing was) before asking Serve.ps1 to start.
    $sessionBefore = $null
    if (Test-Path -LiteralPath $SessionFile -PathType Leaf) { $sessionBefore = (Get-Item -LiteralPath $SessionFile).LastWriteTimeUtc }

    Write-Host "Starting the server pair (Scripts\Serve.ps1 -Checkout $CheckoutFull -PortBase $PortBase -Testers $($Bots -join ',') -Gms $($Bots -join ',')) ..."
    & $ServePs1 -Checkout $CheckoutFull -PortBase $PortBase -Testers $Bots -Gms $Bots
    $serveExit = $LASTEXITCODE

    $sessionWritten = $false
    if (Test-Path -LiteralPath $SessionFile -PathType Leaf) {
        $sessionAfter = (Get-Item -LiteralPath $SessionFile).LastWriteTimeUtc
        if ($null -eq $sessionBefore -or $sessionAfter -gt $sessionBefore) { $sessionWritten = $true }
    }

    if ($serveExit -ne 0 -or -not $sessionWritten) {
        # "started" is proved by a FRESH run\session.json, not by Serve.ps1's exit code alone: exit 0 with
        # no (fresh) session file means Serve.ps1 reported success without actually starting a pair this
        # script can find and stop, which must not be treated as "started" -- no probe wait, no log tail,
        # no stop attempt (there is nothing this call is known to have started).
        if ($serveExit -eq 0) {
            Write-Host "Serve.ps1 reported success (exit 0) but $SessionFile was not (re)written by this call; treating this run as not started."
        } else {
            Write-Host "Serve.ps1 -Start did not succeed (exit $serveExit); nothing to run."
        }
        exit $(if ($serveExit -ne 0) { $serveExit } else { 1 })
    }
    $started = $true

    if ([TestBranchCtrl]::Interrupted) {
        Write-Host "Interrupted ($([TestBranchCtrl]::LastSignal)) right after the pair started; stopping without running anything."
        $exitCode = 1
    } else {
        Write-Host "Waiting for the status probe on port $GamePort (timeout ${ReadyTimeoutSec}s) ..."
        $probe = Wait-GameReady -HostName '127.0.0.1' -Port $GamePort -TimeoutSec $ReadyTimeoutSec
        if ($null -eq $probe) {
            if ([TestBranchCtrl]::Interrupted) {
                Write-Host "Interrupted ($([TestBranchCtrl]::LastSignal)) while waiting for the status probe on port $GamePort."
            } else {
                Write-Host "Game server did not answer the status probe on port $GamePort within ${ReadyTimeoutSec}s."
                Show-LogTail -Root $CheckoutFull
            }
            $exitCode = 1
        } else {
            Write-Host "Ready: $probe"

            # F5: the test client is copied and built only now -- after the ports/already-running/build-
            # failure refusal checks above have all had their chance to exit first -- into a directory
            # private to this checkout (see DESCRIPTION), not project1998-testclient's own shared
            # TestClient.Cli\bin. A fresh copy every run: source only (bin\/obj\ stripped after the copy,
            # Copy-Item -Exclude does not reliably reach nested subdirectories), so stale build output
            # from a previous run's copy is never mistaken for this run's.
            $buildOk = $false
            try {
                if (Test-Path -LiteralPath $BuildRoot) { Remove-Item -LiteralPath $BuildRoot -Recurse -Force }
                New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
                Copy-Item -LiteralPath (Join-Path $TestClientFull 'Directory.Build.props') -Destination $BuildRoot -Force
                foreach ($proj in @('TestClient', 'TestClient.Cli')) {
                    Copy-Item -LiteralPath (Join-Path $TestClientFull $proj) -Destination (Join-Path $BuildRoot $proj) -Recurse -Force
                }
                Get-ChildItem -LiteralPath $BuildRoot -Recurse -Directory -Force |
                    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
                    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Host "Could not stage the test client copy at $BuildRoot ($($_.Exception.Message))."
                $exitCode = 1
            }

            if ($exitCode -eq 0) {
                $savedRepoEnv = $env:P1998_REPO
                try {
                    $env:P1998_REPO = $CheckoutFull
                    Write-Host "Building $CopiedCliProject (P1998_REPO=$CheckoutFull) ..."
                    $buildArgs = Format-Args @('build', $CopiedCliProject, '-nologo', '-v:m')
                    $bpsi = New-Object System.Diagnostics.ProcessStartInfo
                    $bpsi.FileName = $Dotnet
                    $bpsi.Arguments = $buildArgs
                    $bpsi.WorkingDirectory = $BuildRoot
                    $bpsi.UseShellExecute = $false
                    $bproc = [System.Diagnostics.Process]::Start($bpsi)
                    $bproc.WaitForExit()
                    if ($bproc.ExitCode -ne 0) {
                        Write-Host "BUILD FAILED (exit $($bproc.ExitCode)) - test client not built, no scripts run."
                        $exitCode = 1
                    } else { $buildOk = $true }
                } finally {
                    $env:P1998_REPO = $savedRepoEnv
                }
            }

            if ($buildOk -and [TestBranchCtrl]::Interrupted) {
                Write-Host "Interrupted ($([TestBranchCtrl]::LastSignal)) after the test client build; running nothing."
                $exitCode = 1
            } elseif ($buildOk) {
                $results = New-Object System.Collections.Generic.List[object]
                foreach ($f in $scriptFiles) {
                    if ([TestBranchCtrl]::Interrupted) {
                        Write-Host "Interrupted ($([TestBranchCtrl]::LastSignal)); not starting $(Split-Path -Leaf $f)."
                        break
                    }
                    Write-Host "Running $(Split-Path -Leaf $f) ..."
                    $r = Invoke-TestScript -Dotnet $Dotnet -CliProject $CopiedCliProject `
                                            -ScriptPath $f -LoginHost '127.0.0.1' -LoginPort $LoginPort -User $PrimaryBot -UserPass $PrimaryPass `
                                            -TimeoutSec $ScriptTimeoutSec
                    $results.Add($r)
                    if ($r.Interrupted) { break }
                }

                Write-Host ""
                Write-Host ("{0,-28} {1,7} {2,6} {3,6} {4,9}  {5}" -f 'SCRIPT', 'EXIT', 'PASS', 'FAIL', 'SECONDS', 'FIRST FAILURE')
                foreach ($r in $results) {
                    $exitDisp = if ($r.Interrupted) { 'INTERRUPT' } elseif ($r.TimedOut) { 'TIMEOUT' } else { [string]$r.ExitCode }
                    Write-Host ("{0,-28} {1,7} {2,6} {3,6} {4,9}  {5}" -f $r.Script, $exitDisp, $r.Passed, $r.Failed, $r.WallClockSec, $r.FirstFailure)
                }
                Write-Host ""

                # F11: a script that exited 0 but still reported a failed expect (a diagnostic Note() text
                # that happens to start "FAIL ", not one of ScriptRunner's own Check() failures, which
                # always drive the exit code to 1 -- but this must not rely on that always being true) must
                # not read as a pass just because $_.Failed was never consulted.
                $notOk = @($results | Where-Object { $_.TimedOut -or $_.Interrupted -or $_.ExitCode -ne 0 -or $_.Failed -gt 0 })
                $allOk = ($notOk.Count -eq 0) -and (-not [TestBranchCtrl]::Interrupted) -and ($results.Count -eq $scriptFiles.Count)
                $passedCount = @($results | Where-Object { -not $_.TimedOut -and -not $_.Interrupted -and $_.ExitCode -eq 0 -and $_.Failed -eq 0 }).Count
                Write-Host "$passedCount/$($scriptFiles.Count) scripts exited 0 with zero failed expects."
                if ($exitCode -eq 0) { $exitCode = if ($allOk) { 0 } else { 1 } }

                if ($Json) {
                    $doc = [ordered]@{
                        checkout   = $CheckoutFull
                        testClient = $TestClientFull
                        portBase   = $PortBase
                        bots       = @($Bots)
                        primaryBot = $PrimaryBot
                        ranAt      = (Get-Date).ToString('o')
                        interrupted = [bool][TestBranchCtrl]::Interrupted
                        results    = @($results | ForEach-Object {
                            [ordered]@{
                                script       = $_.Script
                                exitCode     = $_.ExitCode
                                timedOut     = $_.TimedOut
                                interrupted  = $_.Interrupted
                                passed       = $_.Passed
                                failed       = $_.Failed
                                wallClockSec = $_.WallClockSec
                                firstFailure = $_.FirstFailure
                            }
                        })
                    }
                    $jsonText = $doc | ConvertTo-Json -Depth 6
                    # No BOM, matching Scripts\Serve.ps1's Write-Session: a plain json parser reliably rejects one.
                    [System.IO.File]::WriteAllText($Json, $jsonText + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding $false))
                    Write-Host "Wrote $Json"
                }
            }
        }
    }
} finally {
    if ($started) {
        if ($KeepRunning) {
            Write-Host "-KeepRunning: leaving the pair up. Stop it with:"
            Write-Host "  Scripts\Serve.ps1 -Checkout $CheckoutFull -PortBase $PortBase -Stop"
        } else {
            Write-Host "Stopping the pair (Scripts\Serve.ps1 -Checkout $CheckoutFull -PortBase $PortBase -Stop) ..."
            & $ServePs1 -Checkout $CheckoutFull -PortBase $PortBase -Stop
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Serve.ps1 -Stop did not return 0 (exit $LASTEXITCODE) - see its output above."
                if ($exitCode -eq 0) { $exitCode = 1 }
            }
        }
    }
    # N4: the per-checkout test-client copy ($BuildRoot, see the build step above) is scratch, not state --
    # left behind it just grows stale on every run. -KeepRunning also keeps it, on the theory that a
    # developer poking at the pair afterwards may want to poke at the same build too.
    if (-not $KeepRunning -and (Test-Path -LiteralPath $BuildRoot)) {
        Remove-Item -LiteralPath $BuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
} finally {
    # Uninstalled no matter how the above ends (including every `exit` above -- a `finally` still runs
    # when the block it guards exits via `exit`, same as it would for a thrown exception). Left installed,
    # this is process-global and would keep swallowing Ctrl+C/Ctrl+Break in whatever console ran this
    # script for everything typed there afterwards -- the reviewer proved exactly that with two shells.
    if ($ctrlInstalled) { [TestBranchCtrl]::Uninstall() | Out-Null }
}

exit $exitCode
