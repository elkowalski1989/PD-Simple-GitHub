@echo off
rem License watch: opens a disposable board copy in owned Allegro and waits
rem for the board to open (needs a PCB license seat). Leaves Allegro running
rem with the board open on success and writes allegro-ready.txt for the
rem connected campaign. Fully autonomous.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Watch-License.ps1"
exit /b %errorlevel%
