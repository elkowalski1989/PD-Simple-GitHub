@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
set "PD_SIMPLE_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %PD_SIMPLE_EXIT%
