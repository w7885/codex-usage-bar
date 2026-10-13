@echo off
"%~dp0dist\CodexUsageBar.exe" --install
if errorlevel 1 exit /b 1
start "" "%~dp0dist\CodexUsageBar.exe"
