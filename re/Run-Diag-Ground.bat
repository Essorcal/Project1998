@echo off
REM Fills the whole view with the 4.x ground WORD given as %1, put through the REAL translation.
REM
REM Unlike Run-Diag-Solid.bat (which streams a raw sheet index straight to the client), this exercises
REM the sheet selector, so it covers the ~30% of the world that lives on the second legacy sheet:
REM
REM     Run-Diag-Ground.bat 652      sheet 1 -> TileA[651]      (translation is identity here)
REM     Run-Diag-Ground.bat 49152    sheet 2 -> TileB[0]        (0xC000; goes through Tile533Map.csv)
REM     Run-Diag-Ground.bat 51448    sheet 2 -> TileB[2296]
REM
REM CAVEAT, learned the hard way: a uniform fill proves less than it looks like it does. Tile sheets
REM group related terrain, so several consecutive frames are often the same material and one screenshot
REM can be consistent with three different offsets at once. This is a smoke test, not a proof. The
REM authoritative check is the offline both-pipelines render comparison (docs\5.x\Reverse-Engineering.md),
REM which compares all 1.7M cells of all 1,750 maps without involving the client at all.
if "%~1"=="" (
  echo usage: Run-Diag-Ground.bat ^<groundWord^>    e.g. 652 ^(sheet 1^) or 49152 ^(sheet 2^)
  exit /b 1
)
set P1998_MAP_DIAG=ground:%~1
set DOTNET="C:\Users\brian\AppData\Local\Microsoft\dotnet\dotnet.exe"
%DOTNET% run --project "%~dp0..\Server" -- --ports 2000,2005,2001,2006
pause
