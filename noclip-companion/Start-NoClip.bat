@echo off
title NexusTK No-Clip Companion
cd /d "%~dp0"

REM Prefer the "py" launcher (a real python.org install) over "python", which on some PCs is just
REM the Microsoft Store stub that opens the Store instead of running.
set PY=
where py >nul 2>nul && set PY=py
if not defined PY ( where python >nul 2>nul && set PY=python )

if not defined PY (
  echo Python was NOT found. Run Install-Once.bat first ^(see README.txt^).
  echo.
  pause
  exit /b 1
)

echo Starting the No-Clip companion. Keep this window open while you play.
echo Log in, then type  @clip  in game to toggle walls on/off.
echo Close this window when you are done to restore normal collision.
echo.
%PY% "%~dp0frida_noclip_533.py"

echo.
echo (companion stopped - normal collision restored)
pause
