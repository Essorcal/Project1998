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
REM Close a window (or Ctrl+C in it) to stop that process.
REM
REM Finding dotnet: a PATH install is the normal case and is tried first. The fallbacks cover the
REM user-local SDK layout, where the installer does NOT put dotnet on PATH -- a machine can have a
REM perfectly good .NET 8 and still fail here with "'dotnet' is not recognized". Set P1998_DOTNET if
REM yours is somewhere else again.
set "DOTNET=%P1998_DOTNET%"
if not defined DOTNET (where dotnet >nul 2>&1 && set "DOTNET=dotnet")
if not defined DOTNET if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET (
    echo *** No .NET SDK found. Install .NET 8 from https://dotnet.microsoft.com/download ***
    echo *** or set P1998_DOTNET to the full path of dotnet.exe.                          ***
    pause
    exit /b 1
)

REM Build the whole solution ONCE, up front, before launching anything. Two reasons:
REM   1. Fail-fast: if the code doesn't compile we stop here and show the errors, instead of opening two
REM      server windows that each spew a build failure.
REM   2. No build race: `dotnet run` builds on its own, so launching both windows at once had them BOTH
REM      rebuild the shared `Shared` project in parallel and collide on its obj cache
REM      ("Shared.AssemblyInfoInputs.cache ... being used by another process"). Building here once, then
REM      launching with --no-build, removes that race entirely.
echo Building Project1998.sln ...
"%DOTNET%" build "%~dp0Project1998.sln" -v:m -nologo
if errorlevel 1 (
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
start "NexusTK LOGIN (2000/2001)" cmd /k ""%DOTNET%" run --no-build --project "%~dp0LoginServer" -- --ports 2000,2001"
start "NexusTK GAME (2005/2006)"  cmd /k ""%DOTNET%" run --no-build --project "%~dp0Server" -- --ports 2005,2006"
