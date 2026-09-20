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
if ($BoardPath -eq '') { $BoardPath = 'C:\Users\EMILEKOWALSKI\Desktop\boards\blyth1z_unrouted.brd' }
$runRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-licensewatch-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

foreach ($file in @($allegroExe, $resident, $BoardPath)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
$stale = @(Get-Process -Name 'allegro' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stale.Count -gt 0) { throw "An Allegro is already running (PID $($stale[0].Id)). Nothing was touched." }

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
    if ($skillResident.Contains('"')) { throw 'Resident path has an unsupported quote.' }
    $replayPath = Join-Path $runRoot 'load-resident.scr'
    @('skill (load "' + $skillResident + '")', 'skill (pdab_start)', '') -join "`n" |
        Set-Content -LiteralPath $replayPath -Encoding ascii -NoNewline
    $bridgePointerPath = Join-Path $runRoot 'bridge-pointer.txt'

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
    $startInfo.EnvironmentVariables['ALLEGRO_BRIDGE_ACCEPTANCE_BRIDGE_FILE'] = $bridgePointerPath

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
        $bridgeDirectory = ''
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
            if (Test-Path -LiteralPath $bridgePointerPath -PathType Leaf) {
                $candidate = ([System.IO.File]::ReadAllText($bridgePointerPath)).Trim()
                if (-not [string]::IsNullOrWhiteSpace($candidate) -and
                    (Test-Path -LiteralPath $candidate -PathType Container)) {
                    $bridgeDirectory = $candidate
                }
            }
            if (($root.Current.Name -like '*.brd*') -and ($bridgeDirectory -ne '')) {
                Write-Host ('BOARD OPEN: ' + $root.Current.Name)
                Write-Host ('BRIDGE: ' + $bridgeDirectory)
                @($allegro.Id, $runRoot, $scratchBoard, $bridgeDirectory) |
                    Set-Content -LiteralPath (Join-Path $runRoot 'allegro-ready.txt') -Encoding utf8
                Write-Host "READY. Leaving Allegro PID $($allegro.Id) running."
                return
            }
            Start-Sleep -Seconds 5
        }
        throw 'Board + resident bridge did not become ready within the watch window.'
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
