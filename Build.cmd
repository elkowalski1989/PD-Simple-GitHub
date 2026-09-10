@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
set "PD_SIMPLE_EXIT=%ERRORLEVEL%"
if not "%PD_SIMPLE_EXIT%"=="0" pause
exit /b %PD_SIMPLE_EXIT%
