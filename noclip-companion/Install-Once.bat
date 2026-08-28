@echo off
title No-Clip Companion - one-time setup
echo ============================================================
echo   NexusTK No-Clip Companion - one-time setup
echo   This installs "Frida", the only thing the companion needs.
echo ============================================================
echo.

REM Prefer the "py" launcher (a real python.org install) over "python", which on some PCs is just
REM the Microsoft Store stub that opens the Store instead of running.
set PY=
where py >nul 2>nul && set PY=py
if not defined PY ( where python >nul 2>nul && set PY=python )

if not defined PY (
  echo Python was NOT found on this PC.
  echo.
  echo   1^) Install Python 3 from https://www.python.org/downloads/
  echo      IMPORTANT: tick "Add Python to PATH" on the first install screen.
  echo   2^) Then run this Install-Once.bat again.
  echo.
  pause
  exit /b 1
)

echo Using %PY%. Installing Frida ...
echo.
%PY% -m pip install --upgrade frida
echo.
if errorlevel 1 (
  echo Something went wrong installing Frida. Check your internet connection
  echo and see README.txt, then try again.
) else (
  echo Done. You can close this window and run Start-NoClip.bat to play.
)
echo.
pause
