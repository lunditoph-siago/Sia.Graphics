[CmdletBinding()]
param(
  [string]$InstallDirectory = "",
  [string]$Version = "30.0.0"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
  $InstallDirectory = Join-Path $repositoryRoot "artifacts\llvm-toolchain"
}

$cargoRoot = Join-Path $repositoryRoot "artifacts\naga-cli-win-x64"
$naga = Join-Path $cargoRoot "bin\naga.exe"
if (!(Test-Path -LiteralPath $naga) -or (& $naga --version) -ne $Version) {
  & cargo install naga-cli --version $Version --locked --force --root $cargoRoot
  if ($LASTEXITCODE -ne 0) {
    throw "Naga build failed with exit code $LASTEXITCODE."
  }
}

$installBin = Join-Path $InstallDirectory "bin"
$installLicenses = Join-Path $InstallDirectory "licenses"
New-Item -ItemType Directory -Force -Path $installBin, $installLicenses | Out-Null
Copy-Item -LiteralPath $naga `
  -Destination $installBin -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Naga.LICENSE.txt") `
  -Destination (Join-Path $installLicenses "Naga.txt") -Force

& (Join-Path $installBin "naga.exe") --version
if ($LASTEXITCODE -ne 0) {
  throw "The built naga executable could not be launched."
}
