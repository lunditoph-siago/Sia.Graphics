param([Parameter(Mandatory = $true)][string]$Header, [Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$symbols = [regex]::Matches([IO.File]::ReadAllText($Header), '(?m)^WGPU_EXPORT[^;\r\n]*\b(wgpu[A-Z]\w*)\(') | ForEach-Object { $_.Groups[1].Value }
if ($symbols.Count -lt 200) { throw 'Incomplete Dawn C API export list.' }
$exports = @('EXPORTS') + @($symbols | ForEach-Object {
    switch ($_) {
        'wgpuCreateInstance' { 'wgpuCreateInstance=siaDawnCreateInstance' }
        'wgpuGetProcAddress' { 'wgpuGetProcAddress=siaDawnGetProcAddress' }
        default { $_ }
    }
})
$definition = ($exports -join "`n") + "`n"
$path = Join-Path $OutputDirectory 'Dawn.def'
if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne $definition) { [IO.File]::WriteAllText($path, $definition) }
$source = "#include <dawn/webgpu.h>`nextern `"C`" {`nvoid (*sia_dawn_exports[])() = {`n" + (($symbols | ForEach-Object { "reinterpret_cast<void(*)()>(&$_)," }) -join "`n") + "`n};`n}`n"
$path = Join-Path $OutputDirectory 'DawnExports.cpp'
if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne $source) { [IO.File]::WriteAllText($path, $source) }
