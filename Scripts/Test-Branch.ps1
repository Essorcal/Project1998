<#
.SYNOPSIS
Start a server pair from a checkout, run the test client's bot scripts against it, stop the pair, and
print one pass/fail table.

.DESCRIPTION
A reviewer's only way to check a claim like "HandleWalk behaviour unchanged" today is to start a server
by hand, drive a bot script by hand, and read the console. This turns that into one command: it starts
LOGIN/GAME the same way a developer would (Scripts\Serve.ps1, visible consoles, never headless), waits
for the game port to answer its status probe, runs every script in project1998-testclient\scripts\ (or
whatever -Scripts names) with --json, and reports exit code / passed expects / failed expects / wall
clock per script. Exit 0 only if every script exited 0 (TestClient.Cli\Program.cs .NOTES: 0 pass, 1 an
expect failed, 2 usage, 3 the backend could not run the script) -- this script does not massage that
away.

WHAT IT CALLS, AND WHY. Everything about starting, identifying and stopping the pair is
Scripts\Serve.ps1's job, not reimplemented here: this script only adds -Checkout/-PortBase/-Testers/-Gms
and reads its exit code. -Testers/-Gms both name -Bot (default botone) because the test client's scripts
use tester-tier GM commands (@warp, @npc, ...) AND gm-tier ones (@item, @take, @hp, @clearinv, @self,
...) to grant themselves the state they run against -- see "Findings" in this PR's report for which
script needs which. Readiness is judged by Server/StatusResponder.cs's probe: a plain HTTP GET to the
GAME port (PortBase+5, "login on the base always hands its client to base+5" -- Scripts\Serve.ps1
header, PORTS) gets a one-shot JSON reply once Session.RunAsync's accept loop is live (Server/Session.cs,
the `!IsLoginPort && StatusResponder.LooksLikeHttp` branch); polling that instead of just the TCP port
means a script never races the content load the way "port is open" would.

WHY THE TEST CLIENT IS BUILT WITH P1998_REPO SET. project1998-testclient\Directory.Build.props points
Protocol.Tk495 at ..\NexusTK by default (a sibling of the test client repo, not of -Checkout), overridable
with the P1998_REPO environment variable. This script sets P1998_REPO to -Checkout for the one `dotnet
build` it runs up front, so the cipher/framing the bot speaks is built from the branch under test, then
restores whatever P1998_REPO was.

WHY A WALL-CLOCK GUARD EXISTS ALONGSIDE THE CLIENT'S OWN --timeout-ms. Every `expect` in TestClient
already gives up on its own timeout (ScriptRunner.AwaitObs links a CancellationTokenSource to
--timeout-ms), so a healthy client process always returns. -ScriptTimeoutSec is the harness's own backstop
for the case that isn't healthy -- a wedged `dotnet run` that never gets that far -- and is enforced with
`taskkill /T /F` (the child TestClient.Cli.exe apphost is a grandchild of the `dotnet` process this script
starts, so a plain Kill() on .NET Framework, which has no Kill(bool) overload, would leave it running).

.PARAMETER Checkout
The clone to run the pair from and test. Passed straight through to Serve.ps1 -Checkout.

.PARAMETER PortBase
First login port, 1024..65000 (Serve.ps1 -PortBase). Default 3000, not 2000: 2000 is the base a developer
normally has a pair on (README.md "Quick start"), and this script's whole point is to run unattended
alongside that.

.PARAMETER Scripts
Which scripts to run: explicit paths, a glob, or a mix (both accepted as a comma-separated -Scripts list,
matching Serve.ps1 -Testers/-Gms). Default: every *.txt directly under -TestClient's scripts\ directory.

.PARAMETER TestClient
The project1998-testclient checkout. Default C:\Repo\project1998-testclient (its README's own assumed
path: "every agent works on the one machine where C:\Repo\NexusTK exists"). Read-only: this script never
edits or commits there.

.PARAMETER Bot
The account every script logs in as, and the name granted both tester and GM tier for this run (Serve.ps1
-Testers <Bot> -Gms <Bot>). Default botone (project1998-testclient README, "Bot accounts").

.PARAMETER Pass
Password for -Bot. Default bot1pass (project1998-testclient README; passwords need a digit).

.PARAMETER KeepRunning
Skip the -Stop this script would otherwise run once every script has finished (or the ready-probe timed
out), so a developer can keep poking at the pair. Stop it yourself afterwards:
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
Windows PowerShell 5.1 compatible. Exit codes: 0 every script exited 0; 1 a script exited nonzero, the
ready-probe timed out, or Serve.ps1 -Start failed (its own exit 1); 2 Serve.ps1 refused to start (ports
held, or this checkout already has a pair running -- its own exit 2, "say who holds them"); 2 also for
this script's own usage errors (no scripts matched, TestClient.Cli project missing, ...).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Checkout,
    [int]$PortBase = 3000,
    [string[]]$Scripts,
    [string]$TestClient = 'C:\Repo\project1998-testclient',
    [string]$Bot = 'botone',
    [string]$Pass = 'bot1pass',
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
# its command line by hand.
function Format-Args([string[]]$Parts) {
    return (($Parts | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { [string]$_ }
    }) -join ' ')
}

# taskkill /T: the process this script starts is `dotnet`, which for `dotnet run` launches the built
# apphost (TestClient.Cli.exe) as a CHILD of it -- Kill() alone (the only overload on .NET Framework;
# Kill(bool entireProcessTree) is .NET Core-only) would leave that child running and the bot connected.
function Stop-ProcessTree([int]$ProcessId) {
    & taskkill /PID $ProcessId /T /F 2>$null | Out-Null
}

# The status probe (Server/StatusResponder.cs): a bare HTTP GET on the GAME port, answered once
# Session.RunAsync's accept loop is live, before the client's first real frame. One-shot -- the server
# closes the connection after replying (Connection: close), so a fresh TcpClient every attempt.
function Wait-GameReady([string]$HostName, [int]$Port, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
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
# "FAIL" at that position, so it never miscounts as a per-expect failure).
function Invoke-TestScript([string]$Dotnet, [string]$CliProject, [string]$ScriptPath, [string]$LoginHost,
                            [int]$LoginPort, [string]$User, [string]$UserPass, [int]$TimeoutSec) {
    $argLine = Format-Args @('run', '--no-build', '--project', $CliProject, '--', $ScriptPath,
                              '--host', $LoginHost, '--port', "$LoginPort", '--user', $User, '--pass', $UserPass, '--json')
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
    # Started as Tasks (not a blocking ReadToEnd) before WaitForExit: the classic redirected-stream
    # deadlock is exactly "process blocks writing because nobody is reading while we block waiting for
    # exit", and starting the reads first avoids it.
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $exited = $proc.WaitForExit($TimeoutSec * 1000)
    $timedOut = -not $exited
    if ($timedOut) {
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

    $exitCode = if ($timedOut) { $null } else { $proc.ExitCode }
    if ($firstFailure -eq '' -and $failed -eq 0 -and $exitCode -ne 0) {
        if ($timedOut) { $firstFailure = "no result within ${TimeoutSec}s (killed); last note: $lastNote" }
        elseif ($lastNote -ne '') { $firstFailure = "(no FAIL line, exit $exitCode) $lastNote" }
        else {
            $errLine = ($stderr -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
            $firstFailure = "(no FAIL line, exit $exitCode) $errLine"
        }
    }

    return [pscustomobject]@{
        Script       = (Split-Path -Leaf $ScriptPath)
        ExitCode     = $exitCode
        TimedOut     = $timedOut
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
    # -PortBase is reported before this script spends time building the test client.
    Write-Host "-PortBase must be 1024..65000 (Serve.ps1's own bound: the pair binds base, base+1, base+5, base+6)."
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

Write-Host "Test-Branch: checkout=$CheckoutFull  portBase=$PortBase  testClient=$TestClientFull  bot=$Bot"
Write-Host "Scripts ($($scriptFiles.Count)):"
foreach ($f in $scriptFiles) { Write-Host "  $f" }

# ---------------------------------------------------------------------------------------------------
# Build the test client against this checkout's Protocol.Tk495 (see DESCRIPTION, P1998_REPO)
# ---------------------------------------------------------------------------------------------------

$savedRepoEnv = $env:P1998_REPO
try {
    $env:P1998_REPO = $CheckoutFull
    Write-Host "Building $Sln (P1998_REPO=$CheckoutFull) ..."
    $buildArgs = Format-Args @('build', $Sln, '-nologo', '-v:m')
    $bpsi = New-Object System.Diagnostics.ProcessStartInfo
    $bpsi.FileName = $Dotnet
    $bpsi.Arguments = $buildArgs
    $bpsi.WorkingDirectory = $TestClientFull
    $bpsi.UseShellExecute = $false
    $bproc = [System.Diagnostics.Process]::Start($bpsi)
    $bproc.WaitForExit()
    if ($bproc.ExitCode -ne 0) { Write-Host "BUILD FAILED (exit $($bproc.ExitCode)) - nothing was started."; exit 2 }
} finally {
    $env:P1998_REPO = $savedRepoEnv
}

# ---------------------------------------------------------------------------------------------------
# Start, wait for readiness, run scripts, always stop (unless -KeepRunning)
# ---------------------------------------------------------------------------------------------------

$started = $false
$exitCode = 0
try {
    Write-Host "Starting the server pair (Scripts\Serve.ps1 -Checkout $CheckoutFull -PortBase $PortBase -Testers $Bot -Gms $Bot) ..."
    & $ServePs1 -Checkout $CheckoutFull -PortBase $PortBase -Testers $Bot -Gms $Bot
    $serveExit = $LASTEXITCODE
    if ($serveExit -ne 0) {
        # Serve.ps1's own Write-Host lines already said who holds the ports (exit 2) or what failed to
        # build/launch (exit 1) -- exit with the same code and nothing further, per the contract.
        Write-Host "Serve.ps1 -Start did not succeed (exit $serveExit); nothing to run."
        exit $serveExit
    }
    $started = $true

    Write-Host "Waiting for the status probe on port $GamePort (timeout ${ReadyTimeoutSec}s) ..."
    $probe = Wait-GameReady -HostName '127.0.0.1' -Port $GamePort -TimeoutSec $ReadyTimeoutSec
    if ($null -eq $probe) {
        Write-Host "Game server did not answer the status probe on port $GamePort within ${ReadyTimeoutSec}s."
        Show-LogTail -Root $CheckoutFull
        $exitCode = 1
    } else {
        Write-Host "Ready: $probe"

        $results = New-Object System.Collections.Generic.List[object]
        foreach ($f in $scriptFiles) {
            Write-Host "Running $(Split-Path -Leaf $f) ..."
            $r = Invoke-TestScript -Dotnet $Dotnet -CliProject $CliProject -ScriptPath $f -LoginHost '127.0.0.1' `
                                    -LoginPort $LoginPort -User $Bot -UserPass $Pass -TimeoutSec $ScriptTimeoutSec
            $results.Add($r)
        }

        Write-Host ""
        Write-Host ("{0,-24} {1,6} {2,6} {3,6} {4,9}  {5}" -f 'SCRIPT', 'EXIT', 'PASS', 'FAIL', 'SECONDS', 'FIRST FAILURE')
        foreach ($r in $results) {
            $exitDisp = if ($r.TimedOut) { 'TIMEOUT' } else { [string]$r.ExitCode }
            Write-Host ("{0,-24} {1,6} {2,6} {3,6} {4,9}  {5}" -f $r.Script, $exitDisp, $r.Passed, $r.Failed, $r.WallClockSec, $r.FirstFailure)
        }
        Write-Host ""

        $allOk = -not (@($results | Where-Object { $_.TimedOut -or $_.ExitCode -ne 0 }).Count -gt 0)
        $passedCount = @($results | Where-Object { -not $_.TimedOut -and $_.ExitCode -eq 0 }).Count
        Write-Host "$passedCount/$($results.Count) scripts exited 0."
        $exitCode = if ($allOk) { 0 } else { 1 }

        if ($Json) {
            $doc = [ordered]@{
                checkout   = $CheckoutFull
                testClient = $TestClientFull
                portBase   = $PortBase
                bot        = $Bot
                ranAt      = (Get-Date).ToString('o')
                results    = @($results | ForEach-Object {
                    [ordered]@{
                        script       = $_.Script
                        exitCode     = $_.ExitCode
                        timedOut     = $_.TimedOut
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
}

exit $exitCode
