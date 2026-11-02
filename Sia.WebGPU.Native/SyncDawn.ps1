param(
    [string]$Tag,
    [string]$ArtifactsDirectory,
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $PSScriptRoot '../artifacts/dawn' }
$manifestPath = Join-Path $PSScriptRoot 'Dawn.json'
if (-not $Tag) {
    $Tag = if ($Refresh -or -not (Test-Path -LiteralPath $manifestPath)) { 'latest' }
           else { (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).Tag }
}
if ($Tag -ne 'latest' -and $Tag -notmatch '^v\d{8}\.\d{6}$') { throw "Invalid Dawn release tag: $Tag" }
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null
$metadataPath = Join-Path $ArtifactsDirectory "$Tag.json"
if ($Tag -eq 'latest' -or -not (Test-Path -LiteralPath $metadataPath)) {
    $endpoint = if ($Tag -eq 'latest') { 'latest' } else { "tags/$Tag" }
    $release = Invoke-RestMethod "https://api.github.com/repos/google/dawn/releases/$endpoint" -Headers @{ 'User-Agent' = 'Sia.WebGPU' }
    if ($release.tag_name -notmatch '^v\d{8}\.\d{6}$') { throw 'Unexpected Dawn release tag.' }
    $release | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $ArtifactsDirectory "$($release.tag_name).json") -Encoding UTF8
} else {
    $release = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
}
$Tag = $release.tag_name
$headers = @($release.assets | Where-Object { $_.name -match '^dawn-headers-([a-f0-9]{40})\.tar\.gz$' })
if ($headers.Count -ne 1) { throw 'Expected one Dawn header archive.' }
$revision = [regex]::Match($headers[0].name, '[a-f0-9]{40}').Value
$releaseRoot = Join-Path $ArtifactsDirectory $Tag
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

function Get-DawnHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-DawnAsset($Asset) {
    if ($Asset.name -ne [IO.Path]::GetFileName($Asset.name) -or $Asset.digest -notmatch '^sha256:([a-f0-9]{64})$') { throw 'Invalid Dawn asset metadata.' }
    $expectedHash = $Matches[1]
    $path = Join-Path $releaseRoot $Asset.name
    if (-not (Test-Path -LiteralPath $path) -or (Get-DawnHash $path) -ine $expectedHash) {
        Write-Host "Downloading $($Asset.name)"
        Invoke-WebRequest $Asset.browser_download_url -OutFile "$path.download" -UseBasicParsing
        if ((Get-DawnHash "$path.download") -ine $expectedHash) { throw "Checksum mismatch: $($Asset.name)" }
        Move-Item -LiteralPath "$path.download" -Destination $path -Force
    }
    return $path
}

if ($Refresh -or -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../Sia.WebGPU.Generators/Resources/webgpu.h'))) {
    $headerArchive = Get-DawnAsset $headers[0]
    $headerRoot = Join-Path $releaseRoot 'headers'
    New-Item -ItemType Directory -Path $headerRoot -Force | Out-Null
    & tar -xf $headerArchive -C $headerRoot
    if ($LASTEXITCODE -ne 0) { throw 'Dawn header extraction failed.' }
    $header = @(Get-ChildItem -LiteralPath $headerRoot -Recurse -Filter webgpu.h | Where-Object { $_.Directory.Name -eq 'dawn' })
    if ($header.Count -ne 1) { throw 'Expected one Dawn C header.' }
    $port = @($release.assets | Where-Object { $_.name -eq "emdawnwebgpu-$Tag.remoteport.py" })
    if ($port.Count -ne 1) { throw 'Expected one matching Emdawn remote port.' }
    $portPath = Get-DawnAsset $port[0]
    $licensePath = Join-Path $releaseRoot 'LICENSE.dawn'
    Invoke-WebRequest "https://raw.githubusercontent.com/google/dawn/$revision/LICENSE" -OutFile $licensePath -UseBasicParsing
    Copy-Item -LiteralPath $header[0].FullName -Destination (Join-Path $PSScriptRoot '../Sia.WebGPU.Generators/Resources/webgpu.h')
    Copy-Item -LiteralPath (Join-Path $header[0].DirectoryName 'dawn_version.h') -Destination (Join-Path $PSScriptRoot '../Sia.WebGPU.Generators/Resources/dawn_version.h')
    Copy-Item -LiteralPath $portPath -Destination (Join-Path $PSScriptRoot '../Sia.WebGPU/emdawnwebgpu.remoteport.py')
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $PSScriptRoot 'LICENSE.dawn')
    @{ Tag = $Tag; Revision = $revision } | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

[pscustomobject]@{ Tag = $Tag; Revision = $revision; Root = $releaseRoot; Release = $release }
