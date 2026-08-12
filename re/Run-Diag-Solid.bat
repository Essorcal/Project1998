@echo off
REM Terrain diagnostic: fill every cell with ONE ground index (default 651, the real map's floor).
REM Pass an index as arg1, e.g.  Run-Diag-Solid.bat 5000
set IDX=%1
if "%IDX%"=="" set IDX=651
set P1998_MAP_DIAG=solid:%IDX%
set DOTNET="C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
%DOTNET% run --project "%~dp0..\Server" -- --ports 2000,2005,2001,2006
pause
