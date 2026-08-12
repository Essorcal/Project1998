@echo off
REM Passability test: floor 651 everywhere (all visible), but pass=N on vertical wall-lines every 5
REM tiles (map columns where x % 5 == 2). Same tile graphic on wall/non-wall cells, so:
REM   - if the character gets BLOCKED at those columns  -> the client enforces collision from `pass`.
REM   - if those columns look/draw differently          -> `pass` also affects rendering.
REM   - if nothing changes at all                       -> the client ignores `pass` (server must gate moves).
REM Try several block values, e.g.  Run-Diag-PassTest.bat 1   /  2   /  3   /  32768
set PVAL=%1
if "%PVAL%"=="" set PVAL=1
set P1998_MAP_DIAG=passtest:%PVAL%
set DOTNET="C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
%DOTNET% run --project "%~dp0..\Server" -- --ports 2000,2005,2001,2006
pause
