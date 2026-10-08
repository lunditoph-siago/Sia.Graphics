$ErrorActionPreference = 'Continue'
$taskRoot = $PSScriptRoot
$workspace = (Resolve-Path (Join-Path $taskRoot '../..')).Path
$oracle = Join-Path $taskRoot 'oracle-target/release/sia-naga-reference-oracle.exe'
$dotnet = Join-Path $workspace '.dotnet/dotnet.exe'
$harness = Join-Path $taskRoot 'harness/bin/Release/net10.0/harness.dll'
$output = Join-Path $taskRoot 'wgsl-corpus'
New-Item -ItemType Directory -Force $output | Out-Null
$inputs = @('collatz', 'access', 'boids', 'control-flow', 'operators', 'conversions', 'struct-layout', '7995-unicode-idents', '6220-break-from-loop')
foreach ($name in $inputs) {
  $inputFile = Join-Path $workspace ".reference/wgpu/naga/tests/in/wgsl/$name.wgsl"
  if (!(Test-Path -LiteralPath $inputFile)) { continue }
  $wgsl = Join-Path $output "$name.managed.wgsl"
  $errors = Join-Path $output "$name.log"
  & $dotnet $harness $inputFile $wgsl 2> $errors
  if ($LASTEXITCODE -ne 0) { Write-Output "MANAGED_REJECT $name"; continue }
  & $oracle $wgsl 2>> $errors
  if ($LASTEXITCODE -ne 0) { Write-Output "OUTPUT_INVALID $name"; Get-Content $errors -TotalCount 3; continue }
  Write-Output "PASS $name"
}
