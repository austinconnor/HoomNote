param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$UpdateUrl = $env:HOOMNOTE_UPDATE_URL,
    [string]$ReleaseNotesPath = '',
    [string]$AzureTrustedSignFile = $env:HOOMNOTE_AZURE_TRUSTED_SIGN_FILE,
    [string]$SignParams = $env:HOOMNOTE_SIGN_PARAMS
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($UpdateUrl)) {
    $UpdateUrl = 'https://github.com/austinconnor/HoomNote'
}

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'src\HoomNote.App\HoomNote.App.csproj'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$portableOutput = Join-Path $artifactsRoot 'HoomNote'
$releaseOutput = Join-Path $artifactsRoot 'HoomNote-Releases'
$stableInstaller = Join-Path $artifactsRoot 'HoomNote-Setup.exe'
$stablePortableZip = Join-Path $artifactsRoot 'HoomNote-Portable.zip'
$icon = Join-Path $projectRoot 'src\HoomNote.App\Assets\AppIcon.ico'
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$channel = $Runtime

function Reset-ArtifactDirectory([string]$Path) {
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactsRoot)
    $resolvedTarget = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedTarget.StartsWith($resolvedArtifacts + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a build directory outside the HoomNote artifacts folder."
    }
    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
}

[xml]$projectXml = Get-Content -Raw $project
$versionNode = $projectXml.Project.PropertyGroup |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.Version) } |
    Select-Object -First 1
$version = [string]$versionNode.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'HoomNote project version could not be determined.'
}

if (-not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile) -and
    -not [string]::IsNullOrWhiteSpace($SignParams)) {
    throw 'Use either -AzureTrustedSignFile or -SignParams, not both.'
}

Reset-ArtifactDirectory $portableOutput
Reset-ArtifactDirectory $releaseOutput

dotnet restore $project -r $Runtime -p:Platform=$platform -p:PublishReadyToRun=false -p:WindowsAppSDKSelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'HoomNote restore failed.' }

dotnet publish $project -c Release -p:Platform=$platform -r $Runtime --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false --no-restore -o $portableOutput
if ($LASTEXITCODE -ne 0) { throw 'HoomNote publish failed.' }

$feedConfiguration = [ordered]@{
    url = if ([string]::IsNullOrWhiteSpace($UpdateUrl)) { '' } else { $UpdateUrl.TrimEnd('/') }
    source = if ($UpdateUrl -match '^https://github\.com/') { 'github' } else { 'http' }
    channel = $channel
}
$feedConfiguration | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $portableOutput 'update-feed.json') -Encoding utf8NoBOM

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Velopack tool restore failed.' }

$packArguments = @(
    'tool', 'run', 'vpk', 'pack',
    '--packId', 'HoomNote',
    '--packVersion', $version,
    '--packDir', $portableOutput,
    '--mainExe', 'HoomNote.exe',
    '--packTitle', 'HoomNote',
    '--packAuthors', 'HoomNote contributors',
    '--outputDir', $releaseOutput,
    '--runtime', $Runtime,
    '--channel', $channel,
    '--icon', $icon,
    '--shortcuts', 'StartMenuRoot,Desktop'
)

if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $defaultReleaseNotes = Join-Path $projectRoot 'docs\release-notes.md'
    if (Test-Path -LiteralPath $defaultReleaseNotes) {
        $ReleaseNotesPath = $defaultReleaseNotes
    }
}
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $resolvedReleaseNotes = [System.IO.Path]::GetFullPath($ReleaseNotesPath)
    if (-not (Test-Path -LiteralPath $resolvedReleaseNotes)) {
        throw "Release notes file not found: $resolvedReleaseNotes"
    }
    $packArguments += @('--releaseNotes', $resolvedReleaseNotes)
}

if (-not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    $resolvedSigningFile = [System.IO.Path]::GetFullPath($AzureTrustedSignFile)
    if (-not (Test-Path -LiteralPath $resolvedSigningFile)) {
        throw "Azure Artifact Signing metadata file not found: $resolvedSigningFile"
    }
    $packArguments += @('--azureTrustedSignFile', $resolvedSigningFile)
}
elseif (-not [string]::IsNullOrWhiteSpace($SignParams)) {
    $packArguments += @('--signParams', $SignParams)
}

& dotnet @packArguments
if ($LASTEXITCODE -ne 0) { throw 'HoomNote installer packaging failed.' }

$generatedInstaller = Get-ChildItem -LiteralPath $releaseOutput -Filter 'HoomNote-*-Setup.exe' -File |
    Select-Object -First 1
$generatedPortableZip = Get-ChildItem -LiteralPath $releaseOutput -Filter 'HoomNote-*-Portable.zip' -File |
    Select-Object -First 1
$executable = Join-Path $portableOutput 'HoomNote.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'HoomNote.exe was not created.' }
if ($null -eq $generatedInstaller) { throw 'HoomNote installer was not created.' }
if ($null -eq $generatedPortableZip) { throw 'HoomNote portable ZIP was not created.' }

Copy-Item -LiteralPath $generatedInstaller.FullName -Destination $stableInstaller -Force
Copy-Item -LiteralPath $generatedPortableZip.FullName -Destination $stablePortableZip -Force

$checksums = Get-ChildItem -LiteralPath $releaseOutput -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
    }
$checksums | Set-Content -LiteralPath (Join-Path $releaseOutput 'SHA256SUMS.txt') -Encoding ascii

Write-Host "HoomNote portable app: $executable"
Write-Host "HoomNote one-click installer: $stableInstaller"
Write-Host "HoomNote portable ZIP: $stablePortableZip"
Write-Host "OTA feed assets: $releaseOutput"
if ([string]::IsNullOrWhiteSpace($AzureTrustedSignFile) -and
    [string]::IsNullOrWhiteSpace($SignParams)) {
    Write-Warning 'Installer is unsigned and may trigger Windows SmartScreen. Configure signing before public distribution.'
}
