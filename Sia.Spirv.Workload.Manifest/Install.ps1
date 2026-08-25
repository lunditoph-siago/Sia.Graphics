# Installs a packed baseline manifest into a specific portable .NET SDK.
[CmdletBinding()]
param(
  [string]$DotNetRoot = "",
  [string]$PackageDirectory = "",
  [Parameter(Mandatory)]
  [string]$ManifestVersion
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DotNetRoot)) {
  $DotNetRoot = (Resolve-Path (Join-Path $repositoryRoot "..\.dotnet")).Path
}
else {
  $DotNetRoot = (Resolve-Path -LiteralPath $DotNetRoot).Path
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
  $PackageDirectory = Join-Path $repositoryRoot "artifacts\packages"
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$dotnetName = if ($IsWindows) { "dotnet.exe" } else { "dotnet" }
$dotnet = Join-Path $DotNetRoot $dotnetName
if (!(Test-Path -LiteralPath $dotnet)) {
  throw "dotnet was not found in '$DotNetRoot'."
}
$sdkVersion = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) {
  throw "Failed to determine the .NET SDK version."
}
$featureBandMatch = [Regex]::Match(
  $sdkVersion, '^\d+\.\d+\.\d+(?:-[^.]+\.\d+)?')
if (!$featureBandMatch.Success) {
  throw "Could not derive an SDK feature band from '$sdkVersion'."
}

$featureBand = $featureBandMatch.Value
$manifestPackage = Join-Path $PackageDirectory (
  "Sia.Spirv.Workload.Manifest-$featureBand.$ManifestVersion.nupkg")
if (!(Test-Path -LiteralPath $manifestPackage)) {
  throw "The matching workload manifest package '$manifestPackage' was not found."
}
$manifestDirectory = Join-Path $DotNetRoot (
  "sdk-manifests\$featureBand\sia.spirv.workload\$ManifestVersion")
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null

$archive = [IO.Compression.ZipFile]::OpenRead($manifestPackage)
try {
  foreach ($manifestFile in @("WorkloadManifest.json", "WorkloadManifest.targets")) {
    $entry = $archive.GetEntry("data/$manifestFile")
    if ($null -eq $entry) {
      throw "The manifest package does not contain 'data/$manifestFile'."
    }
    $destination = Join-Path $manifestDirectory $manifestFile
    $sourceStream = $entry.Open()
    $destinationStream = [IO.File]::Create($destination)
    try {
      $sourceStream.CopyTo($destinationStream)
    }
    finally {
      $destinationStream.Dispose()
      $sourceStream.Dispose()
    }
  }
}
finally {
  $archive.Dispose()
}

Write-Output "Installed the spirv-tools baseline manifest for SDK feature band $featureBand."
Write-Output "Run: dotnet workload install spirv-tools --source '$PackageDirectory' --skip-manifest-update"
