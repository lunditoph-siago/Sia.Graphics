param(
    [Parameter(Mandatory = $true)][string]$RuntimeIdentifier,
    [string]$Dotnet = 'dotnet',
    [string]$Toolchain,
    [string]$OutputDirectory = 'artifacts/native-task-tests'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$taskSource = Join-Path $PSScriptRoot 'NativeBuildTask.cs'
$windows = $RuntimeIdentifier.StartsWith('win-')
$library = Join-Path $root $(if ($windows) { 'fixture.dll' } elseif ($RuntimeIdentifier.StartsWith('osx-')) { 'fixture.dylib' } else { 'fixture.so' })
[IO.File]::WriteAllText((Join-Path $root 'value.h'), "#define VALUE 7`n")
[IO.File]::WriteAllText((Join-Path $root 'value.cpp'), "#include `"value.h`"`n#ifndef FLAG`n#define FLAG 0`n#endif`nextern `"C`" int native_value() { return VALUE + FLAG; }`n")
[IO.File]::WriteAllText((Join-Path $root 'other.cpp'), "extern `"C`" int other_value() { return 19; }`n")
[IO.File]::WriteAllText((Join-Path $root 'fixture.def'), "EXPORTS`nnative_value`nother_value`n")
$project = @"
<Project DefaultTargets="Build">
  <UsingTask TaskName="NativeBuildTask" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="`$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
    <Task><Code Type="Class" Language="cs" Source="$([Security.SecurityElement]::Escape($taskSource))" /></Task>
  </UsingTask>
  <ItemGroup><Source Include="$root/value.cpp;$root/other.cpp" /><Option Include="`$(TestFlag)" Condition="'`$(TestFlag)' != ''" /></ItemGroup>
  <Target Name="Build">
    <NativeBuildTask Sources="@(Source)" RuntimeIdentifier="$RuntimeIdentifier" OutputFile="$library" IntermediateDirectory="$root/objects" Toolchain="$Toolchain" DefinitionFile="$root/fixture.def" CompileOptions="@(Option)" MaxParallelism="2">
      <Output TaskParameter="CompiledSources" PropertyName="Compiled" /><Output TaskParameter="Linked" PropertyName="Linked" />
    </NativeBuildTask>
    <WriteLinesToFile File="$root/result.txt" Lines="`$(Compiled);`$(Linked)" Overwrite="true" />
  </Target>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $root 'fixture.proj'), $project)
$results = @()
function Build-Fixture($Name, $ExpectedCompiled, $ExpectedLinked, $Flag = '') {
    $log = Join-Path $root "$Name.log"
    & $Dotnet msbuild (Join-Path $root 'fixture.proj') "-p:TestFlag=$Flag" -v:minimal *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log; throw "Fixture failed: $Name" }
    $values = @(Get-Content -LiteralPath (Join-Path $root 'result.txt'))
    if ($values[0] -ne "$ExpectedCompiled" -or $values[1] -ine "$ExpectedLinked") { throw "Unexpected incremental result for $Name`: $values" }
    $script:results += [pscustomobject]@{ Name=$Name; Compiled=$values[0]; Linked=$values[1] }
}
Build-Fixture 'initial-parallel' 2 'True'
Build-Fixture 'unchanged' 0 'False'
Start-Sleep -Milliseconds 1100
[IO.File]::WriteAllText((Join-Path $root 'value.h'), "#define VALUE 11`n")
Build-Fixture 'header-change' 1 'True'
$flag = if ($windows) { '/DFLAG=4' } else { '-DFLAG=4' }
Build-Fixture 'option-change' 2 'True' $flag
[IO.File]::WriteAllText((Join-Path $root 'unrelated.txt'), 'unrelated')
Build-Fixture 'unrelated-file' 0 'False' $flag
$sourcePath = Join-Path $root 'value.cpp'
$validSource = [IO.File]::ReadAllText($sourcePath)
[IO.File]::WriteAllText($sourcePath, $validSource + "`n#error deliberate_native_test_failure`n")
& $Dotnet msbuild (Join-Path $root 'fixture.proj') "-p:TestFlag=$flag" -v:quiet *> (Join-Path $root 'expected-failure.log')
if ($LASTEXITCODE -eq 0) { throw 'Invalid C++ source must fail.' }
[IO.File]::WriteAllText($sourcePath, $validSource)
Build-Fixture 'recovery-after-failure' 1 'True' $flag
$probe = "import ctypes,sys; lib=ctypes.CDLL(sys.argv[1]); assert lib.native_value()==15; assert lib.other_value()==19; print('native calls PASS')"
& python -c $probe $library
if ($LASTEXITCODE -ne 0) { throw 'Fixture native call failed.' }
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'results.json')
$results | Format-Table
