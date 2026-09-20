[CmdletBinding()]
param([int] $ProcessId = 0, [string] $OutFile = '', [int] $TimeoutSeconds = 60)

# Dump actionable automation elements (buttons, menu/tab/list items, anything
# with an AutomationId) plus all visible text of a running process window.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = Get-Process -Id $ProcessId
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$window = $null
while ((Get-Date) -lt $deadline) {
    if ($process.HasExited) { throw "PID $ProcessId exited." }
    $process.Refresh()
    if ($process.MainWindowHandle -ne 0) {
        $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr] $process.MainWindowHandle)
        break
    }
    Start-Sleep -Milliseconds 200
}
if ($null -eq $window) { throw "PID $ProcessId showed no window within $TimeoutSeconds s." }
Write-Host ('window=' + $window.Current.Name)

$lines = New-Object System.Collections.Generic.List[string]
$all = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $all) {
    try {
        $current = $element.Current
        if ($current.ControlType -eq [System.Windows.Automation.ControlType]::Button -or
            $current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -or
            $current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem -or
            $current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -or
            $current.ControlType -eq [System.Windows.Automation.ControlType]::CheckBox -or
            -not [string]::IsNullOrWhiteSpace($current.AutomationId)) {
            $lines.Add($current.ControlType.ProgrammaticName + ' | id=' + $current.AutomationId +
                ' | name=' + (($current.Name -replace '\s+', ' ').Trim()) +
                ' | enabled=' + $current.IsEnabled)
        }
    }
    catch { }
}
$textCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)
$texts = @($window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants, $textCondition) |
    ForEach-Object { try { $_.Current.Name } catch { $null } } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

$out = New-Object System.Collections.Generic.List[string]
$out.Add('=== ACTIONABLE (' + $lines.Count + ') ===')
foreach ($line in ($lines | Sort-Object -Unique)) { $out.Add($line) }
$out.Add('')
$out.Add('=== TEXT (' + $texts.Count + ') ===')
foreach ($text in ($texts | Sort-Object -Unique)) { $out.Add($text) }
if ($OutFile -eq '') {
    foreach ($line in $out) { Write-Host $line }
}
else {
    $out | Set-Content -LiteralPath $OutFile -Encoding utf8
    Write-Host ('wrote ' + $OutFile)
}
