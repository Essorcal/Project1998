@echo off
REM Starts the NexusTK server as TWO independent processes, each in its own window:
REM   LOGIN  : ports 2000 (4.95) / 2001 (5.33)  -- account creation, login, handoff to game
REM   GAME   : ports 2005 (4.95) / 2006 (5.33)  -- the world (movement, combat, items, NPCs)
REM They run separately so one can crash or be restarted without taking the other down. The login
REM window is the internet-facing front door; the game window can be closed + relaunched to ship a
REM code change while players stay connected to login and auto-reconnect.
REM
REM Split deployment: set P1998_GAME_HOST to the game box's public IP before launching login, so the
REM handoff redirects clients to the right machine (defaults to 127.0.0.1 = same box).
REM
REM Optional first argument: the login port base. For example, `run-server.bat 3000` binds login to
REM 3000/3001 and game to 3005/3006. With no argument the original 2000/2001 + 2005/2006 pair is used.
REM
REM Close a window (or Ctrl+C in it) to stop that process.
REM
REM Finding dotnet: what we need is an SDK, and merely FINDING a dotnet.exe does not give us one.
REM `C:\Program Files\dotnet` on PATH is very often a runtime-only install (bundled by some other app,
REM or what an SDK uninstall leaves behind). It answers `where dotnet` perfectly happily and then fails
REM every build with "No .NET SDKs were found" -- so each candidate is PROBED with --list-sdks and only
REM accepted if it reports one. The user-local layout (%LOCALAPPDATA%\Microsoft\dotnet) is checked too:
REM that installer does NOT put dotnet on PATH, so a machine can have a perfectly good .NET 8 SDK that
REM `where dotnet` never finds. Set P1998_DOTNET to the full path of a dotnet.exe if yours is elsewhere.
REM If nothing on the box has one, :get_dotnet offers to fetch a private copy into .dotnet\ -- so a
REM fresh clone on a machine with no .NET at all can still start the server.
REM Suppress the .NET "first run experience", which fires on the first invocation of an SDK this
REM machine has not used before -- including our --list-sdks probe. Left alone it prints a page of
REM welcome text and INSTALLS AN ASP.NET HTTPS DEVELOPMENT CERTIFICATE into the user's certificate
REM store: a side effect outside .dotnet\ that survives deleting it, for a feature this server does
REM not use. Telemetry is deliberately NOT touched here -- that is the operator's call, not ours.
set "DOTNET_NOLOGO=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=0"
set "P1998_ROOT=%~dp0"
set "P1998_DOTNET_DIR=%P1998_ROOT%.dotnet"
set "PORT_BASE=%~1"
if not defined PORT_BASE set "PORT_BASE=2000"
set /a "LOGIN_533=PORT_BASE+1"
set /a "GAME_495=PORT_BASE+5"
set /a "GAME_533=PORT_BASE+6"
set "DOTNET="
if defined P1998_DOTNET (
    call :try_dotnet "%P1998_DOTNET%"
    if not defined DOTNET echo *** P1998_DOTNET has no .NET SDK -- ignoring it and looking elsewhere. ***
)
call :try_dotnet "%P1998_DOTNET_DIR%\dotnet.exe"
if not defined DOTNET for /f "delims=" %%d in ('where dotnet 2^>nul') do call :try_dotnet "%%d"
if not defined DOTNET call :try_dotnet "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET call :try_dotnet "%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET call :get_dotnet
if not defined DOTNET (
    echo *** No .NET 8 SDK found -- a runtime-only dotnet does not count, and cannot build. ***
    echo *** Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 ***
    echo *** or set P1998_DOTNET to the full path of a dotnet.exe that has one.           ***
    pause
    exit /b 1
)

REM Point the runtime host at the SDK we just chose. `dotnet run` starts the built apphost
REM (Server.exe), and an apphost looks for its runtime in DOTNET_ROOT, then the registry, then
REM C:\Program Files\dotnet -- so without this it can pick the MACHINE-WIDE install, which may have no
REM .NET 8 runtime at all. It then exits 150 with "You must install or update .NET" before writing a
REM single log line, which reads as the server crashing rather than as a launcher problem. Doubly so
REM for the .dotnet\ copy below, which the registry knows nothing about.
for %%i in ("%DOTNET%") do set "DOTNET_ROOT=%%~dpi"
if "%DOTNET_ROOT:~-1%"=="\" set "DOTNET_ROOT=%DOTNET_ROOT:~0,-1%"

REM Build the whole solution ONCE, up front, before launching anything. Two reasons:
REM   1. Fail-fast: if the code doesn't compile we stop here and show the errors, instead of opening two
REM      server windows that each spew a build failure.
REM   2. No build race: `dotnet run` builds on its own, so launching both windows at once had them BOTH
REM      rebuild the shared `Shared` project in parallel and collide on its obj cache
REM      ("Shared.AssemblyInfoInputs.cache ... being used by another process"). Building here once, then
REM      launching with --no-build, removes that race entirely.
echo Building Project1998.sln ...
"%DOTNET%" build "%~dp0Project1998.sln" -v:m -nologo
REM The check below is a string compare against 0, NOT `if errorlevel 1`: the latter is a >= test and
REM the dotnet host can exit NEGATIVE (0x8000809B = -2147450725 when it has no SDK), which sails
REM straight past a >= 1 test. That is exactly how a failed build used to print "Build OK" and open two
REM server windows anyway. Any non-zero code is a failure here.
if not "%ERRORLEVEL%"=="0" (
    echo.
    echo *** BUILD FAILED -- server not started. Fix the errors above and re-run. ***
    pause
    exit /b 1
)
echo Build OK -- starting login + game servers.

REM The command after `cmd /k` is wrapped in ONE extra outer pair of quotes: with the quoted dotnet path
REM AND the quoted project path there are 4 quotes, and cmd /k otherwise strips the first+last quote and
REM mangles both paths ("...volume label syntax is incorrect"). The outer pair absorbs that stripping.
REM --no-build: we already built the solution above, so each window just runs the fresh binaries.
start "NexusTK LOGIN (%PORT_BASE%/%LOGIN_533%)" cmd /k ""%DOTNET%" run --no-build --project "%~dp0LoginServer" -- --ports %PORT_BASE%,%LOGIN_533%"
start "NexusTK GAME (%GAME_495%/%GAME_533%)"  cmd /k ""%DOTNET%" run --no-build --project "%~dp0Server" -- --ports %GAME_495%,%GAME_533%"

REM Done -- everything below is subroutines, so stop before falling into them.
exit /b 0

REM :try_dotnet <path to dotnet.exe>
REM Accepts the candidate as DOTNET only if it exists AND reports an installed .NET 8 SDK. Not "any
REM SDK": global.json pins the 8.0 band, so a dotnet with only a 9.x or 6.x SDK would be accepted here
REM and then fail the build with a version error -- refuse it up front instead, so the message the
REM user sees is the one at the top ("install the .NET 8 SDK"). First winner wins -- once DOTNET is
REM set every later call is a no-op, so the call order above is simply the preference order.
:try_dotnet
if defined DOTNET goto :eof
if not exist "%~1" goto :eof
"%~1" --list-sdks 2>nul | findstr /r "^8\." >nul
if errorlevel 1 goto :eof
set "DOTNET=%~1"
goto :eof

REM :get_dotnet
REM Last resort, reached only when the machine has no SDK anywhere: fetch one from Microsoft into
REM .dotnet\ beside this script, using Microsoft's own dotnet-install.ps1. Deliberately self-contained
REM -- no administrator rights, no PATH or registry change, no admin-visible install, and
REM .dotnet\ is gitignored, so deleting that one folder undoes the whole thing. The probe above finds it
REM first on every later run, so this happens once per clone and never again.
REM   P1998_NO_INSTALL=1  refuse outright and just report the problem (CI, locked-down boxes)
REM   P1998_AUTO_INSTALL=1  skip the prompt and install (unattended setup)
:get_dotnet
if defined P1998_NO_INSTALL goto :eof
echo.
echo No .NET SDK was found on this machine -- only runtimes, or nothing at all.
echo.
echo This script can download the .NET 8 SDK from Microsoft and install a private copy into
echo     %P1998_DOTNET_DIR%
echo Roughly a 250 MB download, ~700 MB on disk. No administrator rights, no PATH or registry
echo change, and nothing else on the machine learns about it. Delete the .dotnet folder to undo
echo it. (Restored NuGet packages still cache in %%USERPROFILE%%\.nuget, as they do for any build.)
echo.
if defined P1998_AUTO_INSTALL goto :run_install
choice /c YN /n /m "Download and install it now? [Y/N] "
if errorlevel 2 (
    echo Skipped.
    goto :eof
)
:run_install
echo.
echo Fetching https://dot.net/v1/dotnet-install.ps1 ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $s=Join-Path $env:TEMP 'dotnet-install.ps1'; Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $s; & $s -Channel 8.0 -InstallDir '%P1998_DOTNET_DIR%' -NoPath"
if not "%ERRORLEVEL%"=="0" (
    echo.
    echo *** The .NET SDK install failed -- see the errors above. Nothing was started. ***
    goto :eof
)
call :try_dotnet "%P1998_DOTNET_DIR%\dotnet.exe"
if not defined DOTNET echo *** Installed, but %P1998_DOTNET_DIR%\dotnet.exe still reports no SDK. ***
goto :eof
