@echo off
setlocal
if exist "%~dp0meetus.exe" (
    start "Meetus Privatus" "%~dp0meetus.exe" run
    exit /b
)
if exist "%~dp0.artifacts\publish\meetus.exe" (
    start "Meetus Privatus" "%~dp0.artifacts\publish\meetus.exe" run
    exit /b
)
echo Meetus Privatus has not been built. See README.md for installation instructions.
pause
exit /b 1
