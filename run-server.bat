@echo off
REM Rebuilds (if needed) and starts the NexusTK server on login=2000, game=2005.
REM Uses the user-local .NET 8 SDK (not on PATH). Ctrl+C to stop.
set DOTNET="C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
%DOTNET% run --project "%~dp0Server" -- --ports 2000,2005
pause
