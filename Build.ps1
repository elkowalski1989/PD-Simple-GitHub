[CmdletBinding()]
param(
    [switch]$SourceOnly,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

function New-PdSourceArchive {
    param([string]$projectRoot, [string]$stage, [string]$artifacts)
    $sourceZip = Join-Path $artifacts 'PD-Simple-Source.zip'
    & python (Join-Path $projectRoot 'scripts/package-source.py') --root $projectRoot --output $sourceZip | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'PD source packaging failed.'
    }
    return $sourceZip
}

$projectRoot = $PSScriptRoot
$propsPath = Join-Path $projectRoot 'Directory.Build.props'
$loaderTemplatePath = Join-Path $projectRoot 'installer/pd_simple_loader.il.in'

try {
    [xml]$propsXml = [IO.File]::ReadAllText($propsPath)
} catch {
    throw "Directory.Build.props is invalid XML: $($_.Exception.Message)"
}
$versionNodes = @($propsXml.SelectNodes('//AllegroBridgePackageVersion'))
if ($versionNodes.Count -ne 1 -or
    [string]::IsNullOrWhiteSpace($versionNodes[0].InnerText)) {
    throw 'Directory.Build.props must contain exactly one non-empty AllegroBridgePackageVersion.'
}
$bridgePackageVersion = $versionNodes[0].InnerText
$loaderTemplate = [IO.File]::ReadAllText($loaderTemplatePath)
$versionPlaceholder = '@SDK_VERSION@'
$placeholderCount = [regex]::Matches(
    $loaderTemplate,
    [regex]::Escape($versionPlaceholder)).Count
if ($placeholderCount -ne 3 -or $loaderTemplate -match '1\.13\.0-preview\.\d+') {
    throw 'The PD Simple loader must use exactly three @SDK_VERSION@ placeholders and no literal preview version.'
}
$materializedLoader = $loaderTemplate.Replace(
    $versionPlaceholder,
    $bridgePackageVersion)

$artifacts = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot 'artifacts'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$stage = Join-Path $artifacts ('build-' + [guid]::NewGuid().ToString('N'))
if ($SourceOnly) {
    $sourceZip = New-PdSourceArchive -projectRoot $projectRoot -stage $stage -artifacts $artifacts
    Write-Host "Private-recipient source archive: $sourceZip"
    return
}
$package = Join-Path $stage 'PD-Simple'
$app = Join-Path $package 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null

# The binary dependencies are the matching pinned SDK/WPF packages in packages/.
& dotnet publish (Join-Path $projectRoot 'src/PD.Simple/PD.Simple.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --disable-build-servers `
    -o $app
if ($LASTEXITCODE -ne 0) {
    throw 'PD Simple publish failed.'
}

$requiredFiles = @(
    'PD.Simple.exe'
    'AllegroBridge.Host.exe'
    'AllegroBridge/Resident/pd_allegro_bridge.il'
    'AllegroBridge/Resident/pd_custom_extensions.il'
    'AllegroBridge/Resident/pd_constraint_observer.il'
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required) -PathType Leaf)) {
        throw "Missing published file: $required"
    }
}
[IO.File]::WriteAllText(
    (Join-Path $package 'pd_simple_loader.il.in'),
    $materializedLoader,
    [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer/Install.ps1') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer/Install.cmd') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $package
$files = @(Get-ChildItem -LiteralPath $app -Recurse -File)
$files += Get-Item -LiteralPath (Join-Path $package 'pd_simple_loader.il.in')
$manifest = [ordered]@{
    product = 'pd-simple'
    version = '1.0.0'
    files = @(
        $files | Sort-Object FullName | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($package.Length + 1).Replace('\', '/')
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    )
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'pd-simple-payload.json') -Encoding UTF8

# Validate the same payload that the team installer will admit.
& (Join-Path $package 'Install.ps1') -VerifyOnly
$setupZip = Join-Path $artifacts 'PD-Simple-Setup.zip'
if (Test-Path -LiteralPath $setupZip) {
    Move-Item -LiteralPath $setupZip -Destination ($setupZip + '.previous-' + [guid]::NewGuid().ToString('N'))
}
Compress-Archive -LiteralPath $package -DestinationPath $setupZip -CompressionLevel Optimal

$sourceZip = New-PdSourceArchive -projectRoot $projectRoot -stage $stage -artifacts $artifacts
Write-Host "Private-recipient setup archive: $setupZip"
Write-Host "Private-recipient source archive: $sourceZip"
Write-Host "Unpacked build: $package"
