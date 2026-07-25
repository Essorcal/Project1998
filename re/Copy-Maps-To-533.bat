@echo off
REM Copy 4.x maps into the 5.33 install to TEST whether the client reads local map files.
REM Right-click -> Run as administrator (writes into Program Files).
setlocal
set "S=C:\Program Files (x86)\Nexon\NextAeon\Maps"
set "D=C:\Program Files (x86)\Nexon\NextAeon5\Maps"

if not exist "%D%" mkdir "%D%"
echo Copying all TK*.map ...
robocopy "%S%" "%D%" *.map /NFL /NDL /NJH /NJS /NP >nul

REM Spawn map (id 32) under every plausible 5.33 name/format (all = raw 4.x geometry)
copy /Y "%D%\TK32.map" "%D%\TK000032.cmp" >nul
copy /Y "%D%\TK32.map" "%D%\TK000032.map" >nul
copy /Y "%D%\TK32.map" "%D%\C0032.MAP"    >nul
copy /Y "%D%\TK32.map" "%D%\TK32.cmp"      >nul

echo.
echo Done. Spawn-map variants in %D%:
dir /b "%D%\TK000032.*" "%D%\C0032.MAP" "%D%\TK32.cmp"
echo.
pause
