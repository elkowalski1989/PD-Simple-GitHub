[CmdletBinding()]
param([string] $BoardPath = '', [int] $WaitMinutes = 4, [switch] $LeaveRunning)

# One-off cause probe: launch owned Allegro with a fully isolated profile
# (fresh HOME/SPB_DATA/USERPROFILE/TEMP, blank allegro.ilinit, SI batch mode)
# to separate profile/startup-hook causes from license/environment causes.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$allegroExe = 'C:\Cadence\SPB_25.1\tools\bin\allegro.exe'
if ($BoardPath -eq '') { $BoardPath = 'C:\Users\EMILEKOWALSKI\Desktop\boards\ingram9z_040324.brd' }
$runRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-isolated-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

foreach ($file in @($allegroExe, $BoardPath)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
$stale = @(Get-Process -Name 'allegro' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stale.Count -gt 0) { throw "An Allegro is already running (PID $($stale[0].Id)). Nothing was touched." }

[void](New-Item -ItemType Directory -Path $runRoot -Force)
$profileRoot = Join-Path $runRoot 'profile'
$tempRoot = Join-Path $profileRoot 'Temp'
$userProfile = Join-Path $profileRoot 'User'
$localAppData = Join-Path $userProfile 'AppData\Local'
$appData = Join-Path $userProfile 'AppData\Roaming'
$spbData = Join-Path $appData 'SPB_Data'
foreach ($directory in @($tempRoot, $localAppData, $appData, $spbData)) {
    [void](New-Item -ItemType Directory -Path $directory -Force)
}
$inputRoot = Join-Path $runRoot 'input'
[void](New-Item -ItemType Directory -Path $inputRoot -Force)
$scratchBoard = Join-Path $inputRoot 'campaign.brd'
Copy-Item -LiteralPath $BoardPath -Destination $scratchBoard
[void](New-Item -ItemType Directory -Path (Join-Path $spbData 'pcbenv') -Force)
[void](New-Item -ItemType Directory -Path (Join-Path $userProfile 'pcbenv') -Force)
';;; isolated probe: no startup hooks.' | Set-Content -LiteralPath (Join-Path $spbData 'pcbenv\allegro.ilinit') -Encoding ascii
';;; isolated probe: no startup hooks.' | Set-Content -LiteralPath (Join-Path $userProfile 'pcbenv\allegro.ilinit') -Encoding ascii
';;; isolated probe: no startup hooks.' | Set-Content -LiteralPath (Join-Path $inputRoot 'allegro.ilinit') -Encoding ascii

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $allegroExe
$startInfo.WorkingDirectory = $inputRoot
$startInfo.UseShellExecute = $false
$arguments = @('-product', 'Allegro_performance', '-p', $inputRoot,
    '-j', (Join-Path $runRoot 'allegro.jrl'), $scratchBoard)
foreach ($value in $arguments) {
    if ($value.Contains('"')) { throw 'Argument quote not supported.' }
}
$startInfo.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$driveRoot = [System.IO.Path]::GetPathRoot($userProfile)
$homeDrive = $driveRoot.TrimEnd('\')
$startInfo.EnvironmentVariables['LOCALAPPDATA'] = $localAppData
$startInfo.EnvironmentVariables['APPDATA'] = $appData
$startInfo.EnvironmentVariables['USERPROFILE'] = $userProfile
$startInfo.EnvironmentVariables['HOME'] = $spbData
$startInfo.EnvironmentVariables['SPB_DATA'] = $spbData
$startInfo.EnvironmentVariables['HOMEDRIVE'] = $homeDrive
$startInfo.EnvironmentVariables['HOMEPATH'] = $userProfile.Substring($homeDrive.Length)
$startInfo.EnvironmentVariables['TEMP'] = $tempRoot
$startInfo.EnvironmentVariables['TMP'] = $tempRoot
$startInfo.EnvironmentVariables['SI_TOOLKIT_BATCH_MODE'] = '1'

$allegro = New-Object System.Diagnostics.Process
$allegro.StartInfo = $startInfo
if (-not $allegro.Start()) { throw 'Allegro did not start.' }
Write-Host "Isolated Allegro PID $($allegro.Id)."
try {
    $windowDeadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $windowDeadline) {
        if ($allegro.HasExited) { throw "Allegro exited during startup (exit $($allegro.ExitCode))." }
        $allegro.Refresh()
        if ($allegro.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($allegro.MainWindowHandle -eq 0) { throw 'No Allegro window within three minutes.' }
    $openDeadline = (Get-Date).AddMinutes($WaitMinutes)
    while ((Get-Date) -lt $openDeadline) {
        if ($allegro.HasExited) { throw "Allegro exited while opening (exit $($allegro.ExitCode))." }
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $allegro.MainWindowHandle)
        if ($root.Current.Name -like '*.brd*') {
            Write-Host ('ISOLATED BOARD OPEN: ' + $root.Current.Name)
            @($allegro.Id, $runRoot, $scratchBoard) | Set-Content -LiteralPath (Join-Path $runRoot 'allegro-ready.txt') -Encoding utf8
            return
        }
        Start-Sleep -Seconds 5
    }
    throw 'Isolated board open did not complete within the window.'
}
finally {
    if ($LeaveRunning -and -not $allegro.HasExited) {
        Write-Host "Leaving isolated Allegro PID $($allegro.Id) running."
    }
    elseif (-not $allegro.HasExited) {
        $allegro.CloseMainWindow() | Out-Null
        if (-not $allegro.WaitForExit(20000)) { $allegro.Kill(); $allegro.WaitForExit() }
        Write-Host 'Isolated Allegro closed.'
    }
    $allegro.Dispose()
}
