[CmdletBinding()]
param([int] $ProcessId = 0, [string] $RunRoot = '', [switch] $LaunchPd, [switch] $LeaveRunning)

# Offline sweep: with PD already running (disconnected), click each of the 12
# tool entries plus home/corridor/explorer, and save per-tool automation-tree,
# text, and in-app screenshot evidence. Records PASS/FAIL/SKIPPED per tool.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$dumpScript = Join-Path $PSScriptRoot 'Dump-Tree.ps1'
$screenshotButton = 'Capture PD Simple window screenshot and copy path'
$tools = @(
    @('CrossingMenuButton', '01-crossing'),
    @('InspectorMenuButton', '02-inspector'),
    @('MeasureMenuButton', '03-measure'),
    @('PlacementMenuButton', '04-placement'),
    @('ViaRouteMenuButton', '05-viaroute'),
    @('OverlayMenuButton', '06-overlay'),
    @('ReviewMenuButton', '07-review'),
    @('ScenesMenuButton', '08-scenes'),
    @('ConstraintsDrcMenuButton', '09-constraintsdrc'),
    @('PhysicalSymbolsMenuButton', '10-physicalsymbols'),
    @('PadstacksMenuButton', '11-padstacks'),
    @('ManufacturingMenuButton', '12-manufacturing'),
    @('CorridorMenuButton', '13-corridor'),
    @('ExplorerMenuButton', '14-explorer'),
    @('HomeMenuButton', '15-home')
)

if ($RunRoot -eq '') {
    $RunRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
[void](New-Item -ItemType Directory -Path $RunRoot -Force)
$ownPd = $false
if ($LaunchPd) {
    $stalePd = @(Get-Process -Name 'PD.Simple' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
    if ($stalePd.Count -gt 0) { throw "Close PD Simple first; found PID $($stalePd[0].Id). Nothing was touched." }
    $env:PD_SIMPLE_SCREENSHOT_DIR = Join-Path $RunRoot 'pd-shots'
    $process = Start-Process -FilePath 'C:\e2studio\worktrees\pd-coordinator\src\PD.Simple\bin\Release\net10.0-windows\PD.Simple.exe' -PassThru
    $null = $process.Handle
    $ownPd = $true
    $launchDeadline = (Get-Date).AddMinutes(2)
    while ((Get-Date) -lt $launchDeadline) {
        if ($process.HasExited) { throw 'PD exited before creating its main window.' }
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($process.MainWindowHandle -eq 0) { throw 'PD showed no window within two minutes.' }
    $ProcessId = $process.Id
}
else {
    $process = Get-Process -Id $ProcessId
}
$logPath = Join-Path $RunRoot 'sweep.log'
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $process.MainWindowHandle)
    $results = New-Object System.Collections.Generic.List[string]
    $results.Add('tool,nav_id,outcome,detail')
    foreach ($tool in $tools) {
        $navId = $tool[0]
        $slug = $tool[1]
        $process.Refresh()
        if ($process.HasExited) {
            $results.Add("$slug,$navId,SKIPPED,PD exited during the sweep")
            continue
        }
        $toolDir = Join-Path $RunRoot $slug
        [void](New-Item -ItemType Directory -Path $toolDir -Force)
        try {
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $navId)
            $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -eq $button) { throw "Nav button $navId not found." }
            if (-not $button.Current.IsEnabled) { throw "Nav button $navId is disabled." }
            $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Seconds 3
            $process.Refresh()
            if ($process.HasExited) { throw 'PD exited after navigating here.' }
            $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $process.MainWindowHandle)

            & $dumpScript -ProcessId $process.Id -OutFile (Join-Path $toolDir 'tree.txt') -TimeoutSeconds 30 | Out-Null

            $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $screenshotButton)
            $shotButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
            if ($null -eq $shotButton) { throw 'Screenshot button not found on this page.' }
            $before = Get-Date
            $shotButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $shotDeadline = (Get-Date).AddSeconds(20)
            $capture = $null
            while ((Get-Date) -lt $shotDeadline) {
                Start-Sleep -Milliseconds 300
                $roots = @(
                    (Join-Path $RunRoot 'pd-shots'),
                    (Join-Path ([Environment]::GetFolderPath('MyPictures')) 'PD Simple\Screenshots'))
                foreach ($root in $roots) {
                    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
                    $found = @(Get-ChildItem -LiteralPath $root -Filter '*.png' -ErrorAction SilentlyContinue |
                        Where-Object { $_.LastWriteTimeUtc -ge $before.ToUniversalTime() } |
                        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
                    if ($found.Count -ge 1) { $capture = $found[0]; break }
                }
                if ($null -ne $capture) { break }
            }
            if ($null -eq $capture) { throw 'Screenshot button produced no PNG.' }
            Copy-Item -LiteralPath $capture.FullName -Destination (Join-Path $toolDir 'shot.png')
            $results.Add("$slug,$navId,PASS,")
            Write-Host "PASS $slug"
        }
        catch {
            $detail = ($_.Exception.Message -replace '\s+', ' ').Trim()
            $results.Add("$slug,$navId,FAIL,$detail")
            Write-Host "FAIL $slug :: $detail"
        }
    }
    $results | Set-Content -LiteralPath (Join-Path $RunRoot 'summary.csv') -Encoding utf8
    $pass = @($results | Where-Object { $_ -like '*,PASS,*' }).Count
    Write-Host "SWEEP DONE: $pass/$($tools.Count) PASS. Evidence: $RunRoot"
}
finally {
    if ($ownPd) {
        try {
            $process.Refresh()
            if (-not $process.HasExited -and -not $LeaveRunning) {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(25000)) { $process.Kill(); $process.WaitForExit() }
                Write-Host 'Owned PD closed.'
            }
        }
        finally {
            $process.Dispose()
        }
        Remove-Item Env:PD_SIMPLE_SCREENSHOT_DIR -ErrorAction SilentlyContinue
    }
    Stop-Transcript | Out-Null
}
