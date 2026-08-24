[CmdletBinding()]
param(
  [string]$DotNetRoot = "",
  [string]$ManifestVersion = "0.1.0-preview.1"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DotNetRoot)) {
  $DotNetRoot = (Resolve-Path (Join-Path $repositoryRoot "..\.dotnet")).Path
}
else {
  $DotNetRoot = (Resolve-Path -LiteralPath $DotNetRoot).Path
}

$dotnet = Join-Path $DotNetRoot "dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet)) {
  throw "dotnet.exe was not found in '$DotNetRoot'."
}
$sdkVersion = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) {
  throw "Failed to determine the .NET SDK version."
}
$match = [Regex]::Match($sdkVersion, '^\d+\.\d+\.\d+(?:-[^.]+\.\d+)?')
if (!$match.Success) {
  throw "Could not derive an SDK feature band from '$sdkVersion'."
}

$featureBand = $match.Value
$manifestSourceDirectory = Join-Path $repositoryRoot "Sia.Spirv.Workload.Manifest"
$manifestDirectory = Join-Path $DotNetRoot (
  "sdk-manifests\$featureBand\sia.spirv.workload\$ManifestVersion")
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
foreach ($manifestFile in @("WorkloadManifest.json", "WorkloadManifest.targets")) {
  Copy-Item -LiteralPath (Join-Path $manifestSourceDirectory $manifestFile) `
    -Destination (Join-Path $manifestDirectory $manifestFile) `
    -Force
}

Write-Output "Installed the spirv-tools baseline manifest for SDK feature band $featureBand."
Write-Output "Run: dotnet workload install spirv-tools --source <package-source> --skip-manifest-update"
