@echo off
REM Tunes the 4.95 self-walk commit delay (ms between the 0x0C animate and the 0x04 commit).
REM Pass a value as arg1, e.g.  Run-V495-WalkMs.bat 90
REM 0 = old same-frame behavior (instant slide, no animation).
REM Set here (inside the .bat) because PowerShell's `set VAR=val` does NOT set a real env var
REM (it's aliased to Set-Variable) -- child processes like dotnet.exe never see it. Use $env:VAR="x"
REM if you want to set it directly in PowerShell instead of using this launcher.
set MS=%1
if "%MS%"=="" set MS=150
set P1998_V495_WALK_MS=%MS%
set DOTNET="C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
%DOTNET% run --project "%~dp0..\Server" -- --ports 2000,2005,2001,2006
pause
