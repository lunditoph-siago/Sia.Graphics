[CmdletBinding()]
param(
  [ValidateSet("Browser", "Native", "All")]
  [string]$Target = "All",
  [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$projectDirectory = $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $repositoryRoot "artifacts\naga"
}

if ($Target -in @("Native", "All")) {
  & cargo test `
    --manifest-path (Join-Path $projectDirectory "Cargo.toml") `
    --release `
    --locked
  if ($LASTEXITCODE -ne 0) {
    throw "Testing the native Naga ABI failed."
  }

  & cargo build `
    --manifest-path (Join-Path $projectDirectory "Cargo.toml") `
    --release `
    --locked `
    --bin naga `
    --lib
  if ($LASTEXITCODE -ne 0) {
    throw "Building the native Naga bridge failed."
  }

  $nativeOutput = Join-Path $OutputDirectory "native"
  New-Item -ItemType Directory -Force -Path $nativeOutput | Out-Null
  $suffix = if ($IsWindows) { ".exe" } else { "" }
  $library = if ($IsWindows) { "sia_spirv_naga.dll" } else { "libsia_spirv_naga.so" }
  Copy-Item `
    -LiteralPath (Join-Path $projectDirectory "target\release\naga$suffix") `
    -Destination $nativeOutput `
    -Force
  Copy-Item `
    -LiteralPath (Join-Path $projectDirectory "target\release\$library") `
    -Destination $nativeOutput `
    -Force
  Copy-Item `
    -LiteralPath (Join-Path $projectDirectory "target\include\sia_spirv_naga.h") `
    -Destination $nativeOutput `
    -Force
}

if ($Target -in @("Browser", "All")) {
  $installedTargets = @(& rustup target list --installed)
  if ($installedTargets -notcontains "wasm32-unknown-unknown") {
    & rustup target add wasm32-unknown-unknown
    if ($LASTEXITCODE -ne 0) {
      throw "Installing the wasm32-unknown-unknown Rust target failed."
    }
  }

  & cargo build `
    --manifest-path (Join-Path $projectDirectory "Cargo.toml") `
    --target wasm32-unknown-unknown `
    --release `
    --locked `
    --lib
  if ($LASTEXITCODE -ne 0) {
    throw "Building the Naga Wasm bridge failed."
  }

  $browserOutput = Join-Path $OutputDirectory "browser"
  New-Item -ItemType Directory -Force -Path $browserOutput | Out-Null
  Copy-Item `
    -LiteralPath (Join-Path $projectDirectory "target\wasm32-unknown-unknown\release\sia_spirv_naga.wasm") `
    -Destination (Join-Path $browserOutput "sia-spirv-naga.wasm") `
    -Force
  Copy-Item `
    -LiteralPath (Join-Path $projectDirectory "sia-spirv-polyfill.js") `
    -Destination (Join-Path $browserOutput "sia-spirv-polyfill.js") `
    -Force
}
