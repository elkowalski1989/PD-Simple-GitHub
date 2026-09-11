@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0rebuild-with-local-bridge.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo.
    echo Rebuild failed with exit code %RC%.
    pause
)
exit /b %RC%
