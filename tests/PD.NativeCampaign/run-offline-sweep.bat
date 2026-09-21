@echo off
rem Offline sweep: launches PD disconnected, visits all 12 tools plus
rem corridor/explorer/home, saves tree+text+screenshot per tool. No Allegro,
rem no license needed. Fully autonomous.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sweep-Tools.ps1" -LaunchPd
exit /b %errorlevel%
