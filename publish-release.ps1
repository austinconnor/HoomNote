param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Repository = 'austinconnor/HoomNote',
    [string]$AzureTrustedSignFile = $env:HOOMNOTE_AZURE_TRUSTED_SIGN_FILE,
    [string]$SignParams = $env:HOOMNOTE_SIGN_PARAMS
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'src\HoomNote.App\HoomNote.App.csproj'
$releaseDirectory = Join-Path $projectRoot 'artifacts\HoomNote-Releases'
$releaseNotes = Join-Path $projectRoot 'docs\release-notes.md'
$repositoryUrl = "https://github.com/$Repository"

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI is not authenticated.' }

[xml]$projectXml = Get-Content -Raw $project
$versionNode = $projectXml.Project.PropertyGroup |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.Version) } |
    Select-Object -First 1
$version = [string]$versionNode.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'HoomNote project version could not be determined.' }
$tag = "v$version"

gh release view $tag --repo $Repository *> $null
if ($LASTEXITCODE -eq 0) {
    throw "GitHub release $tag already exists. Increment the app version before publishing another OTA update."
}

$buildArguments = @{
    Runtime = $Runtime
    UpdateUrl = $repositoryUrl
    ReleaseNotesPath = $releaseNotes
}
if (-not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    $buildArguments.AzureTrustedSignFile = $AzureTrustedSignFile
}
if (-not [string]::IsNullOrWhiteSpace($SignParams)) {
    $buildArguments.SignParams = $SignParams
}
& (Join-Path $projectRoot 'build-release.ps1') @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'HoomNote release build failed.' }

$assets = Get-ChildItem -LiteralPath $releaseDirectory -File |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName
if ($assets.Count -eq 0) { throw 'No HoomNote release assets were generated.' }

$releaseArguments = @(
    'release', 'create', $tag,
    '--repo', $Repository,
    '--target', 'main',
    '--title', "HoomNote $version",
    '--notes-file', $releaseNotes
)
$releaseArguments += $assets
& gh @releaseArguments
if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed.' }

Write-Host "HoomNote OTA release published: https://github.com/$Repository/releases/tag/$tag"
