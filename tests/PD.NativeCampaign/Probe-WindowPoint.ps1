[CmdletBinding()]
param([int] $ProcessId = 0, [int] $OffsetX = 1350, [int] $OffsetY = 650)

# Probe which process/window actually owns a screen point inside the target
# window (answers: Allegro's own pixels vs an occluding foreign window).
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WP {
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@

$process = Get-Process -Id $ProcessId
$rect = New-Object WP+RECT
[WP]::GetWindowRect($process.MainWindowHandle, [ref] $rect) | Out-Null
Write-Host ('winrect=' + $rect.L + ',' + $rect.T + ',' + $rect.R + ',' + $rect.B)
$point = New-Object WP+POINT
$point.X = $rect.L + $OffsetX
$point.Y = $rect.T + $OffsetY
$hit = [WP]::WindowFromPoint($point)
$hitPid = 0
[WP]::GetWindowThreadProcessId($hit, [ref] $hitPid) | Out-Null
$text = New-Object Text.StringBuilder 256
[WP]::GetWindowText($hit, $text, 256) | Out-Null
Write-Host ('hitpid=' + $hitPid + ' text=' + $text.ToString())
$owner = Get-Process -Id $hitPid -ErrorAction SilentlyContinue
if ($null -ne $owner) { Write-Host ('proc=' + $owner.ProcessName + ' path=' + $owner.Path) }
Write-Host ('foreground-is-target=' + ([WP]::GetForegroundWindow() -eq $process.MainWindowHandle))
