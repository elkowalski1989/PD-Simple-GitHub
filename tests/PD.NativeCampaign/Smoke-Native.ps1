[CmdletBinding()]
param([string] $BoardPath = '', [switch] $LeaveRunning, [switch] $NoReplay)

# Smoke 0: launch owned Allegro on a disposable board copy with the resident
# bridge loaded, then prove window-only capture works (PrintWindow and
# window-DC BitBlt; never a desktop screenshot). Pure ASCII for PS 5.1.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pdRoot = 'C:\e2studio\worktrees\pd-coordinator'
$allegroExe = 'C:\Cadence\SPB_25.1\tools\bin\allegro.exe'
$resident = Join-Path $pdRoot 'src\PD.Simple\bin\Release\net10.0-windows\AllegroBridge\Resident\pd_allegro_bridge.il'
$captureExe = Join-Path $pdRoot 'tests\PD.NativeCampaign\WindowCapture\bin\Release\net10.0-windows\PD.WindowCapture.exe'
$defaultBoard = 'C:\Users\EMILEKOWALSKI\Desktop\boards\ingram9z_040324.brd'
$runRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

function Click-YesIfPresent {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement] $Root)
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Yes')))
    $button = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (($null -ne $button) -and $button.Current.IsEnabled) {
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    }
    return $false
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if ($BoardPath -eq '') { $BoardPath = $defaultBoard }
foreach ($file in @($allegroExe, $resident, $captureExe, $BoardPath)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
$stale = @(Get-Process -Name 'allegro' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stale.Count -gt 0) { throw "Close Allegro first; found PID $($stale[0].Id). Nothing was touched." }

[void](New-Item -ItemType Directory -Path $runRoot -Force)
$logPath = Join-Path $runRoot 'smoke.log'
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    $inputRoot = Join-Path $runRoot 'input'
    [void](New-Item -ItemType Directory -Path $inputRoot -Force)
    $scratchBoard = Join-Path $inputRoot 'campaign.brd'
    Copy-Item -LiteralPath $BoardPath -Destination $scratchBoard
    Write-Host "Disposable board: $scratchBoard"

    $skillResident = $resident.Replace('\', '/')
    if ($skillResident.Contains('"')) { throw 'Resident path has an unsupported quote.' }
    $replayPath = Join-Path $runRoot 'load-resident.scr'
    @('skill (load "' + $skillResident + '")', 'skill (pdab_start)', '') -join "`n" |
        Set-Content -LiteralPath $replayPath -Encoding ascii -NoNewline

    $journalPath = Join-Path $runRoot 'allegro.jrl'
    $launchArgs = @('-product', 'Allegro_performance', '-p', $inputRoot, '-j', $journalPath)
    if (-not $NoReplay) { $launchArgs += @('-s', $replayPath) }
    $launchArgs += @($scratchBoard)
    $allegro = Start-Process -FilePath $allegroExe -ArgumentList $launchArgs -PassThru
    $null = $allegro.Handle
    Write-Host "Allegro PID $($allegro.Id) starting..."
    try {
        $deadline = (Get-Date).AddMinutes(6)
        while ((Get-Date) -lt $deadline) {
            if ($allegro.HasExited) { throw "Allegro exited during startup (exit $($allegro.ExitCode)). See $journalPath" }
            $allegro.Refresh()
            if ($allegro.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($allegro.MainWindowHandle -eq 0) { throw 'Allegro showed no window within six minutes.' }
        Write-Host "Allegro window up (PID $($allegro.Id))."
        $openDeadline = (Get-Date).AddMinutes(6)
        $boardTitle = $false
        while ((Get-Date) -lt $openDeadline) {
            if ($allegro.HasExited) { throw "Allegro exited while opening the board (exit $($allegro.ExitCode))." }
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $allegro.MainWindowHandle)
            if (Click-YesIfPresent $root) { Write-Host 'Accepted a board-compatibility prompt with Yes.' }
            if ($root.Current.Name -like '*.brd*') { $boardTitle = $true; break }
            Start-Sleep -Milliseconds 1000
        }
        if (-not $boardTitle) { throw 'The board did not finish opening within six minutes (no .brd window title).' }
        Write-Host "Board open: $($root.Current.Name)"
        Start-Sleep -Seconds 3

        foreach ($method in @('printwindow', 'windowdc')) {
            $png = Join-Path $runRoot "allegro-$method.png"
            $out = & $captureExe --pid $allegro.Id --out $png --method $method --place '0,0,1456,813' --require-unoccluded 2>&1
            Write-Host $out
            if ($LASTEXITCODE -ne 0) { throw "Capture ($method) failed." }
        }
        $pngAuto = Join-Path $runRoot 'allegro-auto.png'
        $out = & $captureExe --pid $allegro.Id --out $pngAuto --method auto --require-unoccluded 2>&1
        Write-Host $out
        if ($LASTEXITCODE -ne 0) { throw 'Capture (auto) failed.' }
        Write-Host "SMOKE GREEN. Evidence: $runRoot"
    }
    finally {
        if ($LeaveRunning) {
            Write-Host "Leaving Allegro PID $($allegro.Id) running for follow-up steps."
        }
        elseif (-not $allegro.HasExited) {
            $allegro.CloseMainWindow() | Out-Null
            if (-not $allegro.WaitForExit(20000)) { $allegro.Kill(); $allegro.WaitForExit() }
            Write-Host 'Owned Allegro closed.'
        }
        $allegro.Dispose()
    }
}
finally {
    Stop-Transcript | Out-Null
}
