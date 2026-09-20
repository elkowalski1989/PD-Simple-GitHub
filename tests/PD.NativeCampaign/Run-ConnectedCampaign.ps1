[CmdletBinding()]
param([string] $AllegroReadyFile = '', [string] $RunRoot = '', [switch] $LeaveRunning)

# Connected campaign: drives all 12 PD tools against the live Allegro board
# opened by Watch-License, running each tool's representative safe action,
# capturing PD (in-app) + Allegro (window-only) evidence per tool.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$pdRoot = 'C:\e2studio\worktrees\pd-coordinator'
$pdExe = Join-Path $pdRoot 'src\PD.Simple\bin\Release\net10.0-windows\PD.Simple.exe'
$captureExe = Join-Path $pdRoot 'tests\PD.NativeCampaign\WindowCapture\bin\Release\net10.0-windows\PD.WindowCapture.exe'
$dumpScript = Join-Path $PSScriptRoot 'Dump-Tree.ps1'
$screenshotButton = 'Capture PD Simple window screenshot and copy path'

# Per-tool plan: nav button, ordered action buttons to invoke (each waited on
# via marker text below), and marker fragments proving live behavior.
# Markers use -like wildcards and must appear in the page text after the step.
$plan = @(
    @{ Nav = 'CrossingMenuButton'; Slug = '01-crossing'; Steps = @(
        @{ Button = 'CaptureBoardButton'; Markers = @('*capture*') },
        @{ Button = 'ReadMetadataButton'; Markers = @('*metadata*', '*board*') },
        @{ Button = 'AnalyzeButton'; Markers = @('*crossing*') }) },
    @{ Nav = 'InspectorMenuButton'; Slug = '02-inspector'; Steps = @(
        @{ Button = 'CaptureBoardButton'; Markers = @('*capture*') },
        @{ Button = 'ReadMetadataButton'; Markers = @('*metadata*', '*board*') }) },
    @{ Nav = 'MeasureMenuButton'; Slug = '03-measure'; Steps = @(
        @{ Button = 'CaptureBoardButton'; Markers = @('*capture*') }) },
    @{ Nav = 'PlacementMenuButton'; Slug = '04-placement'; Steps = @(
        @{ Button = 'CaptureBoardButton'; Markers = @('*capture*') }) },
    @{ Nav = 'ViaRouteMenuButton'; Slug = '05-viaroute'; Steps = @(
        @{ Button = 'CaptureBoardButton'; Markers = @('*capture*') },
        @{ Button = 'ReadTargetsButton'; Markers = @('*target*') },
        @{ Button = 'PreviewEditButton'; Markers = @('*preview*') }) },
    @{ Nav = 'OverlayMenuButton'; Slug = '06-overlay'; Steps = @(
        @{ Button = 'AcquireButton'; Markers = @('*acquir*', '*scene*') },
        @{ Button = 'BuildButton'; Markers = @('*preview*', '*overlay*') }) },
    @{ Nav = 'ReviewMenuButton'; Slug = '07-review'; Steps = @() },
    @{ Nav = 'ScenesMenuButton'; Slug = '08-scenes'; Steps = @(
        @{ Button = 'CaptureBoardButton'; Markers = @('*capture*') },
        @{ Button = 'SaveSceneButton'; Markers = @('*scene*', '*saved*') }) },
    @{ Nav = 'ConstraintsDrcMenuButton'; Slug = '09-constraintsdrc'; Steps = @(
        @{ Button = 'RefreshConstraintsButton'; Markers = @('*snapshot*') },
        @{ Button = 'ReadMarkersButton'; Markers = @('*marker*') },
        @{ Button = 'RunDrcButton'; Markers = @('*DRC*', '*complet*', '*rule*') }) },
    @{ Nav = 'PhysicalSymbolsMenuButton'; Slug = '10-physicalsymbols'; Steps = @(
        @{ Button = 'PhysicalSymbols.StageButton'; Markers = @('*stag*', '*work area*') },
        @{ Button = 'PhysicalSymbols.PrepareButton'; Markers = @('*preview*', '*request*') }) },
    @{ Nav = 'PadstacksMenuButton'; Slug = '11-padstacks'; Steps = @() },
    @{ Nav = 'ManufacturingMenuButton'; Slug = '12-manufacturing'; Steps = @(
        @{ Button = 'MfgRefreshSourceButton'; Markers = @('*source*', '*fence*') },
        @{ Button = 'MfgBuildPlanButton'; Markers = @('*plan*', '*manifest*', '*stag*') }) }
)

function Get-Window { param($Process)
    return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $Process.MainWindowHandle)
}

function Get-AllText { param($Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $attempt = 0
    while ($true) {
        try {
            return @($Root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, $condition) |
                ForEach-Object { try { $_.Current.Name } catch { $null } })
        }
        catch {
            $attempt++
            if ($attempt -ge 4) { throw }
            Start-Sleep -Milliseconds 750
        }
    }
}

function Find-ByAutomationId { param($Root, $Id)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ButtonByName { param($Root, $Name)
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Save-PdShot { param($Root, $ToolDir, $ShotRoot)
    $shotButton = Find-ButtonByName $Root $screenshotButton
    if ($null -eq $shotButton) { throw 'Screenshot button not found.' }
    $before = (Get-Date).ToUniversalTime()
    $shotButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        if (Test-Path -LiteralPath $ShotRoot -PathType Container) {
            $found = @(Get-ChildItem -LiteralPath $ShotRoot -Filter '*.png' -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTimeUtc -ge $before } |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
            if ($found.Count -ge 1) {
                Copy-Item -LiteralPath $found[0].FullName -Destination (Join-Path $ToolDir 'pd.png')
                return
            }
        }
    }
    throw 'Screenshot button produced no PNG.'
}

function Set-PdVisualState { param($State)
    $element = Get-Window $script:pdProcess
    $pattern = [System.Windows.Automation.WindowPattern] $element.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern)
    $pattern.SetWindowVisualState($State)
    Start-Sleep -Milliseconds 400
}

function Save-AllegroShot { param($AllegroId, $ToolDir, $Stem)
    # PD is campaign-owned: minimize it around Allegro captures so no PD pixel
    # can overlap the Allegro rect. (PD's own shots use in-app rendering and
    # never need foreground.) Foreign occluders still fail honestly via
    # --require-unoccluded.
    Set-PdVisualState ([System.Windows.Automation.WindowVisualState]::Minimized)
    try {
        $png = Join-Path $ToolDir ("$Stem.png")
        $out = & $captureExe --pid $AllegroId --out $png --method auto --require-unoccluded 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Allegro capture failed: $out" }
        Write-Host ("  " + ($out -join ' '))
    }
    finally {
        Set-PdVisualState ([System.Windows.Automation.WindowVisualState]::Normal)
    }
}

foreach ($file in @($pdExe, $captureExe, $dumpScript)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
if ($AllegroReadyFile -eq '') {
    $found = @(Get-ChildItem 'C:\e2studio\pd-simple-local-runs' -Directory -Filter 'native-licensewatch-*' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    if ($found.Count -ge 1) {
        $candidate = Join-Path $found[0].FullName 'allegro-ready.txt'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $AllegroReadyFile = $candidate }
    }
}
if ($AllegroReadyFile -eq '' -or -not (Test-Path -LiteralPath $AllegroReadyFile -PathType Leaf)) {
    throw 'No allegro-ready.txt. Run Watch-License.ps1 until the board opens first.'
}
$ready = @(Get-Content -LiteralPath $AllegroReadyFile)
$allegroId = [int] $ready[0]
$allegro = Get-Process -Id $allegroId -ErrorAction SilentlyContinue
if ($null -eq $allegro -or $allegro.HasExited) { throw "Ready Allegro PID $allegroId is gone." }
$stalePd = @(Get-Process -Name 'PD.Simple' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
if ($stalePd.Count -gt 0) { throw "Close PD Simple first; found PID $($stalePd[0].Id). Nothing was touched." }

if ($RunRoot -eq '') {
    $RunRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-connected-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
[void](New-Item -ItemType Directory -Path $RunRoot -Force)
$shotRoot = Join-Path $RunRoot 'pd-shots'
$logPath = Join-Path $RunRoot 'campaign.log'
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    $env:PD_SIMPLE_SCREENSHOT_DIR = $shotRoot
    $pd = Start-Process -FilePath $pdExe -PassThru
    $null = $pd.Handle
    Write-Host "PD PID $($pd.Id) starting against Allegro PID $allegroId..."
    try {
        $deadline = (Get-Date).AddMinutes(2)
        $window = $null
        while ((Get-Date) -lt $deadline) {
            if ($pd.HasExited) { throw 'PD exited before creating its main window.' }
            $pd.Refresh()
            if ($pd.MainWindowHandle -ne 0) {
                $window = Get-Window $pd
                break
            }
            Start-Sleep -Milliseconds 200
        }
        if ($null -eq $window) { throw 'PD showed no window within two minutes.' }

        $connected = $false
        $connectDeadline = (Get-Date).AddMinutes(4)
        while ((Get-Date) -lt $connectDeadline) {
            if ($pd.HasExited) { throw 'PD exited while connecting.' }
            if ((@(Get-AllText $window)) -contains 'Connected to Allegro.') { $connected = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $connected) {
            $reconnect = Find-ButtonByName $window 'Reconnect / Attach'
            if ($null -eq $reconnect -or -not $reconnect.Current.IsEnabled) {
                throw 'No automatic connection and Reconnect / Attach is unavailable.'
            }
            $reconnect.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $attachDeadline = (Get-Date).AddMinutes(4)
            while ((Get-Date) -lt $attachDeadline) {
                if ($pd.HasExited) { throw 'PD exited while attaching.' }
                if ((@(Get-AllText $window)) -contains 'Connected to Allegro.') { $connected = $true; break }
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $connected) { throw 'PD did not connect to the live board.' }
        Write-Host 'Connected to Allegro.'
        $script:pdProcess = $pd

        $results = New-Object System.Collections.Generic.List[string]
        $results.Add('tool,step,outcome,detail')
        foreach ($tool in $plan) {
            $slug = $tool.Slug
            $pd.Refresh()
            if ($pd.HasExited) {
                $results.Add("$slug,*,SKIPPED,PD exited during the campaign")
                continue
            }
            $toolDir = Join-Path $RunRoot $slug
            [void](New-Item -ItemType Directory -Path $toolDir -Force)
            try {
                $nav = Find-ByAutomationId $window $tool.Nav
                if ($null -eq $nav) { throw ("Nav " + $tool.Nav + " not found.") }
                $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Seconds 2
                $window = Get-Window $pd
                Save-PdShot $window $toolDir $shotRoot
                Save-AllegroShot $allegroId $toolDir 'allegro-arrive'
                $results.Add("$slug,arrive,PASS,")
                Write-Host "PASS $slug/arrive"
            }
            catch {
                $results.Add("$slug,arrive,FAIL," + (($_.Exception.Message -replace '\s+', ' ').Trim()))
                Write-Host ("FAIL $slug/arrive :: " + $_.Exception.Message)
                continue
            }
            foreach ($step in $tool.Steps) {
                $label = $step.Button
                try {
                    $control = Find-ByAutomationId $window $label
                    if ($null -eq $control) { throw "Button $label not found." }
                    if (-not $control.Current.IsEnabled) { throw "Button $label is disabled while connected." }
                    $beforeText = @(Get-AllText $window) -join "`n"
                    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    $stepDeadline = (Get-Date).AddMinutes(5)
                    $matched = $null
                    while ((Get-Date) -lt $stepDeadline) {
                        if ($pd.HasExited) { throw 'PD exited during the step.' }
                        $texts = @(Get-AllText $window)
                        $bad = @($texts | Where-Object {
                            $_.StartsWith('Check did not complete') -or
                            $_.StartsWith('Could not start check') -or
                            $_.StartsWith('Result could not be verified') })
                        if ($bad.Count -gt 0) { throw ("Action failed: " + $bad[0]) }
                        foreach ($text in $texts) {
                            foreach ($marker in $step.Markers) {
                                if ($text -like $marker -and -not $beforeText.Contains($text)) {
                                    $matched = $marker + ' :: ' + (($text -replace '\s+', ' ').Trim())
                                    break
                                }
                            }
                            if ($null -ne $matched) { break }
                        }
                        if ($null -ne $matched) { break }
                        Start-Sleep -Milliseconds 500
                    }
                    if ($null -eq $matched) {
                        throw ('No fresh marker text (' + ($step.Markers -join ' | ') + ') within five minutes.')
                    }
                    Save-PdShot $window $toolDir $shotRoot
                    $shot = @(Get-ChildItem -LiteralPath $toolDir -Filter 'pd.png')[0]
                    Move-Item -LiteralPath $shot.FullName -Destination (Join-Path $toolDir ("pd-" + $label + ".png")) -Force
                    Save-AllegroShot $allegroId $toolDir ("allegro-" + $label)
                    @(Get-AllText $window) | Set-Content -LiteralPath (Join-Path $toolDir ("text-" + $label + ".txt")) -Encoding utf8
                    $results.Add("$slug,$label,PASS,$matched")
                    Write-Host "PASS $slug/$label"
                }
                catch {
                    $detail = ($_.Exception.Message -replace '\s+', ' ').Trim()
                    $results.Add("$slug,$label,FAIL,$detail")
                    Write-Host "FAIL $slug/$label :: $detail"
                    try {
                        Save-PdShot $window $toolDir $shotRoot
                        $shot = @(Get-ChildItem -LiteralPath $toolDir -Filter 'pd.png')[0]
                        Move-Item -LiteralPath $shot.FullName -Destination (Join-Path $toolDir ("pd-" + $label + "-fail.png")) -Force
                    }
                    catch { }
                }
            }
            & $dumpScript -ProcessId $pd.Id -OutFile (Join-Path $toolDir 'tree.txt') -TimeoutSeconds 30 | Out-Null
        }
        $results | Set-Content -LiteralPath (Join-Path $RunRoot 'summary.csv') -Encoding utf8
        $pass = @($results | Where-Object { $_ -like '*,PASS,*' }).Count
        $total = $results.Count - 1
        Write-Host "CAMPAIGN DONE: $pass/$total PASS. Evidence: $RunRoot"
    }
    finally {
        if ($LeaveRunning) {
            Write-Host "Leaving PD PID $($pd.Id) running."
        }
        elseif (-not $pd.HasExited) {
            $pd.CloseMainWindow() | Out-Null
            if (-not $pd.WaitForExit(25000)) { $pd.Kill(); $pd.WaitForExit() }
            Write-Host 'PD closed.'
        }
        $pd.Dispose()
    }
}
finally {
    Remove-Item Env:PD_SIMPLE_SCREENSHOT_DIR -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
}
