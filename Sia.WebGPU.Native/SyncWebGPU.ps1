param(
    [string]$Tag,
    [string]$ArtifactsDirectory,
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $PSScriptRoot '../artifacts/webgpu' }
$headerPath = Join-Path $PSScriptRoot '../Sia.WebGPU.Generators/Resources/webgpu.h'
if (-not $Tag) {
    $recorded = if (Test-Path -LiteralPath $headerPath) { [regex]::Match([IO.File]::ReadAllText($headerPath), '#define WEBGPU_NATIVE_RELEASE_TAG "(v[0-9]{8}\.[0-9]{6})"') } else { $null }
    $Tag = if ($Refresh -or -not $recorded -or -not $recorded.Success) { 'latest' } else { $recorded.Groups[1].Value }
}
if ($Tag -ne 'latest' -and $Tag -notmatch '^v\d{8}\.\d{6}$') { throw "Invalid WebGPU native release tag: $Tag" }
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null
$metadataPath = Join-Path $ArtifactsDirectory "$Tag.json"
if ($Tag -eq 'latest' -or -not (Test-Path -LiteralPath $metadataPath)) {
    $endpoint = if ($Tag -eq 'latest') { 'latest' } else { "tags/$Tag" }
    $release = Invoke-RestMethod "https://api.github.com/repos/google/dawn/releases/$endpoint" -Headers @{ 'User-Agent' = 'Sia.WebGPU' }
    if ($release.tag_name -notmatch '^v\d{8}\.\d{6}$') { throw 'Unexpected WebGPU native release tag.' }
    $release | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $ArtifactsDirectory "$($release.tag_name).json") -Encoding UTF8
} else {
    $release = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
}
$Tag = $release.tag_name
$headers = @($release.assets | Where-Object { $_.name -match '^dawn-headers-([a-f0-9]{40})\.tar\.gz$' })
if ($headers.Count -ne 1) { throw 'Expected one WebGPU header archive.' }
$revision = [regex]::Match($headers[0].name, '[a-f0-9]{40}').Value
$releaseRoot = Join-Path $ArtifactsDirectory $Tag
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

function Get-NativeHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-NativeAsset($Asset) {
    if ($Asset.name -ne [IO.Path]::GetFileName($Asset.name) -or $Asset.digest -notmatch '^sha256:([a-f0-9]{64})$') { throw 'Invalid WebGPU asset metadata.' }
    $expectedHash = $Matches[1]
    $path = Join-Path $releaseRoot $Asset.name
    if (-not (Test-Path -LiteralPath $path) -or (Get-NativeHash $path) -ine $expectedHash) {
        Write-Host "Downloading $($Asset.name)"
        Invoke-WebRequest $Asset.browser_download_url -OutFile "$path.download" -UseBasicParsing
        if ((Get-NativeHash "$path.download") -ine $expectedHash) { throw "Checksum mismatch: $($Asset.name)" }
        Move-Item -LiteralPath "$path.download" -Destination $path -Force
    }
    return $path
}

if ($Refresh -or -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../Sia.WebGPU.Generators/Resources/webgpu.h'))) {
    $headerArchive = Get-NativeAsset $headers[0]
    $headerRoot = Join-Path $releaseRoot 'headers'
    New-Item -ItemType Directory -Path $headerRoot -Force | Out-Null
    & tar -xf $headerArchive -C $headerRoot
    if ($LASTEXITCODE -ne 0) { throw 'WebGPU header extraction failed.' }
    $header = @(Get-ChildItem -LiteralPath $headerRoot -Recurse -Filter webgpu.h | Where-Object { $_.Directory.Name -eq 'dawn' })
    if ($header.Count -ne 1) { throw 'Expected one WebGPU C header.' }
    $licensePath = Join-Path $releaseRoot 'LICENSE.webgpu'
    Invoke-WebRequest "https://raw.githubusercontent.com/google/dawn/$revision/LICENSE" -OutFile $licensePath -UseBasicParsing
    $version = [IO.File]::ReadAllText((Join-Path $header[0].DirectoryName 'dawn_version.h'))
    $text = [IO.File]::ReadAllText($header[0].FullName).Replace('#include "dawn/dawn_version.h"', $version)
    $text = "#define WEBGPU_NATIVE_RELEASE_TAG `"$Tag`"`n" + $text
    [IO.File]::WriteAllText($headerPath, $text)
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $PSScriptRoot 'LICENSE.webgpu')
}

[pscustomobject]@{ Tag = $Tag; Revision = $revision; Root = $releaseRoot; Release = $release }
