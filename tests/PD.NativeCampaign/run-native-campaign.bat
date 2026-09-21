@echo off
rem Native campaign: one-click autonomous 12-tool run against live Allegro.
rem Step 1 opens a disposable board copy in owned Allegro (needs a PCB
rem license seat) and writes allegro-ready.txt; step 2 launches PD,
rem connects, drives each tool's representative live action, and saves
rem PD (in-app) plus Allegro (window-only, occlusion-guarded) evidence
rem per step. No prompts; exit code is non-zero on any failure.
call "%~dp0run-licensewatch.bat"
if errorlevel 1 exit /b %errorlevel%
call "%~dp0run-connected-campaign.bat"
exit /b %errorlevel%
