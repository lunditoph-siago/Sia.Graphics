# Packs and validates the packages that compose the spirv-tools workload.
[CmdletBinding()]
param(
  [string]$Configuration = "Release",
  [string]$OutputDirectory = "",
  [string]$DotNetPath = "",
  [string]$PackageVersion = "",
  [string]$SdkFeatureBand = "",
  [string[]]$HostRuntimeIdentifiers = @()
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $repositoryRoot "artifacts\packages"
}
if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
  $portableDotNet = Join-Path $repositoryRoot "..\.dotnet\dotnet.exe"
  $DotNetPath = if (Test-Path -LiteralPath $portableDotNet) {
    (Resolve-Path -LiteralPath $portableDotNet).Path
  }
  else {
    (Get-Command dotnet -ErrorAction Stop).Source
  }
}
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
  [xml]$packageProperties = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "Sia.Spirv.Packages.props")
  $PackageVersion = $packageProperties.Project.PropertyGroup.SiaSpirvPackageVersion.InnerText
}
if ([string]::IsNullOrWhiteSpace($SdkFeatureBand)) {
  $sdkVersion = (& $DotNetPath --version).Trim()
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to determine the .NET SDK version."
  }
  $featureBandMatch = [Regex]::Match(
    $sdkVersion, '^\d+\.\d+\.\d+(?:-[^.]+\.\d+)?')
  if (!$featureBandMatch.Success) {
    throw "Could not derive an SDK feature band from '$sdkVersion'."
  }
  $SdkFeatureBand = $featureBandMatch.Value
}
if ($HostRuntimeIdentifiers.Count -eq 0) {
  $HostRuntimeIdentifiers = if ($IsWindows) {
    @("win-x64")
  }
  elseif ($IsLinux) {
    @("linux-x64")
  }
  else {
    throw "Only Windows x64 and Linux x64 build hosts are supported."
  }
}

$requiredToolNames = @(
  "llc", "opt", "llvm-as", "llvm-dis", "spirv-as", "spirv-dis",
  "spirv-link", "spirv-opt", "spirv-val")
$requiredLicenses = @("LLVM.txt", "SPIRV-Tools.txt", "SPIRV-Headers.txt")
$toolchains = @{
  "win-x64" = @{
    Directory = "artifacts\llvm-toolchain"
    Extension = ".exe"
    Project = "Sia.Spirv.Toolchain.win-x64\Sia.Spirv.Toolchain.win-x64.csproj"
  }
  "linux-x64" = @{
    Directory = "artifacts\llvm-toolchain-linux-x64"
    Extension = ""
    Project = "Sia.Spirv.Toolchain.linux-x64\Sia.Spirv.Toolchain.linux-x64.csproj"
  }
}
foreach ($hostRid in $HostRuntimeIdentifiers) {
  if (!$toolchains.ContainsKey($hostRid)) {
    throw "Unsupported host RID '$hostRid'."
  }
  $toolchain = $toolchains[$hostRid]
  foreach ($toolName in $requiredToolNames) {
    $toolPath = Join-Path $repositoryRoot (
      "$($toolchain.Directory)\bin\$toolName$($toolchain.Extension)")
    if (!(Test-Path -LiteralPath $toolPath)) {
      throw "Required tool '$toolPath' was not found. Build the native toolchain first."
    }
  }
  foreach ($license in $requiredLicenses) {
    $licensePath = Join-Path $repositoryRoot (
      "$($toolchain.Directory)\licenses\$license")
    if (!(Test-Path -LiteralPath $licensePath)) {
      throw "Required license '$licensePath' was not found. Build the native toolchain first."
    }
  }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$projects = [Collections.Generic.List[string]]@(
  "Sia.Spirv.Core\Sia.Spirv.Core.csproj",
  "Sia.Spirv.Runtime\Sia.Spirv.Runtime.csproj"
)
foreach ($hostRid in $HostRuntimeIdentifiers) {
  $projects.Add($toolchains[$hostRid].Project)
}
$projects.Add("Sia.Spirv.Sdk\Sia.Spirv.Sdk.csproj")
$projects.Add("Sia.Spirv.Bootstrap\Sia.Spirv.Bootstrap.csproj")
$projects.Add("Sia.Spirv.Workload.Manifest\Sia.Spirv.Workload.Manifest.csproj")
foreach ($project in $projects) {
  & $DotNetPath pack (Join-Path $repositoryRoot $project) `
    --configuration $Configuration `
    --output $OutputDirectory `
    -p:SiaSpirvPackageVersion=$PackageVersion `
    -p:SiaSpirvSdkFeatureBand=$SdkFeatureBand `
    -p:UseSharedCompilation=false `
    -p:BuildInParallel=false
  if ($LASTEXITCODE -ne 0) {
    throw "Packing '$project' failed with exit code $LASTEXITCODE."
  }
}

if ("linux-x64" -in $HostRuntimeIdentifiers) {
  $linuxPackagePath = Join-Path $OutputDirectory (
    "Sia.Spirv.Toolchain.linux-x64.$PackageVersion.nupkg")
  $archive = [IO.Compression.ZipFile]::Open(
    $linuxPackagePath, [IO.Compression.ZipArchiveMode]::Update)
  try {
    foreach ($toolName in $requiredToolNames) {
      $entryName = "tools/linux-x64/$toolName"
      $entry = $archive.GetEntry($entryName)
      if ($null -eq $entry) {
        throw "The Linux toolchain package does not contain '$entryName'."
      }
      $entry.ExternalAttributes = -2115174400
    }
  }
  finally {
    $archive.Dispose()
  }
}

& (Join-Path $PSScriptRoot "VerifyPackages.ps1") `
  -PackageDirectory $OutputDirectory `
  -PackageVersion $PackageVersion `
  -SdkFeatureBand $SdkFeatureBand `
  -HostRuntimeIdentifiers $HostRuntimeIdentifiers

Write-Output "Packed the managed SDK and $($HostRuntimeIdentifiers -join ', ') host toolchains."
