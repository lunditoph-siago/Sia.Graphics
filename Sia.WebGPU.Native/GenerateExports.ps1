param([Parameter(Mandatory = $true)][string]$Header, [Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$symbols = [regex]::Matches([IO.File]::ReadAllText($Header), '(?m)^WGPU_EXPORT[^;\r\n]*\b(wgpu[A-Z]\w*)\(') | ForEach-Object { $_.Groups[1].Value }
if ($symbols.Count -lt 200) { throw 'Incomplete WebGPU C API export list.' }
$exports = @('EXPORTS') + $symbols
$definition = ($exports -join "`n") + "`n"
$path = Join-Path $OutputDirectory 'Native.def'
if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne $definition) { [IO.File]::WriteAllText($path, $definition) }
$source = "#include <dawn/webgpu.h>`nextern `"C`" {`nvoid (*sia_webgpu_exports[])() = {`n" + (($symbols | ForEach-Object { "reinterpret_cast<void(*)()>(&$_)," }) -join "`n") + "`n};`n}`n"
$path = Join-Path $OutputDirectory 'NativeExports.cpp'
if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne $source) { [IO.File]::WriteAllText($path, $source) }
