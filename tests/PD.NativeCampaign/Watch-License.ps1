[CmdletBinding()]
param([string] $BoardPath = '', [int] $WaitMinutes = 8)

# License-watch: launch one owned Allegro on a disposable board copy and wait
# for the board to open (window title gains .brd). No captures, no focus
# stealing: safe to run alongside the PD sweep. On success the board stays
# open and the PID is written to allegro-ready.txt; on failure Allegro is
# closed and the exit code is 1.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$allegroExe = 'C:\Cadence\SPB_25.1\tools\bin\allegro.exe'
if ($BoardPath -eq '') { $BoardPath = 'C:\Users\EMILEKOWALSKI\Desktop\boards\blyth1z_unrouted.brd' }
$runRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-licensewatch-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

foreach ($file in @($allegroExe, $BoardPath)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
$stale = @(Get-Process -Name 'allegro' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stale.Count -gt 0) { throw "An Allegro is already running (PID $($stale[0].Id)). Nothing was touched." }

[void](New-Item -ItemType Directory -Path $runRoot -Force)
$logPath = Join-Path $runRoot 'watch.log'
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    $inputRoot = Join-Path $runRoot 'input'
    [void](New-Item -ItemType Directory -Path $inputRoot -Force)
    $scratchBoard = Join-Path $inputRoot 'campaign.brd'
    Copy-Item -LiteralPath $BoardPath -Destination $scratchBoard
    $allegro = Start-Process -FilePath $allegroExe -ArgumentList @(
        '-product', 'Allegro_performance',
        '-p', $inputRoot,
        '-j', (Join-Path $runRoot 'allegro.jrl'),
        $scratchBoard) -PassThru
    $null = $allegro.Handle
    Write-Host "Watching Allegro PID $($allegro.Id) for up to $WaitMinutes minutes..."
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
                Write-Host ('BOARD OPEN: ' + $root.Current.Name)
                @($allegro.Id, $runRoot, $scratchBoard) | Set-Content -LiteralPath (Join-Path $runRoot 'allegro-ready.txt') -Encoding utf8
                Write-Host "READY. Leaving Allegro PID $($allegro.Id) running."
                return
            }
            Start-Sleep -Seconds 5
        }
        throw 'Board did not open within the watch window (license seats still exhausted?).'
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
