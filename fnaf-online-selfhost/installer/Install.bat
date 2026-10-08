@echo off
setlocal
cd /d "%~dp0"
title FNAF Online Self-Host installer
if not exist "FNAF Online Multiplayer.exe" (
  echo.
  echo  This must be run from the game folder - the one that contains "FNAF Online Multiplayer.exe".
  echo  Copy ALL the files from the zip into that folder, then double-click Install.bat again.
  echo.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
