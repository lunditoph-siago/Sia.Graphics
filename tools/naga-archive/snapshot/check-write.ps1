$ErrorActionPreference = 'Continue'
$taskRoot = $PSScriptRoot
$workspace = (Resolve-Path (Join-Path $taskRoot '../..')).Path
$oracle = Join-Path $taskRoot 'oracle-target/release/sia-naga-reference-oracle.exe'
$dotnet = Join-Path $workspace '.dotnet/dotnet.exe'
$harness = Join-Path $taskRoot 'harness/bin/Release/net10.0/harness.dll'
$output = Join-Path $taskRoot 'write-corpus'
New-Item -ItemType Directory -Force $output | Out-Null
$inputs = @('collatz', 'access', 'boids', 'control-flow', 'conversions', 'struct-layout', '7995-unicode-idents', '6220-break-from-loop')
foreach ($name in $inputs) {
  $inputFile = Join-Path $workspace ".reference/wgpu/naga/tests/in/wgsl/$name.wgsl"
  $spv = Join-Path $output "$name.managed.spv"
  $wgsl = Join-Path $output "$name.reference.wgsl"
  $managed = Join-Path $output "$name.roundtrip.wgsl"
  $errors = Join-Path $output "$name.log"
  & $dotnet $harness $inputFile $spv 2> $errors
  if ($LASTEXITCODE -ne 0) { Write-Output "MANAGED_REJECT $name"; continue }
  & $oracle $spv $wgsl 2>> $errors
  if ($LASTEXITCODE -ne 0) { Write-Output "SPIRV_INVALID $name"; Get-Content $errors -TotalCount 3; continue }
  & $dotnet $harness $spv $managed 2>> $errors
  if ($LASTEXITCODE -ne 0) { Write-Output "MANAGED_SPIRV_REJECT $name"; continue }
  & $oracle $managed 2>> $errors
  if ($LASTEXITCODE -ne 0) { Write-Output "ROUNDTRIP_INVALID $name"; Get-Content $errors -TotalCount 3; continue }
  Write-Output "PASS $name"
}
