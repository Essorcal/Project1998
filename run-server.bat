@echo off
REM Rebuilds (if needed) and starts the unified NexusTK server.
REM   2000/2005 = 4.95 login/game (V495)    2001/2006 = 5.33 login/game (V533)
REM The server tags each connection's client version by the port it arrived on.
REM Uses the user-local .NET 8 SDK (not on PATH). Ctrl+C to stop.
set DOTNET="C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
%DOTNET% run --project "%~dp0Server" -- --ports 2000,2005,2001,2006
pause
