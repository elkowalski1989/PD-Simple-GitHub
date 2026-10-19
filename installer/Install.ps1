param(
    [switch]$VerifyOnly,
    [switch]$Uninstall,
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PD-Simple'),
    [string]$InitPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'SPB_Data/pcbenv/allegro.ilinit')
)
$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
$initPath = [IO.Path]::GetFullPath($InitPath)
if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\', '/')) {
    throw 'A drive root is not an installation directory.'
}
$begin = ';; --- PD Simple loader BEGIN ---'
$end = ';; --- PD Simple loader END ---'
$blockPattern = '(?ms)^' + [regex]::Escape($begin) + '\r?\n.*?^' + [regex]::Escape($end) + '(?:\r?\n)?'
$workflowBegin = ';; --- PD AI Workflow loader BEGIN ---'
$workflowEnd = ';; --- PD AI Workflow loader END ---'
$workflowBlockPattern = '(?ms)^' + [regex]::Escape($workflowBegin) + '\r?\n.*?^' + [regex]::Escape($workflowEnd) + '(?:\r?\n)?'
$workflowBackupName = 'retired-ai-workflow-startup.txt'
$preferencesName = 'preferences.json'
$stamp = [guid]::NewGuid().ToString('N')

function Read-Init {
    if (-not (Test-Path -LiteralPath $initPath)) {
        return ''
    }
    # A strict UTF-8 read refuses to corrupt a legacy-encoded user's init.
    return [IO.File]::ReadAllText($initPath, [Text.UTF8Encoding]::new($false, $true))
}
function Without-ManagedBlock([string]$text) {
    $starts = [regex]::Matches($text, [regex]::Escape($begin)).Count
    $ends = [regex]::Matches($text, [regex]::Escape($end)).Count
    $blocks = [regex]::Matches($text, $blockPattern).Count
    if ($starts -ne $ends -or $starts -ne $blocks -or $blocks -gt 1) {
        throw 'The PD Simple startup block is malformed; no startup file was changed.'
    }
    return [regex]::Replace($text, $blockPattern, '')
}
function Get-WorkflowStartupBlock([string]$text) {
    $starts = [regex]::Matches($text, [regex]::Escape($workflowBegin)).Count
    $ends = [regex]::Matches($text, [regex]::Escape($workflowEnd)).Count
    $matches = [regex]::Matches($text, $workflowBlockPattern)
    if ($starts -ne $ends -or $starts -ne $matches.Count -or $matches.Count -gt 1) {
        throw 'The AI Workflow startup block is malformed; no startup file was changed.'
    }
    if ($matches.Count -eq 0) {
        return ''
    }
    return $matches[0].Value
}
function Without-WorkflowStartupBlock([string]$text) {
    [void](Get-WorkflowStartupBlock $text)
    return [regex]::Replace($text, $workflowBlockPattern, '')
}
function Require-ManagedInstall {
    if (Test-Path -LiteralPath $installRoot) {
        $oldManifest = Join-Path $installRoot 'pd-simple-payload.json'
        if (-not (Test-Path -LiteralPath $oldManifest) -or
            (Get-Content -LiteralPath $oldManifest -Raw | ConvertFrom-Json).product -ne 'pd-simple') {
            throw "Refusing to replace an unrecognized directory: $installRoot"
        }
    }
}
function Require-InstallNotRunning {
    try {
        $runningProcesses = @(Get-Process -Name 'PD.Simple' -ErrorAction Stop)
    } catch {
        if ($_.FullyQualifiedErrorId -eq 'NoProcessFoundForGivenName,Microsoft.PowerShell.Commands.GetProcessCommand') {
            return
        }
        throw
    }
    $installationPrefix = $installRoot + [IO.Path]::DirectorySeparatorChar
    foreach ($process in $runningProcesses) {
        try {
            $reportedExecutable = [string]$process.Path
            if ([string]::IsNullOrWhiteSpace($reportedExecutable) -or
                $reportedExecutable -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}(?![?.][\\/])[^\\/]+[\\/][^\\/]+[\\/])') {
                throw 'The running process did not report an absolute executable path.'
            }
            $executable = [IO.Path]::GetFullPath($reportedExecutable)
        } catch {
            throw [InvalidOperationException]::new(
                'Cannot authenticate a running PD Simple executable; installation was refused.',
                $_.Exception)
        }
        if ($executable.Equals($installRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $executable.StartsWith($installationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close PD Simple from the selected installation before changing it.'
        }
        # GetFullPath only normalizes the spelling. A lexical mismatch cannot
        # prove that an installation reached through a reparse point is separate.
        $inspectionPaths = @(
            @{ Path = $installRoot; AllowMissing = $true },
            @{ Path = $executable; AllowMissing = $false }
        )
        foreach ($inspection in $inspectionPaths) {
            $ancestor = $inspection.Path
            $allowMissing = $inspection.AllowMissing
            while ($ancestor) {
                try {
                    $entry = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
                    $attributes = $entry.Attributes
                } catch {
                    if ($allowMissing -and $_.Exception -is [Management.Automation.ItemNotFoundException]) {
                        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
                        continue
                    }
                    throw [InvalidOperationException]::new(
                        'Cannot inspect installation or executable ancestry; installation was refused.',
                        $_.Exception)
                }
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Installation or executable ancestry contains a reparse point; installation was refused.'
                }
                $allowMissing = $false
                $ancestor = [IO.Path]::GetDirectoryName($ancestor)
            }
        }
    }
}
function Verify-Payload([string]$root, $manifest) {
    if ($manifest.product -ne 'pd-simple' -or
        $manifest.version -ne '1.0.0' -or
        @($manifest.files).Count -lt 5) {
        throw 'Invalid PD Simple payload manifest.'
    }
    $seen = @{}
    foreach ($entry in $manifest.files) {
        if ($entry.path -notmatch '^(app/[A-Za-z0-9_./ -]+|pd_simple_loader\.il\.in)$' -or
            $entry.path -match '(^|/)\.\.(/|$)' -or
            $seen.ContainsKey($entry.path)) {
            throw 'Unsafe or duplicate payload path.'
        }
        $seen[$entry.path] = $true
        $file = Join-Path $root $entry.path
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Missing payload: $($entry.path)"
        }
        if ((Get-Item -LiteralPath $file).Length -ne $entry.size -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Payload hash mismatch: $($entry.path)"
        }
    }
    $requiredFiles = @(
        'app/PD.Simple.exe'
        'app/PcbSdk.Host.exe'
        'app/PcbSdk/Resident/pcb_engine.il'
        'app/PcbSdk/Resident/pcb_engine_extensions.il'
        'app/PcbSdk/Resident/pcb_engine_constraints.il'
        'pd_simple_loader.il.in'
    )
    foreach ($required in $requiredFiles) {
        if (-not $seen.ContainsKey($required)) {
            throw "Required payload absent: $required"
        }
    }
    $actual = @(
        Get-ChildItem -LiteralPath (Join-Path $root 'app') -Recurse -File |
            ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') }
    )
    if (@($actual | Where-Object { -not $seen.ContainsKey($_) }).Count -gt 0) {
        throw 'Untracked application files in payload.'
    }
}

if ($Uninstall) {
    Require-ManagedInstall
    Require-InstallNotRunning
    $oldInit = Read-Init
    $newInit = Without-ManagedBlock $oldInit
    $workflowBackupPath = Join-Path $installRoot $workflowBackupName
    if ((Get-WorkflowStartupBlock $newInit).Length -eq 0 -and
        (Test-Path -LiteralPath $workflowBackupPath -PathType Leaf)) {
        $workflowStartup = [IO.File]::ReadAllText(
            $workflowBackupPath,
            [Text.UTF8Encoding]::new($false, $true))
        $newInit = $newInit.TrimEnd("`r", "`n") + "`r`n`r`n" +
            $workflowStartup.TrimEnd("`r", "`n") + "`r`n"
    }
    if ($oldInit -ne $newInit) {
        Copy-Item -LiteralPath $initPath -Destination ($initPath + '.pd-simple-backup-' + $stamp)
        [IO.File]::WriteAllText($initPath, $newInit, [Text.UTF8Encoding]::new($false))
    }
    if (Test-Path -LiteralPath $installRoot) {
        $recovery = $installRoot + '.removed-' + $stamp
        Move-Item -LiteralPath $installRoot -Destination $recovery
        Write-Host "Uninstalled. Recoverable files: $recovery"
    }
    Write-Host 'PD Simple startup entry removed. Other Allegro startup entries were preserved.'
    return
}
$manifestPath = Join-Path $PSScriptRoot 'pd-simple-payload.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Verify-Payload $PSScriptRoot $manifest
Write-Host "Verified PD Simple $($manifest.version): $(@($manifest.files).Count) files."
if ($VerifyOnly) {
    return
}
Require-ManagedInstall
Require-InstallNotRunning
$oldInit = Read-Init
$baseInit = Without-ManagedBlock $oldInit
$workflowStartup = Get-WorkflowStartupBlock $baseInit
if ($workflowStartup.Length -eq 0 -and (Test-Path -LiteralPath $installRoot)) {
    $previousWorkflowBackup = Join-Path $installRoot $workflowBackupName
    if (Test-Path -LiteralPath $previousWorkflowBackup -PathType Leaf) {
        $workflowStartup = [IO.File]::ReadAllText(
            $previousWorkflowBackup,
            [Text.UTF8Encoding]::new($false, $true))
    }
}
$baseInit = Without-WorkflowStartupBlock $baseInit
$skillRoot = $installRoot.Replace('\', '/').Replace('"', '\"')
if ($skillRoot.ToCharArray() | Where-Object { [int]$_ -gt 127 -or [int]$_ -lt 32 }) {
    throw 'The SKILL launcher currently requires an ASCII installation path.'
}
$loader = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'pd_simple_loader.il.in')).Replace('@INSTALL_ROOT@', $skillRoot)
$loadLine = '(errset (load "' + $skillRoot + '/pd_simple_loader.il") t)'
$newInit = $baseInit.TrimEnd("`r", "`n") + "`r`n`r`n" + $begin + "`r`n" + $loadLine + "`r`n" + $end + "`r`n"
$stage = $installRoot + '.stage-' + $stamp
$previous = $null
$initWritten = $false
$preferencesHash = $null
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app') -Destination $stage -Recurse
    Copy-Item -LiteralPath $manifestPath -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pd_simple_loader.il.in') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $stage
    if ($workflowStartup.Length -gt 0) {
        [IO.File]::WriteAllText(
            (Join-Path $stage $workflowBackupName),
            $workflowStartup,
            [Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText((Join-Path $stage 'pd_simple_loader.il'), $loader, [Text.Encoding]::ASCII)
    Verify-Payload $stage $manifest
    if (Test-Path -LiteralPath $installRoot) {
        $existingPreferences = Join-Path $installRoot $preferencesName
        if (Test-Path -LiteralPath $existingPreferences -PathType Leaf) {
            $stagedPreferences = Join-Path $stage $preferencesName
            Copy-Item -LiteralPath $existingPreferences -Destination $stagedPreferences
            $preferencesHash = (Get-FileHash -LiteralPath $existingPreferences -Algorithm SHA256).Hash
            if ((Get-FileHash -LiteralPath $stagedPreferences -Algorithm SHA256).Hash -ne $preferencesHash) {
                throw 'The saved PD Simple preferences changed while staging the update.'
            }
        }
    }
    Require-InstallNotRunning
    if (Test-Path -LiteralPath $installRoot) {
        $previous = $installRoot + '.previous-' + $stamp
        Move-Item -LiteralPath $installRoot -Destination $previous
    }
    Move-Item -LiteralPath $stage -Destination $installRoot
    New-Item -ItemType Directory -Path (Split-Path -Parent $initPath) -Force | Out-Null
    if (Test-Path -LiteralPath $initPath) {
        Copy-Item -LiteralPath $initPath -Destination ($initPath + '.pd-simple-backup-' + $stamp)
    }
    $initWritten = $true
    [IO.File]::WriteAllText($initPath, $newInit, [Text.UTF8Encoding]::new($false))
    Verify-Payload $installRoot $manifest
    if ($preferencesHash -and
        (Get-FileHash -LiteralPath (Join-Path $installRoot $preferencesName) -Algorithm SHA256).Hash -ne $preferencesHash) {
        throw 'The saved PD Simple preferences were not retained by the update.'
    }
    if ([IO.File]::ReadAllText($initPath) -ne $newInit -or
        [IO.File]::ReadAllText((Join-Path $installRoot 'pd_simple_loader.il')) -ne $loader) {
        throw 'Installed startup loader verification failed.'
    }
} catch {
    $installError = $_
    # Restore configuration and application independently: a locked init file
    # must not prevent the previous application directory from being restored.
    if ($initWritten) {
        try {
            [IO.File]::WriteAllText($initPath, $oldInit, [Text.UTF8Encoding]::new($false))
        } catch {
            Write-Warning ('Startup restore could not write the file: ' + $_.Exception.Message)
        }
    }
    try {
        if (-not (Test-Path -LiteralPath $stage) -and (Test-Path -LiteralPath $installRoot)) {
            Move-Item -LiteralPath $installRoot -Destination ($installRoot + '.failed-' + $stamp)
        }
        if ($previous -and (Test-Path -LiteralPath $previous)) {
            Move-Item -LiteralPath $previous -Destination $installRoot
        }
    } catch {
        Write-Warning ('Application restore needs attention: ' + $_.Exception.Message)
    }
    throw $installError
}
Write-Host "Installed: $installRoot"
if ($previous) {
    Write-Host "Previous version retained: $previous"
}
Write-Host 'Restart Allegro and open a board. PD Simple will connect hidden.'
Write-Host 'Use AI Workflow > Open AI Workflow, its toolbar button, or pd_simple to show it.'
Write-Host 'The prior AI Workflow startup hook was retired recoverably; Cadence system menus were not changed.'
Write-Host 'If another bridge companion is running, pd_simple asks before switching it.'
