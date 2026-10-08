[CmdletBinding()]
param(
  [string]$InstallDirectory = ""
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
  $InstallDirectory = Join-Path $repositoryRoot "artifacts\llvm-toolchain"
}

$projectDirectory = Join-Path $repositoryRoot "Sia.Spirv.Naga"
& cargo test `
  --manifest-path (Join-Path $projectDirectory "Cargo.toml") `
  --release `
  --locked
if ($LASTEXITCODE -ne 0) {
  throw "Naga ABI tests failed with exit code $LASTEXITCODE."
}
& cargo build `
  --manifest-path (Join-Path $projectDirectory "Cargo.toml") `
  --release `
  --locked `
  --bin naga `
  --lib
if ($LASTEXITCODE -ne 0) {
  throw "Naga build failed with exit code $LASTEXITCODE."
}

$nativeOutput = Join-Path $projectDirectory "target\release"
$installBin = Join-Path $InstallDirectory "bin"
$installInclude = Join-Path $InstallDirectory "include"
$installLicenses = Join-Path $InstallDirectory "licenses"
New-Item -ItemType Directory -Force -Path $installBin, $installInclude, $installLicenses | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeOutput "naga.exe") `
  -Destination $installBin -Force
Copy-Item -LiteralPath (Join-Path $nativeOutput "sia_spirv_naga.dll") `
  -Destination $installBin -Force
Copy-Item -LiteralPath (Join-Path $projectDirectory "target\include\sia_spirv_naga.h") `
  -Destination $installInclude -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Naga.LICENSE.txt") `
  -Destination (Join-Path $installLicenses "Naga.txt") -Force

& (Join-Path $installBin "naga.exe") --version
if ($LASTEXITCODE -ne 0) {
  throw "The built naga executable could not be launched."
}
