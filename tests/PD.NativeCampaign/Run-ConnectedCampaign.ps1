[CmdletBinding()]
param([string] $AllegroReadyFile = '', [string] $RunRoot = '', [switch] $LeaveRunning)

# Connected campaign: drives all 12 PD tools against the live Allegro board
# opened by Watch-License, running each tool's representative safe action,
# capturing PD (in-app) + Allegro (window-only) evidence per tool.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class CampaignMouse { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern void mouse_event(int f, int dx, int dy, int d, int e); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }'

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
        @{ Button = 'AnalyzeButton'; Gesture = 'AnalysisArea'; Markers = @('*Nets:*') }) },
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
        @{ Button = 'PreviewEditButton'; PickTarget = 'EditTargetPicker';
            ExpectFailure = 'fixed and cannot be'; Markers = @('*preview*') }) },
    @{ Nav = 'OverlayMenuButton'; Slug = '06-overlay'; Steps = @(
        @{ Button = 'AcquireButton'; Markers = @('*acquir*', '*scene*') },
        @{ Button = 'BuildButton'; Markers = @('*preview*', '*overlay*') }) },
    @{ Nav = 'ReviewMenuButton'; Slug = '07-review'; Steps = @() },
    @{ Nav = 'ScenesMenuButton'; Slug = '08-scenes'; Steps = @(
        @{ Button = 'CaptureBoardButton'; Markers = @('*capture*') },
        @{ Button = 'SaveSceneButton'; Markers = @('*saved*') }) },
    @{ Nav = 'ConstraintsDrcMenuButton'; Slug = '09-constraintsdrc'; Steps = @(
        @{ Button = 'RefreshConstraintsButton'; Markers = @('*snapshot*') },
        @{ Button = 'ReadMarkersButton'; Markers = @('*marker*') },
        @{ Button = 'RunDrcButton'; Markers = @('*DRC*', '*complet*', '*rule*') }) },
    @{ Nav = 'PhysicalSymbolsMenuButton'; Slug = '10-physicalsymbols'; Steps = @(
        @{ Button = 'PhysicalSymbols.StageButton';
            SetText = @(
                @{ Id = 'PhysicalSymbols.StagingRoot'; Text = '{RunRoot}\symbol-stage' },
                @{ Id = 'PhysicalSymbols.SymbolName'; Text = 'CASE_SYMBOL' });
            Markers = @('*Stag*', '*identity*') },
        @{ Button = 'PhysicalSymbols.PrepareButton';
            SetText = @(
                @{ Id = 'PhysicalSymbols.Operation'; Text = 'Generate' },
                @{ Id = 'PhysicalSymbols.Target'; Text = 'SymbolDocument:CASE_SYMBOL' },
                @{ Id = 'PhysicalSymbols.IntentJson'; File = 'skill\symbol_prepare_intent.json' },
                @{ Id = 'PhysicalSymbols.ContextFingerprint'; Text = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef' });
            ExpectFailure = 'library_destination_unsupported';
            Markers = @('*binding*', '*reject*') }) },
    @{ Nav = 'PadstacksMenuButton'; Slug = '11-padstacks'; Steps = @() },
    @{ Nav = 'ManufacturingMenuButton'; Slug = '12-manufacturing'; Steps = @(
        @{ Button = 'MfgRefreshSourceButton';
            ExpectFailure = 'manufacturing source document identity is incomplete';
            Markers = @('*source*') },
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

function Set-TextById { param($Root, $Id, $Text)
    $field = Find-ByAutomationId $Root $Id
    if ($null -eq $field) { throw "Text field $Id not found." }
    try {
        ([System.Windows.Automation.ValuePattern] $field.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Text)
    }
    catch {
        throw "Text field $Id rejected ValuePattern.SetValue: $($_.Exception.Message)"
    }
}

function Invoke-MouseClick { param($X, $Y)
    [CampaignMouse]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 300
    [CampaignMouse]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 150
    [CampaignMouse]::mouse_event(0x0004, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 600
}

function Invoke-AnalysisAreaGesture { param($Pd)
    # The Crossings Analyze button requires a drawn analysis rectangle:
    # select the Crossings tab (RectangleSelect mode), drag across the
    # canvas, and verify Analyze enables. Geometry derives from live UIA
    # rects (workbench area + tab strip), never hard-coded pixels.
    $Pd.Refresh()
    [CampaignMouse]::SetForegroundWindow([IntPtr] $Pd.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
    $window = Get-Window $Pd
    $already = Find-ByAutomationId $window 'AnalyzeButton'
    if ($null -ne $already -and $already.Current.IsEnabled) {
        Write-Host '  Analysis area already present; skipping gesture.'
        return $window
    }
    $tab = Find-ByAutomationId $window 'CrossingsTab'
    if ($null -eq $tab) { throw 'CrossingsTab not found for the analysis gesture.' }
    ([System.Windows.Automation.SelectionItemPattern] $tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 600
    $Pd.Refresh()
    $window = Get-Window $Pd
    $classCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'EngineWorkbenchView')
    $bench = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $classCondition)
    if ($null -eq $bench) { throw 'EngineWorkbenchView not found for the analysis gesture.' }
    $workRect = $bench.Current.BoundingRectangle
    $tabNow = Find-ByAutomationId $window 'CrossingsTab'
    $tabRect = $tabNow.Current.BoundingRectangle
    $x0 = [int] ($workRect.X + $workRect.Width * 0.35)
    $x1 = [int] ($workRect.X + $workRect.Width * 0.65)
    $y0 = [int] ($workRect.Y + 230)
    $y1 = [int] ($tabRect.Y - 25)
    if ($x1 -le $x0 -or $y1 -le $y0) { throw 'Analysis gesture rect is degenerate.' }
    [CampaignMouse]::SetCursorPos($x0, $y0) | Out-Null
    Start-Sleep -Milliseconds 300
    [CampaignMouse]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 200
    for ($i = 1; $i -le 12; $i++) {
        [CampaignMouse]::SetCursorPos(
            [int] ($x0 + ($x1 - $x0) * $i / 12),
            [int] ($y0 + ($y1 - $y0) * $i / 12)) | Out-Null
        Start-Sleep -Milliseconds 40
    }
    [CampaignMouse]::mouse_event(0x0004, 0, 0, 0, 0)
    $enableDeadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $enableDeadline) {
        $Pd.Refresh()
        $window = Get-Window $Pd
        $analyze = Find-ByAutomationId $window 'AnalyzeButton'
        if ($null -ne $analyze -and $analyze.Current.IsEnabled) { return $window }
        Start-Sleep -Milliseconds 500
    }
    throw 'Analysis-area gesture did not enable Analyze.'
}

function Find-VisibleById { param($Root, $Id)
    # Hidden tool pages may host same-named twins; prefer on-screen.
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $found = @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
    foreach ($candidate in $found) {
        try { if (-not $candidate.Current.IsOffscreen) { return $candidate } } catch { }
    }
    if ($found.Count -gt 0) { return $found[0] }
    return $null
}

function Expand-Picker { param($Picker)
    # WPF ComboBox items materialize as picker descendants only while the
    # dropdown is expanded. Best-effort: pickers without the pattern may
    # already expose items directly.
    try {
        $pattern = [System.Windows.Automation.ExpandCollapsePattern] $Picker.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $pattern.Expand()
            Start-Sleep -Milliseconds 600
        }
        return $true
    }
    catch { return $false }
}

function Invoke-PreviewTargetPick { param($Pd, $Window, $PickerId, $ButtonId)
    # Preview requires a selected target; fixed targets (e.g. placed
    # components) are rejected by design. Select the Native edits tab,
    # then walk picker items from last to first (bounded): select, click
    # Preview, keep the first 'PREVIEW ONLY' and skip fixed targets.
    # Preview is non-mutating, so attempts are safe. When every walked
    # target refuses as fixed, returns the refusal text so the step's
    # ExpectFailure gate asserts the exact refusal. Returns a hash with
    # Window, Matched and BeforeText for the step's evidence flow.
    $Pd.Refresh()
    $window = Get-Window $Pd
    # Scope to the on-screen workbench: hidden tool pages may host
    # same-named twins. Select the visible Native edits tab and verify.
    $tabCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NativeEditsTab')
    $tabs = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCondition))
    $tab = $null
    foreach ($candidate in $tabs) {
        try { if (-not $candidate.Current.IsOffscreen) { $tab = $candidate; break } } catch { }
    }
    if ($null -eq $tab -and $tabs.Count -gt 0) { $tab = $tabs[0] }
    if ($null -eq $tab) { throw 'NativeEditsTab not found for target picking.' }
    ([System.Windows.Automation.SelectionItemPattern] $tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 800
    $Pd.Refresh()
    $window = Get-Window $Pd
    $picker = Find-VisibleById $window $PickerId
    if ($null -eq $picker) { throw "Target picker $PickerId not found." }
    $itemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    Expand-Picker $picker | Out-Null
    $Pd.Refresh()
    $window = Get-Window $Pd
    $picker = Find-VisibleById $window $PickerId
    if ($null -eq $picker) { throw "Target picker $PickerId not found." }
    $items = @($picker.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition))
    if ($items.Count -eq 0) { throw "Target picker $PickerId has no items." }
    $start = $items.Count - 1
    $stop = [Math]::Max(0, $items.Count - 10)
    $beforeText = @(Get-AllText $window) -join "`n"
    $fixedSeen = $null
    for ($i = $start; $i -ge $stop; $i--) {
        if ($Pd.HasExited) { throw 'PD exited during target picking.' }
        try {
            # Each selection collapses the popup and virtualized containers
            # go stale; re-resolve the picker and its items per attempt.
            $Pd.Refresh()
            $window = Get-Window $Pd
            $picker = Find-VisibleById $window $PickerId
            if ($null -eq $picker) { continue }
            Expand-Picker $picker | Out-Null
            $items = @($picker.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition))
            if ($items.Count -eq 0) { continue }
            $index = [Math]::Min($i, $items.Count - 1)
            ([System.Windows.Automation.SelectionItemPattern] $items[$index].GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        }
        catch { continue }
        Start-Sleep -Milliseconds 500
        $Pd.Refresh()
        $window = Get-Window $Pd
        $button = Find-ByAutomationId $window $ButtonId
        if ($null -eq $button -or -not $button.Current.IsEnabled) { continue }
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $deadline = (Get-Date).AddSeconds(25)
        $advanced = $false
        while ((Get-Date) -lt $deadline) {
            if ($Pd.HasExited) { throw 'PD exited during target picking.' }
            $texts = @(Get-AllText $window)
            foreach ($text in $texts) {
                if ([string]::IsNullOrEmpty($text) -or $beforeText.Contains($text)) { continue }
                if ($text.Contains('PREVIEW ONLY')) {
                    return @{
                        Window = $window
                        Matched = '*preview* :: ' + (($text -replace '\s+', ' ').Trim())
                        BeforeText = $beforeText
                    }
                }
                if ($text.Contains('fixed and cannot be')) {
                    $fixedSeen = ($text -replace '\s+', ' ').Trim()
                    $advanced = $true
                    break
                }
                if ($text -match 'failed[:;]|rejected[:;]') { throw ("Action failed: " + $text) }
            }
            if ($advanced) { break }
            Start-Sleep -Milliseconds 500
        }
    }
    if ($null -ne $fixedSeen) {
        # Every walked target refused as fixed: the tool's refusal gate
        # works; the step's ExpectFailure gate asserts the exact text.
        return @{
            Window = $window
            Matched = 'FIXED-GATE :: ' + $fixedSeen
            BeforeText = $beforeText
        }
    }
    throw 'No previewable (non-fixed, valid-input) target among the last picker items.'
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
    $script:pdProcess.Refresh()
    $element = Get-Window $script:pdProcess
    $pattern = [System.Windows.Automation.WindowPattern] $element.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern)
    if (-not $pattern.Current.CanMinimize -and -not $pattern.Current.CanMaximize) {
        Write-Host '  PD visual state is locked (modal or disabled window); skipping minimize/restore, occlusion gate stays honest.'
        return
    }
    $pattern.SetWindowVisualState($State)
    Start-Sleep -Milliseconds 400
}

function Find-PdSaveDialog { param($PdId, $Root)
    # The WPF-owned Common Item Dialog surfaces as a DESCENDANT Window of
    # the PD main window (named 'Save As'), never as a Root child. The
    # primary dialog is the largest such window; smaller same-named
    # windows are validation message boxes.
    $Pd.Refresh()
    $window = Get-Window $Pd
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Save As')
    $both = New-Object System.Windows.Automation.AndCondition($typeCondition, $nameCondition)
    $found = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $both))
    if ($found.Count -eq 0) { return $null }
    $best = $null
    $bestArea = 0
    foreach ($candidate in $found) {
        try {
            $rect = $candidate.Current.BoundingRectangle
            $area = $rect.Width * $rect.Height
            if ($area -gt $bestArea) { $bestArea = $area; $best = $candidate }
        }
        catch { }
    }
    return $best
}

function Dismiss-SavePopups { param($Pd)
    # Dismisses modal popups stacked over the Save dialog: the
    # 'Location is not available' shell error (OK click) and small
    # validation message boxes (Enter). Returns when only the primary
    # dialog (or nothing) remains. Bounded; never loops forever.
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $Pd.Refresh()
        $window = Get-Window $Pd
        $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)
        $all = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $typeCondition))
        $blockers = @($all | Where-Object {
            try {
                $_.Current.Name -eq 'Location is not available' -or
                ($_.Current.Name -eq 'Save As' -and
                    $_.Current.BoundingRectangle.Width -lt 600)
            }
            catch { $false }
        })
        if ($blockers.Count -eq 0) { return }
        $first = $blockers[0]
        try {
            $name = $first.Current.Name
            if ($name -eq 'Location is not available') {
                $paneCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Pane)
                $okCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, 'OK')
                $ok = $first.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.AndCondition($paneCondition, $okCondition)))
                if ($null -eq $ok) { throw 'Error popup has no OK.' }
                $rect = $ok.Current.BoundingRectangle
                Invoke-MouseClick ([int] ($rect.X + $rect.Width / 2)) ([int] ($rect.Y + $rect.Height / 2))
            }
            else {
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                Start-Sleep -Milliseconds 800
            }
        }
        catch {
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Start-Sleep -Milliseconds 800
        }
        Start-Sleep -Seconds 2
    }
}

function Complete-PdSaveDialog { param($Pd, $Window, $ToolDir, $FileName)
    $target = Join-Path $ToolDir $FileName
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        Remove-Item -LiteralPath $target -Force
    }
    $Pd.Refresh()
    [CampaignMouse]::SetForegroundWindow([IntPtr] $Pd.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
    $dialog = $null
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and $null -eq $dialog) {
        $Pd.Refresh()
        $dialog = Find-PdSaveDialog $Pd.Id $Window
        if ($null -eq $dialog) { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $dialog) { throw 'Save dialog never appeared after invoking save.' }
    $paneCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Pane)
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Dismiss-SavePopups $Pd
        $Pd.Refresh()
        $dialog = Find-PdSaveDialog $Pd.Id $Window
        if ($null -eq $dialog) { break }
        $panes = @($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $paneCondition))
        $filePane = $null
        foreach ($pane in $panes) {
            try { if ($pane.Current.ClassName -eq 'Edit') { $filePane = $pane; break } } catch { }
        }
        if ($null -eq $filePane) { throw 'Save dialog has no file-name field.' }
        $rect = $filePane.Current.BoundingRectangle
        Invoke-MouseClick ([int] ($rect.X + $rect.Width / 2)) ([int] ($rect.Y + $rect.Height / 2))
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait($target)
        Start-Sleep -Milliseconds 800
        try {
            if (-not $filePane.Current.Name.Contains($FileName)) {
                throw 'File-name field did not accept the path.'
            }
        }
        catch {
            if ($_.Exception.Message -eq 'File-name field did not accept the path.') { throw }
        }
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        $settleDeadline = (Get-Date).AddSeconds(25)
        while ((Get-Date) -lt $settleDeadline) {
            Start-Sleep -Seconds 2
            $Pd.Refresh()
            if ((Test-Path -LiteralPath $target -PathType Leaf) -and
                ($null -eq (Find-PdSaveDialog $Pd.Id $Window))) {
                $Pd.Refresh()
                return (Get-Window $Pd)
            }
        }
    }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw 'Save dialog did not produce the scene file.'
    }
    if ($null -ne (Find-PdSaveDialog $Pd.Id $Window)) {
        throw 'Save dialog stayed open after producing the scene file.'
    }
    $Pd.Refresh()
    return (Get-Window $Pd)
}

function Save-AllegroShot { param($AllegroId, $ToolDir, $Stem)
    # PD is campaign-owned: minimize it around Allegro captures so no PD pixel
    # can overlap the Allegro rect. (PD's own shots use in-app rendering and
    # never need foreground.) Foreign occluders still fail honestly via
    # --require-unoccluded, but transient toasts get a bounded ride-out
    # (6 x 10s) before the step fails; persistent occlusion still fails.
    Set-PdVisualState ([System.Windows.Automation.WindowVisualState]::Minimized)
    try {
        $png = Join-Path $ToolDir ("$Stem.png")
        $attempt = 0
        while ($true) {
            $out = & $captureExe --pid $AllegroId --out $png --method auto --require-unoccluded 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host ("  " + ($out -join ' '))
                return
            }
            $attempt++
            if ($attempt -ge 6 -or ($out -join ' ') -notmatch 'occluded') {
                throw "Allegro capture failed: $out"
            }
            Write-Host ("  Allegro occluded (attempt $attempt/6); waiting 10s for the toast to clear...")
            Start-Sleep -Seconds 10
        }
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
$adoptPdId = 0
if ($ready.Count -ge 4) { $adoptPdId = [int] $ready[3] }
$adopted = $null
if ($adoptPdId -ne 0) {
    $candidate = Get-Process -Id $adoptPdId -ErrorAction SilentlyContinue
    if ($null -ne $candidate -and -not $candidate.HasExited -and $candidate.Path -eq $pdExe) {
        $adopted = $candidate
    }
}
if ($null -eq $adopted) {
    $stalePd = @(Get-Process -Name 'PD.Simple' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
    if ($stalePd.Count -gt 0) { throw "Close PD Simple first; found PID $($stalePd[0].Id). Nothing was touched." }
}

if ($RunRoot -eq '') {
    $RunRoot = Join-Path 'C:\e2studio\pd-simple-local-runs' ('native-connected-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
[void](New-Item -ItemType Directory -Path $RunRoot -Force)
if ($null -ne $adopted -and $ready.Count -ge 2) {
    # Adopted PD inherited the watch run's shot dir from Allegro's env.
    $shotRoot = Join-Path $ready[1] 'pd-shots'
}
else {
    $shotRoot = Join-Path $RunRoot 'pd-shots'
}
$logPath = Join-Path $RunRoot 'campaign.log'
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    if ($null -ne $adopted) {
        $pd = $adopted
        $null = $pd.Handle
        Write-Host "Adopting resident-launched PD PID $($pd.Id) against Allegro PID $allegroId..."
    }
    else {
        $env:PD_SIMPLE_SCREENSHOT_DIR = $shotRoot
        $pd = Start-Process -FilePath $pdExe -PassThru
        $null = $pd.Handle
        Write-Host "PD PID $($pd.Id) starting against Allegro PID $allegroId..."
    }
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
            # The picker lists Allegro instances as DataItems; select the
            # first available row, then Attach selected enables.
            $rowCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::DataItem)
            $rowDeadline = (Get-Date).AddMinutes(1)
            $row = $null
            while ((Get-Date) -lt $rowDeadline -and $null -eq $row) {
                $row = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
                if ($null -eq $row) { Start-Sleep -Milliseconds 500 }
            }
            if ($null -eq $row) { throw 'The attach picker showed no Allegro instance row.' }
            ([System.Windows.Automation.SelectionItemPattern] $row.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            $attachButton = Find-ByAutomationId $window 'AttachConnectionButton'
            $enableDeadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $enableDeadline) {
                if ($null -ne $attachButton -and $attachButton.Current.IsEnabled) { break }
                Start-Sleep -Milliseconds 500
                $attachButton = Find-ByAutomationId $window 'AttachConnectionButton'
            }
            if ($null -eq $attachButton -or -not $attachButton.Current.IsEnabled) {
                throw 'Attach selected never enabled after row selection.'
            }
            $attachButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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
                $matched = $null
                try {
                    if ($step['Gesture'] -eq 'AnalysisArea') {
                        $window = Invoke-AnalysisAreaGesture $pd
                    }
                    if ($null -ne $step['SetText']) {
                        foreach ($entry in $step['SetText']) {
                            $value = $entry['Text']
                            if ($null -ne $entry['File']) {
                                $value = Get-Content -LiteralPath (Join-Path $PSScriptRoot $entry['File']) -Raw -Encoding utf8
                            }
                            $value = $value.Replace('{RunRoot}', $RunRoot)
                            Set-TextById $window $entry['Id'] $value
                        }
                        Start-Sleep -Milliseconds 500
                        $pd.Refresh()
                        $window = Get-Window $pd
                    }
                    if ($null -ne $step['PickTarget']) {
                        $pick = Invoke-PreviewTargetPick $pd $window $step['PickTarget'] $label
                        $window = $pick.Window
                        $matched = $pick.Matched
                        $beforeText = $pick.BeforeText
                    }
                    if ($null -eq $matched) {
                        $control = Find-ByAutomationId $window $label
                        if ($null -eq $control) { throw "Button $label not found." }
                        if (-not $control.Current.IsEnabled) { throw "Button $label is disabled while connected." }
                        $beforeText = @(Get-AllText $window) -join "`n"
                        $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    }
                    # SaveSceneButton: Complete-PdSaveDialog already proves the
                    # file exists and the dialog closed; the async 'Saved'
                    # status may appear during that wait, so markers match
                    # current text without the freshness gate here.
                    $skipFreshness = ($label -eq 'SaveSceneButton')
                    if ($label -eq 'SaveSceneButton') {
                        $window = Complete-PdSaveDialog $pd $window $toolDir 'captured-board.allegroscene'
                    }
                    $stepDeadline = (Get-Date).AddMinutes(5)
                    $expectedFailure = $step['ExpectFailure']
                    while ((Get-Date) -lt $stepDeadline) {
                        if ($pd.HasExited) { throw 'PD exited during the step.' }
                        $texts = @(Get-AllText $window)
                        if ($null -ne $expectedFailure) {
                            foreach ($text in $texts) {
                                if (-not [string]::IsNullOrEmpty($text) -and
                                    -not $beforeText.Contains($text) -and
                                    $text.Contains($expectedFailure)) {
                                    $matched = 'EXPECTED-GATE :: ' + (($text -replace '\s+', ' ').Trim())
                                    break
                                }
                            }
                            if ($null -ne $matched) { break }
                        }
                        $bad = @($texts | Where-Object {
                            -not [string]::IsNullOrEmpty($_) -and
                            -not $beforeText.Contains($_) -and
                            ($null -eq $expectedFailure -or -not $_.Contains($expectedFailure)) -and (
                                $_.StartsWith('Check did not complete') -or
                                $_.StartsWith('Could not start check') -or
                                $_.StartsWith('Result could not be verified') -or
                                ($_ -match 'failed[:;]') -or
                                ($_ -match 'rejected[:;]')) })
                        if ($bad.Count -gt 0) { throw ("Action failed: " + $bad[0]) }
                        foreach ($text in $texts) {
                            foreach ($marker in $step.Markers) {
                                if ($text -like $marker -and ($skipFreshness -or -not $beforeText.Contains($text))) {
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
                        @(Get-AllText $window) | Set-Content -LiteralPath (Join-Path $toolDir ("text-" + $label + "-fail.txt")) -Encoding utf8
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
