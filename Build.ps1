$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$artifacts = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$stage = Join-Path $artifacts ('build-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'PD-Simple'
$app = Join-Path $package 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null

# The only binary dependency is the pinned package in packages/.
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
    'Skill/pd_simple_controls.il'
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required) -PathType Leaf)) {
        throw "Missing published file: $required"
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer/pd_simple_loader.il.in') -Destination $package
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

# Source archive is explicit: no board files, logs, build caches, or credentials.
$source = Join-Path $stage 'source/PD-Simple'
New-Item -ItemType Directory -Path $source -Force | Out-Null
foreach ($file in @('README.md', 'CONTRIBUTING.md', 'THIRD_PARTY_NOTICES.md',
        'Build.cmd', 'Build.ps1', 'NuGet.Config', '.gitignore', '.gitattributes', '.editorconfig')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $source
}
foreach ($folder in @('src', 'installer', 'packages', 'tests')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $folder))) {
        continue
    }
    Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            $destination = Join-Path $source $_.FullName.Substring($projectRoot.Length + 1)
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
}
$sourceZip = Join-Path $artifacts 'PD-Simple-Source.zip'
if (Test-Path -LiteralPath $sourceZip) {
    Move-Item -LiteralPath $sourceZip -Destination ($sourceZip + '.previous-' + [guid]::NewGuid().ToString('N'))
}
Compress-Archive -LiteralPath $source -DestinationPath $sourceZip -CompressionLevel Optimal
Write-Host "Ready to share: $setupZip"
Write-Host "Source example: $sourceZip"
Write-Host "Unpacked build: $package"
