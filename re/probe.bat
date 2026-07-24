@echo off
REM ================================================================
REM  Live Frida probe of the NexusTK 4.95 client.
REM  RIGHT-CLICK this file -> "Run as administrator".
REM  (The client runs elevated via its WINXPSP2 compat shim, so
REM   Frida must be elevated too, or spawn fails with 0x2e4.)
REM
REM  Make sure the C# server is already running first.
REM  After the client window opens: log in and walk to the game
REM  server. Everything streams to re\probe_log.txt.
REM ================================================================
cd /d "%~dp0.."
set PY=C:\Users\brian\AppData\Local\Programs\Python\Python314\python.exe
echo Launching client under Frida (elevated)...
"%PY%" re\frida_probe.py
echo.
echo Probe stopped. Log saved to re\probe_log.txt
pause
