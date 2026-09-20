[CmdletBinding()]
param([string] $RunRoot = '', [switch] $LeaveRunning)

# Smoke 1: launch PD against the already-running owned Allegro, wait for the
# Engine connection, dump the automation tree for campaign authoring, and
# capture both windows (PD via its in-app renderer, Allegro window-only).
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$pdRoot = 'C:\e2studio\worktrees\pd-coordinator'
$pdExe = Join-Path $pdRoot 'src\PD.Simple\bin\Release\net10.0-windows\PD.Simple.exe'
$captureExe = Join-Path $pdRoot 'tests\PD.NativeCampaign\WindowCapture\bin\Release\net10.0-windows\PD.WindowCapture.exe'
$screenshotButton = 'Capture PD Simple window screenshot and copy path'

function Get-AllText {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement] $Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $attempt = 0
    while ($true) {
        try {
            return @($Root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition) | ForEach-Object { $_.Current.Name })
        }
        catch {
            $attempt++
            if ($attempt -ge 4) { throw }
            Start-Sleep -Milliseconds 750
        }
    }
}

function Find-ButtonByName {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement] $Root,
        [Parameter(Mandatory = $true)][string] $Name)
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)))
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-TreeDump {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement] $Root)
    $lines = New-Object System.Collections.Generic.List[string]
    $all = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        try {
            $current = $element.Current
            if ($current.ControlType -eq [System.Windows.Automation.ControlType]::Button -or
                $current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -or
                $current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem -or
                $current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -or
                -not [string]::IsNullOrWhiteSpace($current.AutomationId)) {
                $lines.Add($current.ControlType.ProgrammaticName + ' | id=' + $current.AutomationId +
                    ' | name=' + ($current.Name -replace '\s+', ' ') +
                    ' | enabled=' + $current.IsEnabled)
            }
        }
        catch { }
    }
    return $lines
}

foreach ($file in @($pdExe, $captureExe)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
$allegros = @(Get-Process -Name 'allegro' -ErrorAction SilentlyContinue | Where-Object {
    -not $_.HasExited -and $_.MainWindowHandle -ne 0 })
if ($allegros.Count -ne 1) { throw "Need exactly one running Allegro; found $($allegros.Count)." }
$allegro = $allegros[0]
$stalePd = @(Get-Process -Name 'PD.Simple' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stalePd.Count -gt 0) { throw "Close PD Simple first; found PID $($stalePd[0].Id). Nothing was touched." }

if ($RunRoot -eq '') {
    $RunRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-connect-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
[void](New-Item -ItemType Directory -Path $RunRoot -Force)
$logPath = Join-Path $RunRoot 'connect.log'
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    $env:PD_SIMPLE_SCREENSHOT_DIR = Join-Path $RunRoot 'pd-shots'
    $pd = Start-Process -FilePath $pdExe -PassThru
    $null = $pd.Handle
    Write-Host "PD PID $($pd.Id) starting..."
    try {
        $deadline = (Get-Date).AddMinutes(2)
        $window = $null
        while ((Get-Date) -lt $deadline) {
            if ($pd.HasExited) { throw 'PD exited before creating its main window.' }
            $pd.Refresh()
            if ($pd.MainWindowHandle -ne 0) {
                $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $pd.MainWindowHandle)
                break
            }
            Start-Sleep -Milliseconds 200
        }
        if ($null -eq $window) { throw 'PD showed no window within two minutes.' }
        Write-Host 'PD window up. Waiting for the Engine connection...'

        $connected = $false
        $connectDeadline = (Get-Date).AddMinutes(3)
        while ((Get-Date) -lt $connectDeadline) {
            if ($pd.HasExited) { throw 'PD exited while connecting.' }
            $text = @(Get-AllText $window)
            if ($text -contains 'Connected to Allegro.') { $connected = $true; break }
            $bad = @($text | Where-Object {
                $_.StartsWith('The selected Engine target could not be connected') -or
                $_.StartsWith('Connection unavailable:') })
            if ($bad.Count -gt 0) { break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $connected) {
            Write-Host 'No automatic connection; invoking Reconnect / Attach...'
            $reconnect = Find-ButtonByName $window 'Reconnect / Attach'
            if ($null -eq $reconnect) { throw 'Reconnect / Attach was not found and no connection appeared.' }
            if (-not $reconnect.Current.IsEnabled) { throw 'Reconnect / Attach is disabled and no connection appeared.' }
            $reconnect.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $attachDeadline = (Get-Date).AddMinutes(3)
            while ((Get-Date) -lt $attachDeadline) {
                if ($pd.HasExited) { throw 'PD exited while attaching.' }
                if ((@(Get-AllText $window)) -contains 'Connected to Allegro.') { $connected = $true; break }
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $connected) { throw 'PD did not connect within the attach window.' }
        Write-Host 'Connected to Allegro.'

        @(Get-AllText $window) | Set-Content -LiteralPath (Join-Path $RunRoot 'pd-text-connected.txt') -Encoding utf8
        @(Get-TreeDump $window) | Set-Content -LiteralPath (Join-Path $RunRoot 'pd-tree-connected.txt') -Encoding utf8
        Write-Host 'Saved PD text + automation-tree snapshots.'

        $shotButton = Find-ButtonByName $window $screenshotButton
        if ($null -eq $shotButton) { throw 'The PD screenshot button was not found.' }
        $shotButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $shotDeadline = (Get-Date).AddSeconds(15)
        $capture = $null
        while ((Get-Date) -lt $shotDeadline) {
            Start-Sleep -Milliseconds 200
            $found = @(Get-ChildItem -LiteralPath $env:PD_SIMPLE_SCREENSHOT_DIR -Filter '*.png' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
            if ($found.Count -ge 1) { $capture = $found[0]; break }
        }
        if ($null -eq $capture) { throw 'The PD screenshot button did not create a PNG.' }
        Write-Host ('PD in-app capture: ' + $capture.FullName)

        $pngAllegro = Join-Path $RunRoot 'allegro-connected.png'
        $out = & $captureExe --pid $allegro.Id --out $pngAllegro --method auto --require-unoccluded 2>&1
        Write-Host $out
        if ($LASTEXITCODE -ne 0) { throw 'Allegro capture failed.' }
        Write-Host "CONNECT GREEN. Evidence: $RunRoot"
    }
    finally {
        if ($LeaveRunning) {
            Write-Host "Leaving PD PID $($pd.Id) running for follow-up steps."
        }
        elseif (-not $pd.HasExited) {
            $pd.CloseMainWindow() | Out-Null
            if (-not $pd.WaitForExit(20000)) { $pd.Kill(); $pd.WaitForExit() }
            Write-Host 'PD closed.'
        }
        $pd.Dispose()
    }
}
finally {
    Remove-Item Env:PD_SIMPLE_SCREENSHOT_DIR -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
}
