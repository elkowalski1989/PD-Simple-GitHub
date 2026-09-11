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
function Require-ManagedInstall {
    if (Test-Path -LiteralPath $installRoot) {
        $oldManifest = Join-Path $installRoot 'pd-simple-payload.json'
        if (-not (Test-Path -LiteralPath $oldManifest) -or
            (Get-Content -LiteralPath $oldManifest -Raw | ConvertFrom-Json).product -ne 'pd-simple') {
            throw "Refusing to replace an unrecognized directory: $installRoot"
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
        'app/AllegroBridge.Host.exe'
        'app/AllegroBridge/Resident/pd_allegro_bridge.il'
        'app/AllegroBridge/Resident/pd_custom_extensions.il'
        'app/AllegroBridge/Resident/pd_constraint_observer.il'
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
    $oldInit = Read-Init
    $newInit = Without-ManagedBlock $oldInit
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
if (@(Get-Process -Name 'PD.Simple' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close PD Simple before installing an update.'
}
$oldInit = Read-Init
$baseInit = Without-ManagedBlock $oldInit
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
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app') -Destination $stage -Recurse
    Copy-Item -LiteralPath $manifestPath -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pd_simple_loader.il.in') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $stage
    [IO.File]::WriteAllText((Join-Path $stage 'pd_simple_loader.il'), $loader, [Text.Encoding]::ASCII)
    Verify-Payload $stage $manifest
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
Write-Host 'Restart Allegro, open a board, then enter pd_simple.'
Write-Host 'Your full Workflow Engine and Cadence system menus were not changed.'
Write-Host 'If another bridge companion is running, pd_simple asks before switching it.'
