[CmdletBinding()]
param([string] $BoardPath = '', [int] $WaitMinutes = 8)

# License watch: open a disposable board copy in owned Allegro under an
# isolated profile (no user startup hooks, no installed-PD auto-open), with
# the worktree .94 resident bridge loaded via replay. Readiness = board title
# plus the resident-published bridge pointer. Leaves Allegro running with the
# board open on success and writes allegro-ready.txt for the connected
# campaign. Fully autonomous.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$allegroExe = 'C:\Cadence\SPB_25.1\tools\bin\allegro.exe'
$resident = 'C:\e2studio\worktrees\pd-coordinator\src\PD.Simple\bin\Release\net10.0-windows\AllegroBridge\Resident\pd_allegro_bridge.il'
$pdExe = 'C:\e2studio\worktrees\pd-coordinator\src\PD.Simple\bin\Release\net10.0-windows\PD.Simple.exe'
$campaignLoader = Join-Path $PSScriptRoot 'skill\pd_campaign_loader.il'
if ($BoardPath -eq '') { $BoardPath = 'C:\Users\EMILEKOWALSKI\Desktop\boards\blyth1z_unrouted.brd' }
$runRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-licensewatch-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

foreach ($file in @($allegroExe, $resident, $campaignLoader, $pdExe, $BoardPath)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
$stale = @(Get-Process -Name 'allegro' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stale.Count -gt 0) { throw "An Allegro is already running (PID $($stale[0].Id)). Nothing was touched." }
$stalePd = @(Get-Process -Name 'PD.Simple' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stalePd.Count -gt 0) { throw "A PD is already running (PID $($stalePd[0].Id)). Nothing was touched." }
$startedAt = Get-Date

[void](New-Item -ItemType Directory -Path $runRoot -Force)
$logPath = Join-Path $runRoot 'watch.log'
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    $profileRoot = Join-Path $runRoot 'profile'
    $tempRoot = Join-Path $profileRoot 'Temp'
    $userProfile = Join-Path $profileRoot 'User'
    $localAppData = Join-Path $userProfile 'AppData\Local'
    $appData = Join-Path $userProfile 'AppData\Roaming'
    $spbData = Join-Path $appData 'SPB_Data'
    foreach ($directory in @($tempRoot, $localAppData, $appData, $spbData)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    [void](New-Item -ItemType Directory -Path (Join-Path $spbData 'pcbenv') -Force)
    [void](New-Item -ItemType Directory -Path (Join-Path $userProfile 'pcbenv') -Force)
    $inputRoot = Join-Path $runRoot 'input'
    [void](New-Item -ItemType Directory -Path $inputRoot -Force)
    $scratchBoard = Join-Path $inputRoot 'campaign.brd'
    Copy-Item -LiteralPath $BoardPath -Destination $scratchBoard
    ';;; campaign: isolated profile, worktree resident via replay.' |
        Set-Content -LiteralPath (Join-Path $spbData 'pcbenv\allegro.ilinit') -Encoding ascii
    ';;; campaign: isolated profile, worktree resident via replay.' |
        Set-Content -LiteralPath (Join-Path $userProfile 'pcbenv\allegro.ilinit') -Encoding ascii
    ';;; campaign: isolated profile, worktree resident via replay.' |
        Set-Content -LiteralPath (Join-Path $inputRoot 'allegro.ilinit') -Encoding ascii

    $skillResident = $resident.Replace('\', '/')
    $skillLoader = $campaignLoader.Replace('\', '/')
    if ($skillResident.Contains('"') -or $skillLoader.Contains('"')) {
        throw 'A SKILL path has an unsupported quote.'
    }
    # The resident must start AFTER the board opens (pdab_start requires a
    # design), so the replay only loads code; the campaign loader's open
    # trigger starts the bridge, which auto-launches PD with bridge context.
    $replayPath = Join-Path $runRoot 'load-resident.scr'
    @('skill (load "' + $skillResident + '")',
        'skill (load "' + $skillLoader + '")', '') -join "`n" |
        Set-Content -LiteralPath $replayPath -Encoding ascii -NoNewline
    $shotRoot = Join-Path $runRoot 'pd-shots'
    [void](New-Item -ItemType Directory -Path $shotRoot -Force)

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $allegroExe
    $startInfo.WorkingDirectory = $inputRoot
    $startInfo.UseShellExecute = $false
    $arguments = @('-product', 'Allegro_performance', '-p', $inputRoot,
        '-j', (Join-Path $runRoot 'allegro.jrl'), '-s', $replayPath, $scratchBoard)
    foreach ($value in $arguments) {
        if ($value.Contains('"')) { throw 'A launch argument has an unsupported quote.' }
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
    $startInfo.EnvironmentVariables['CIRCUITHUB_ALLEGRO_BRIDGE_CONTROL_UI_EXE'] = $pdExe
    $startInfo.EnvironmentVariables['PD_SIMPLE_SCREENSHOT_DIR'] = $shotRoot

    $allegro = New-Object System.Diagnostics.Process
    $allegro.StartInfo = $startInfo
    if (-not $allegro.Start()) { throw 'Allegro did not start.' }
    $null = $allegro.Handle
    Write-Host "Watching isolated Allegro PID $($allegro.Id) for up to $WaitMinutes minutes..."
    try {
        $windowDeadline = (Get-Date).AddMinutes(3)
        while ((Get-Date) -lt $windowDeadline) {
            if ($allegro.HasExited) { throw "Allegro exited during startup (exit $($allegro.ExitCode))." }
            $allegro.Refresh()
            if ($allegro.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($allegro.MainWindowHandle -eq 0) { throw 'Allegro showed no window within three minutes.' }
        $openDeadline = (Get-Date).AddMinutes($WaitMinutes)
        $boardOpen = $false
        $pdPid = 0
        while ((Get-Date) -lt $openDeadline) {
            if ($allegro.HasExited) { throw "Allegro exited while opening (exit $($allegro.ExitCode))." }
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $allegro.MainWindowHandle)
            $yesCondition = New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button)),
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, 'Yes')))
            $yes = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $yesCondition)
            if (($null -ne $yes) -and $yes.Current.IsEnabled) {
                $yes.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Write-Host 'Accepted a prompt with Yes.'
            }
            if ($root.Current.Name -like '*.brd*') {
                if (-not $boardOpen) {
                    $boardOpen = $true
                    Write-Host ('BOARD OPEN: ' + $root.Current.Name)
                }
                $candidate = @(Get-Process -Name 'PD.Simple' -ErrorAction SilentlyContinue | Where-Object {
                    -not $_.HasExited -and $_.StartTime -ge $startedAt -and $_.Path -eq $pdExe } |
                    Sort-Object StartTime | Select-Object -First 1)
                if ($candidate.Count -ge 1) { $pdPid = $candidate[0].Id }
            }
            if ($boardOpen -and $pdPid -ne 0) {
                Write-Host "PD auto-opened by the resident bridge: PID $pdPid."
                @($allegro.Id, $runRoot, $scratchBoard, $pdPid) |
                    Set-Content -LiteralPath (Join-Path $runRoot 'allegro-ready.txt') -Encoding utf8
                Write-Host "READY. Leaving Allegro PID $($allegro.Id) + PD PID $pdPid running."
                return
            }
            Start-Sleep -Seconds 5
        }
        throw 'Board open + resident bridge + PD auto-launch did not complete within the watch window.'
    }
    catch {
        Write-Host ('WATCH FAILED: ' + $_.Exception.Message)
        if (-not $allegro.HasExited) {
            $allegro.CloseMainWindow() | Out-Null
            if (-not $allegro.WaitForExit(20000)) { $allegro.Kill(); $allegro.WaitForExit() }
        }
        exit 1
    }
    finally {
        $allegro.Dispose()
    }
}
finally {
    Stop-Transcript | Out-Null
}
