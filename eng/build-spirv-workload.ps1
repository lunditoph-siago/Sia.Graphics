[CmdletBinding()]
param(
  [string]$Configuration = "Release",
  [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dotnet = (Resolve-Path (Join-Path $repositoryRoot "..\.dotnet\dotnet.exe")).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $repositoryRoot "artifacts\packages"
}

$requiredTools = @(
  "llc.exe",
  "opt.exe",
  "llvm-as.exe",
  "llvm-dis.exe",
  "spirv-as.exe",
  "spirv-dis.exe",
  "spirv-link.exe",
  "spirv-opt.exe",
  "spirv-val.exe"
)
foreach ($tool in $requiredTools) {
  $toolPath = Join-Path $repositoryRoot "artifacts\llvm-toolchain\bin\$tool"
  if (!(Test-Path -LiteralPath $toolPath)) {
    throw "Required tool '$toolPath' was not found. Build the native toolchain first."
  }
}
$requiredLicenses = @("LLVM.txt", "SPIRV-Tools.txt", "SPIRV-Headers.txt")
foreach ($license in $requiredLicenses) {
  $licensePath = Join-Path $repositoryRoot "artifacts\llvm-toolchain\licenses\$license"
  if (!(Test-Path -LiteralPath $licensePath)) {
    throw "Required license '$licensePath' was not found. Build the native toolchain first."
  }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$projects = @(
  "Sia.Spirv.Core\Sia.Spirv.Core.csproj",
  "Sia.Spirv.Runtime\Sia.Spirv.Runtime.csproj",
  "Sia.Spirv.Toolchain.win-x64\Sia.Spirv.Toolchain.win-x64.csproj",
  "Sia.Spirv.Sdk\Sia.Spirv.Sdk.csproj",
  "Sia.Spirv.Workload.Manifest\Sia.Spirv.Workload.Manifest.csproj"
)
foreach ($project in $projects) {
  & $dotnet pack (Join-Path $repositoryRoot $project) `
    --configuration $Configuration `
    --output $OutputDirectory `
    -p:UseSharedCompilation=false `
    -p:BuildInParallel=false
  if ($LASTEXITCODE -ne 0) {
    throw "Packing '$project' failed with exit code $LASTEXITCODE."
  }
}

function Get-PackageEntries([string]$packagePath) {
  $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
  try {
    return @($archive.Entries | ForEach-Object FullName)
  }
  finally {
    $archive.Dispose()
  }
}

$packageVersion = "0.1.0-preview.1"
$sdkPackage = Join-Path $OutputDirectory "Sia.Spirv.Sdk.$packageVersion.nupkg"
$toolchainPackage = Join-Path $OutputDirectory (
  "Sia.Spirv.Toolchain.win-x64.$packageVersion.nupkg")
$sdkEntries = Get-PackageEntries $sdkPackage
$toolchainEntries = Get-PackageEntries $toolchainPackage
if ($sdkEntries | Where-Object { $_.StartsWith("tools/win-x64/", [StringComparison]::OrdinalIgnoreCase) }) {
  throw "The managed SDK package unexpectedly contains the native host toolchain."
}
foreach ($tool in $requiredTools) {
  if (!("tools/win-x64/$tool" -in $toolchainEntries)) {
    throw "The host toolchain package does not contain '$tool'."
  }
}
foreach ($license in $requiredLicenses) {
  if (!("licenses/$license" -in $toolchainEntries)) {
    throw "The host toolchain package does not contain '$license'."
  }
}

Write-Output "Verified split managed SDK and Windows x64 host-toolchain packages."
