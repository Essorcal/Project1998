@echo off
REM Terrain diagnostic: fill the 5.33 map-stream (0x06) with a ground-index RAMP across the whole
REM 16-bit tile range (0..28550) over the visible rectangle. Wherever a valid tile exists in the
REM 5.x TILE.EPF, that cell draws it -> tells us which index range is live. Screenshot the result.
set P1998_MAP_DIAG=sweep
set DOTNET="C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
%DOTNET% run --project "%~dp0..\Server" -- --ports 2000,2005,2001,2006
pause
