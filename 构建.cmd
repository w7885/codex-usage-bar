@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build.ps1"
if errorlevel 1 (
  echo Build failed. Read the error above.
  pause
  exit /b 1
)
echo Build complete. Run the launcher to start the usage bar.
pause
