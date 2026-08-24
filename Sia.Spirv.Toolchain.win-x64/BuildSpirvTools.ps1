[CmdletBinding()]
param(
  [string]$SourceDirectory = "",
  [string]$BuildDirectory = "",
  [string]$InstallDirectory = ""
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
  $SourceDirectory = Join-Path $repositoryRoot "artifacts\spirv-tools"
}
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
  $BuildDirectory = Join-Path $repositoryRoot "artifacts\spirv-tools-build"
}
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
  $InstallDirectory = Join-Path $repositoryRoot "artifacts\llvm-toolchain"
}

$headersDirectory = Join-Path $SourceDirectory "external\spirv-headers"
if (!(Test-Path -LiteralPath (Join-Path $SourceDirectory "CMakeLists.txt"))) {
  throw "SPIRV-Tools source was not found at '$SourceDirectory'."
}
if (!(Test-Path -LiteralPath (Join-Path $headersDirectory "include\spirv\unified1\spirv.core.grammar.json"))) {
  throw "The pinned SPIRV-Headers source was not found at '$headersDirectory'."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path -LiteralPath $vswhere)) {
  throw "Visual Studio Installer's vswhere.exe was not found."
}
$visualStudio = & $vswhere -latest -products * -property installationPath
if ([string]::IsNullOrWhiteSpace($visualStudio)) {
  throw "A Visual Studio installation was not found."
}
$vsDevCmd = Join-Path $visualStudio "Common7\Tools\VsDevCmd.bat"
$ninja = Join-Path $visualStudio "Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
if (!(Test-Path -LiteralPath $vsDevCmd) -or !(Test-Path -LiteralPath $ninja)) {
  throw "The Visual Studio C++ environment or bundled Ninja executable was not found."
}

$environmentLines = & cmd.exe /s /c "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set"
foreach ($line in $environmentLines) {
  $separator = $line.IndexOf("=")
  if ($separator -gt 0) {
    [Environment]::SetEnvironmentVariable(
      $line.Substring(0, $separator),
      $line.Substring($separator + 1),
      "Process")
  }
}
$developerPath = $environmentLines |
  Where-Object {
    $_.StartsWith("PATH=", [StringComparison]::OrdinalIgnoreCase) -and
    $_.Contains("\VC\Tools\MSVC\", [StringComparison]::OrdinalIgnoreCase)
  } |
  Select-Object -First 1
if ($null -eq $developerPath) {
  throw "The Visual Studio developer environment did not provide PATH."
}
$env:Path = $developerPath.Substring(5)

$configureArguments = @(
  "--fresh",
  "-S", $SourceDirectory,
  "-B", $BuildDirectory,
  "-G", "Ninja",
  "-DCMAKE_MAKE_PROGRAM=$ninja",
  "-DCMAKE_BUILD_TYPE=Release",
  "-DCMAKE_INSTALL_PREFIX=$InstallDirectory",
  "-DSPIRV_SKIP_TESTS=ON",
  "-DSPIRV_WERROR=OFF",
  "-DSPIRV_TOOLS_BUILD_STATIC=ON",
  "-DBUILD_SHARED_LIBS=OFF"
)

& cmake @configureArguments
if ($LASTEXITCODE -ne 0) {
  throw "SPIRV-Tools CMake configuration failed with exit code $LASTEXITCODE."
}

$targets = @("spirv-as", "spirv-dis", "spirv-link", "spirv-opt", "spirv-val")
& cmake --build $BuildDirectory --target @targets --config Release
if ($LASTEXITCODE -ne 0) {
  throw "SPIRV-Tools build failed with exit code $LASTEXITCODE."
}

$installBin = Join-Path $InstallDirectory "bin"
New-Item -ItemType Directory -Force -Path $installBin | Out-Null
foreach ($tool in $targets) {
  Copy-Item -LiteralPath (Join-Path $BuildDirectory "tools\$tool.exe") -Destination $installBin -Force
}

$installLicenses = Join-Path $InstallDirectory "licenses"
New-Item -ItemType Directory -Force -Path $installLicenses | Out-Null
Copy-Item -LiteralPath (Join-Path $SourceDirectory "LICENSE") `
  -Destination (Join-Path $installLicenses "SPIRV-Tools.txt") `
  -Force
Copy-Item -LiteralPath (Join-Path $headersDirectory "LICENSE") `
  -Destination (Join-Path $installLicenses "SPIRV-Headers.txt") `
  -Force

& (Join-Path $installBin "spirv-val.exe") --version
if ($LASTEXITCODE -ne 0) {
  throw "The built spirv-val executable could not be launched."
}
