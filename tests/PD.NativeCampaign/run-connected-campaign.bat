@echo off
rem Connected campaign: requires allegro-ready.txt from run-licensewatch
rem (owned Allegro with the board open). Launches PD, connects, drives each
rem of the 12 tools' representative live action, and saves PD (in-app) plus
rem Allegro (window-only, occlusion-guarded) evidence per step. Fully
rem autonomous.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-ConnectedCampaign.ps1"
pause
