[CmdletBinding()]
param(
    [string] $BridgeRoot = 'C:\e2studio\allegro-bridge',
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version = '1.13.0-preview.4',
    [string] $SigningKeysPath,
    [string] $BundledLicenseKeyPath,
    [switch] $ConfigureAllegro
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run this rebuild helper on Windows.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$BridgeRoot = [IO.Path]::GetFullPath($BridgeRoot)
$bundleScript = Join-Path $BridgeRoot 'scripts\build-development-bundle.ps1'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$projectPath = Join-Path $repoRoot 'src\PD.Simple\PD.Simple.csproj'
$packagePath = Join-Path $repoRoot "packages\CircuitHub.AllegroBridge.Sdk.$Version.nupkg"
$manifestPath = Join-Path $repoRoot 'packages\development-bundle.json'
$cacheHost = Join-Path $repoRoot ('.packages\circuithub.allegrobridge.sdk\' + $Version.ToLowerInvariant() + '\tools\win-x64\AllegroBridge.Host.exe')
$outputHost = Join-Path $repoRoot 'src\PD.Simple\bin\Release\net10.0-windows\AllegroBridge.Host.exe'
$outputExe = Join-Path $repoRoot 'src\PD.Simple\bin\Release\net10.0-windows\PD.Simple.exe'

if ([string]::IsNullOrWhiteSpace($SigningKeysPath)) {
    $SigningKeysPath = Join-Path $BridgeRoot '_local-runs\release-inputs\CircuitHubSigningKeys.json'
}
if ([string]::IsNullOrWhiteSpace($BundledLicenseKeyPath)) {
    $BundledLicenseKeyPath = Join-Path $BridgeRoot '_local-runs\release-inputs\CircuitHubBundledLicenseKey.txt'
}
$SigningKeysPath = [IO.Path]::GetFullPath($SigningKeysPath)
$BundledLicenseKeyPath = [IO.Path]::GetFullPath($BundledLicenseKeyPath)

foreach ($required in @($bundleScript, $propsPath, $projectPath, $SigningKeysPath, $BundledLicenseKeyPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required file not found: $required"
    }
}

$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @('PD.Simple', 'AllegroBridge.Host')
})
if ($running.Count -gt 0) {
    $names = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
    throw "Close the running PD/Bridge processes before rebuilding: $names"
}

function Get-ZipEntrySha256 {
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $EntryName
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $zip.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive entry not found: $EntryName"
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $stream = $entry.Open()
            try {
                return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
            } finally {
                $stream.Dispose()
            }
        } finally {
            $sha.Dispose()
        }
    } finally {
        $zip.Dispose()
    }
}

Write-Host ''
Write-Host '============================================================'
Write-Host " PD-Simple local Bridge rebuild -- $Version"
Write-Host '============================================================'
Write-Host ''

# Pin the consumer to the exact development package version using XML semantics.
# Do not use a regex replacement here: a replacement string beginning with "$1"
# followed immediately by a numeric version can be interpreted as a different
# capture-group reference and leave the MSBuild property empty or malformed.
[xml]$propsXml = [IO.File]::ReadAllText($propsPath)
$versionNodes = @($propsXml.SelectNodes('/Project/PropertyGroup/AllegroBridgePackageVersion'))
if ($versionNodes.Count -ne 1) {
    throw 'Directory.Build.props must contain exactly one AllegroBridgePackageVersion element.'
}
$versionNodes[0].InnerText = $Version
$xmlSettings = New-Object System.Xml.XmlWriterSettings
$xmlSettings.Indent = $true
$xmlSettings.Encoding = New-Object System.Text.UTF8Encoding($false)
$xmlWriter = [System.Xml.XmlWriter]::Create($propsPath, $xmlSettings)
try {
    $propsXml.Save($xmlWriter)
} finally {
    $xmlWriter.Dispose()
}

[xml]$writtenProps = [IO.File]::ReadAllText($propsPath)
$writtenNode = $writtenProps.SelectSingleNode('/Project/PropertyGroup/AllegroBridgePackageVersion')
if ($null -eq $writtenNode -or [string]::IsNullOrWhiteSpace($writtenNode.InnerText) -or $writtenNode.InnerText -ne $Version) {
    throw "Directory.Build.props did not retain the requested AllegroBridge package version $Version."
}
Write-Host "Pinned Directory.Build.props to $Version."

# The bundle helper owns exact-version cache invalidation. This removes the local
# feed package, repo-scoped NuGet extraction and consumer bin/obj before copying
# the freshly built matching SDK/Engine/WPF packages.
& $bundleScript `
    -Version $Version `
    -SigningKeysPath $SigningKeysPath `
    -BundledLicenseKeyPath $BundledLicenseKeyPath `
    -ConsumerRoot $repoRoot `
    -RefreshConsumerCache
if ($LASTEXITCODE -ne 0) {
    throw "AllegroBridge development bundle failed with exit code $LASTEXITCODE."
}

Push-Location $repoRoot
try {
    $evaluatedVersion = (& dotnet msbuild 'src\PD.Simple\PD.Simple.csproj' -nologo -getProperty:AllegroBridgePackageVersion).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not evaluate AllegroBridgePackageVersion before restore.'
    }
    if ([string]::IsNullOrWhiteSpace($evaluatedVersion) -or $evaluatedVersion -ne $Version) {
        throw "PD.Simple evaluates AllegroBridgePackageVersion as '$evaluatedVersion' instead of '$Version'. Restore was not attempted."
    }
    Write-Host "Verified evaluated AllegroBridgePackageVersion: $evaluatedVersion"

    & dotnet restore 'src\PD.Simple\PD.Simple.csproj' --force --no-cache
    if ($LASTEXITCODE -ne 0) { throw 'PD.Simple restore failed.' }
    & dotnet build 'src\PD.Simple\PD.Simple.csproj' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'PD.Simple build failed.' }
} finally {
    Pop-Location
}

foreach ($required in @($packagePath, $manifestPath, $cacheHost, $outputHost, $outputExe)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Expected rebuild output not found: $required"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.bundled_license_included -ne $true) {
    throw 'The development bundle manifest says the protected host was built without an included license.'
}

$packageHostHash = Get-ZipEntrySha256 -ArchivePath $packagePath -EntryName 'tools/win-x64/AllegroBridge.Host.exe'
$cacheHostHash = (Get-FileHash -LiteralPath $cacheHost -Algorithm SHA256).Hash.ToLowerInvariant()
$outputHostHash = (Get-FileHash -LiteralPath $outputHost -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestHostHash = ([string]$manifest.host_sha256).ToLowerInvariant()

if ($packageHostHash -ne $manifestHostHash -or
    $cacheHostHash -ne $manifestHostHash -or
    $outputHostHash -ne $manifestHostHash) {
    throw @"
AllegroBridge.Host.exe is not coherent across the development bundle, NuGet cache and PD.Simple output.
Package host: $packageHostHash
Manifest:     $manifestHostHash
NuGet cache:  $cacheHostHash
PD output:    $outputHostHash
Do not launch Allegro with this build.
"@
}

Write-Host ''
Write-Host 'Verified the same protected host at all three handoff points:'
Write-Host "  package: $packagePath"
Write-Host "  cache:   $cacheHost"
Write-Host "  output:  $outputHost"
Write-Host 'Verified: the bundle was built with an explicit bundled-license input.'
Write-Host "PD.Simple.exe: $outputExe"

if ($ConfigureAllegro) {
    $selector = Join-Path $BridgeRoot 'set-allegro-gui.bat'
    if (-not (Test-Path -LiteralPath $selector -PathType Leaf)) {
        throw "Allegro GUI selector not found: $selector"
    }
    Write-Host ''
    Write-Host 'Starting Allegro GUI selector. Choose PD-Simple when prompted.'
    & $selector
    if ($LASTEXITCODE -ne 0) {
        throw "set-allegro-gui.bat failed with exit code $LASTEXITCODE."
    }
}

Write-Host ''
Write-Host 'SUCCESS: package, restored cache and PD.Simple output are coherent.'
Write-Host 'Fully exit and reopen Allegro before testing the new build.'