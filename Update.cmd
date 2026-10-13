@echo off
setlocal
if exist "%~dp0meetus.exe" (
    start "Meetus Privatus Update" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update.ps1" -Pause
    exit /b
)
if exist "%~dp0.artifacts\publish\meetus.exe" (
    start "Meetus Privatus Update" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0.artifacts\publish\Update.ps1" -Pause
    exit /b
)
echo Extract the complete release before running Update.cmd.
pause
exit /b 1
