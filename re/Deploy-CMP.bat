@echo off
REM Install the converted TK000032.cmp into the 5.33 client's Maps folder.
REM Right-click -> Run as administrator.
set "D=C:\Program Files (x86)\Nexon\NextAeon5\Maps"
if not exist "%D%" mkdir "%D%"
copy /Y "%~dp0TK000032.cmp" "%D%\TK000032.cmp"
echo.
echo Installed TK000032.cmp -> %D%
dir /b "%D%\TK000032.cmp"
pause
