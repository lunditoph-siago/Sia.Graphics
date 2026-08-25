[CmdletBinding()]
param(
  [string]$SourceDirectory = "",
  [string]$BuildDirectory = "",
  [string]$InstallDirectory = "",
  [int]$BuildJobs = 2,
  [switch]$SkipConfigure
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
  $SourceDirectory = Join-Path $repositoryRoot "artifacts\dotnet-llvm-project"
}
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
  $BuildDirectory = Join-Path $repositoryRoot "artifacts\llvm-build"
}
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
  $InstallDirectory = Join-Path $repositoryRoot "artifacts\llvm-toolchain"
}

$llvmSource = Join-Path $SourceDirectory "llvm"
$spirvSource = Join-Path $llvmSource "lib\Target\SPIRV"
if (!(Test-Path -LiteralPath $spirvSource)) {
  throw "The LLVM source at '$SourceDirectory' does not contain the SPIR-V target."
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
  "-S", $llvmSource,
  "-B", $BuildDirectory,
  "-G", "Ninja",
  "-DCMAKE_MAKE_PROGRAM=$ninja",
  "-DCMAKE_BUILD_TYPE=Release",
  "-DCMAKE_INSTALL_PREFIX=$InstallDirectory",
  "-DLLVM_TARGETS_TO_BUILD=X86;SPIRV",
  "-DLLVM_BUILD_TOOLS=ON",
  "-DLLVM_INCLUDE_TESTS=OFF",
  "-DLLVM_INCLUDE_BENCHMARKS=OFF",
  "-DLLVM_INCLUDE_EXAMPLES=OFF",
  "-DLLVM_ENABLE_BINDINGS=OFF",
  "-DLLVM_ENABLE_DIA_SDK=OFF",
  "-DLLVM_ENABLE_TERMINFO=OFF",
  "-DLLVM_ENABLE_ZLIB=OFF",
  "-DLLVM_ENABLE_ZSTD=OFF",
  "-DLLVM_ENABLE_LIBXML2=OFF",
  "-DLLVM_ENABLE_CURL=OFF"
)

if (!$SkipConfigure) {
  & cmake @configureArguments
  if ($LASTEXITCODE -ne 0) {
    throw "LLVM CMake configuration failed with exit code $LASTEXITCODE."
  }
}
elseif (!(Test-Path -LiteralPath (Join-Path $BuildDirectory "build.ninja"))) {
  throw "The cached LLVM build tree '$BuildDirectory' is not configured."
}

& cmake --build $BuildDirectory --target llc opt llvm-as llvm-dis `
  --config Release --parallel $BuildJobs
if ($LASTEXITCODE -ne 0) {
  throw "LLVM tool build failed with exit code $LASTEXITCODE."
}

$installBin = Join-Path $InstallDirectory "bin"
New-Item -ItemType Directory -Force -Path $installBin | Out-Null
foreach ($tool in @("llc.exe", "opt.exe", "llvm-as.exe", "llvm-dis.exe")) {
  Copy-Item -LiteralPath (Join-Path $BuildDirectory "bin\$tool") -Destination $installBin -Force
}

$installLicenses = Join-Path $InstallDirectory "licenses"
New-Item -ItemType Directory -Force -Path $installLicenses | Out-Null
Copy-Item -LiteralPath (Join-Path $llvmSource "LICENSE.TXT") `
  -Destination (Join-Path $installLicenses "LLVM.txt") `
  -Force

& (Join-Path $installBin "llc.exe") --version
if ($LASTEXITCODE -ne 0) {
  throw "The built llc executable could not be launched."
}
