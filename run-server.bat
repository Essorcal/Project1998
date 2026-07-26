@echo off
REM Starts the NexusTK server as TWO independent processes, each in its own window:
REM   LOGIN  : ports 2000 (4.95) / 2001 (5.33)  -- account creation, login, handoff to game
REM   GAME   : ports 2005 (4.95) / 2006 (5.33)  -- the world (movement, combat, items, NPCs)
REM They run separately so one can crash or be restarted without taking the other down. The login
REM window is the internet-facing front door; the game window can be closed + relaunched to ship a
REM code change while players stay connected to login and auto-reconnect.
REM
REM Split deployment: set NEXUS_GAME_HOST to the game box's public IP before launching login, so the
REM handoff redirects clients to the right machine (defaults to 127.0.0.1 = same box).
REM
REM Uses the user-local .NET 8 SDK (not on PATH). Close a window (or Ctrl+C in it) to stop that process.
REM dotnet is NOT on PATH (user-local SDK); reference it by full path. Fall back to a PATH dotnet if the
REM hardcoded location ever moves.
set "DOTNET=C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

REM The command after `cmd /k` is wrapped in ONE extra outer pair of quotes: with the quoted dotnet path
REM AND the quoted project path there are 4 quotes, and cmd /k otherwise strips the first+last quote and
REM mangles both paths ("...volume label syntax is incorrect"). The outer pair absorbs that stripping.
start "NexusTK LOGIN (2000/2001)" cmd /k ""%DOTNET%" run --project "%~dp0LoginServer" -- --ports 2000,2001"
start "NexusTK GAME (2005/2006)"  cmd /k ""%DOTNET%" run --project "%~dp0Server" -- --ports 2005,2006"
