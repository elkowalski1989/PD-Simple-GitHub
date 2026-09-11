@echo off
setlocal EnableDelayedExpansion

:: ============================================================
::  Rebuild PD-Simple dev bundle WITH bundled license key
:: ============================================================

set "BRIDGE=C:\e2studio\allegro-bridge"
set "CONSUMER=C:\Users\EMILEKOWALSKI\Desktop\boards\PD-Simple-GitHub"
set "SIGNING_KEYS=%BRIDGE%\_local-runs\release-inputs\CircuitHubSigningKeys.json"
set "LICENSE_KEY=%BRIDGE%\_local-runs\release-inputs\CircuitHubBundledLicenseKey.txt"
set "VERSION=1.13.0-preview.3"
set "PD_SIMPLE_EXE=%CONSUMER%\src\PD.Simple\bin\Release\net10.0-windows\PD.Simple.exe"

echo.
echo ============================================================
echo  Rebuild with bundled license -- %VERSION%
echo ============================================================
echo.

:: ---- preflight ----
if not exist "%SIGNING_KEYS%" (
    echo FAIL: Missing signing keys: %SIGNING_KEYS%
    pause & exit /b 1
)
if not exist "%LICENSE_KEY%" (
    echo FAIL: Missing bundled license key: %LICENSE_KEY%
    pause & exit /b 1
)

:: ---- step 1: clear stale preview nupkgs ----
echo [1/5] Clearing stale packages for %VERSION%...
powershell -NoProfile -Command ^
    "Get-ChildItem '%CONSUMER%\packages' -Filter '*.nupkg' | Where-Object { $_.Name -match '1\.13\.0-preview\.' } | ForEach-Object { Remove-Item $_.FullName -Force; Write-Host ('  Removed: ' + $_.Name) }"
echo   Done.
echo.

:: ---- step 2: add BundledLicenseKeyPath param to bundle script if missing ----
echo [2/5] Patching build-development-bundle.ps1 to accept -BundledLicenseKeyPath...
powershell -NoProfile -Command ^
    "$f = '%BRIDGE%\scripts\build-development-bundle.ps1'; $t = Get-Content $f -Raw; if ($t -notmatch 'BundledLicenseKeyPath') { $t = $t -replace '(\[string\] \$ConsumerRoot)', '$1,`n    [string] `$BundledLicenseKeyPath'; $t = $t -replace '(\$noBundledCredential = Join-Path \$output .NO-BUNDLED-LICENSE-KEY\.txt.)', '$1`nif (-not [string]::IsNullOrWhiteSpace(`$BundledLicenseKeyPath)) { `$BundledLicenseKeyPath = [IO.Path]::GetFullPath(`$BundledLicenseKeyPath); if (-not (Test-Path -LiteralPath `$BundledLicenseKeyPath -PathType Leaf)) { throw \"BundledLicenseKeyPath not found\" }; `$noBundledCredential = `$BundledLicenseKeyPath }'; Set-Content $f $t -Encoding UTF8; Write-Host \"  Patched.'\" } else { Write-Host '  Already has parameter.' }"
echo   Done.
echo.

:: ---- step 3: build dev bundle with license ----
echo [3/5] Building dev bundle with bundled license...
cd /d "%BRIDGE%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%BRIDGE%\scripts\build-development-bundle.ps1" ^
    -Version "%VERSION%" ^
    -SigningKeysPath "%SIGNING_KEYS%" ^
    -BundledLicenseKeyPath "%LICENSE_KEY%" ^
    -ConsumerRoot "%CONSUMER%"
if %ERRORLEVEL% neq 0 ( echo FAIL: bundle build failed & pause & exit /b 1 )
echo.

:: ---- step 4: build PD.Simple ----
echo [4/5] Closing any running PD.Simple, then building...
taskkill /IM PD.Simple.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
cd /d "%CONSUMER%"
dotnet build src\PD.Simple\PD.Simple.csproj -c Release
if %ERRORLEVEL% neq 0 ( echo FAIL: PD.Simple build failed & pause & exit /b 1 )
echo.

if not exist "%PD_SIMPLE_EXE%" (
    echo FAIL: PD.Simple.exe not found after build.
    pause & exit /b 1
)

:: ---- step 5: update installed SKILL loader version token ----
echo [5/5] Updating installed pd_simple_loader.il to %VERSION%...
powershell -NoProfile -Command ^
    "$f = \"$env:LOCALAPPDATA\PD-Simple\pd_simple_loader.il\"; if (Test-Path $f) { $t = Get-Content $f -Raw; $t = $t -replace '1\.\d+\.\d+-preview\.\d+', '%VERSION%'; Set-Content $f $t -Encoding ASCII; Write-Host '  Updated.' } else { Write-Host '  WARNING: pd_simple_loader.il not found -- run the PD Simple installer first.' }"
echo.

echo ============================================================
echo  Done. Fully exit and reopen Allegro, then open a board.
echo ============================================================
echo.
pause
