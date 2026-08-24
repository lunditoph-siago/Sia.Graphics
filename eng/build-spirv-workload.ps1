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

$requiredTools = @("llc.exe", "opt.exe", "spirv-val.exe")
foreach ($tool in $requiredTools) {
  $toolPath = Join-Path $repositoryRoot "artifacts\llvm-toolchain\bin\$tool"
  if (!(Test-Path -LiteralPath $toolPath)) {
    throw "Required tool '$toolPath' was not found. Build the native toolchain first."
  }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$projects = @(
  "Sia.Spirv.Core\Sia.Spirv.Core.csproj",
  "Sia.Spirv.Runtime\Sia.Spirv.Runtime.csproj",
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
