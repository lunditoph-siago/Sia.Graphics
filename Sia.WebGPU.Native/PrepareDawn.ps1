param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x86','win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$RuntimeIdentifier,
    [string]$ArtifactsDirectory,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $PSScriptRoot '../artifacts/dawn' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'runtimes' }
$dawn = . (Join-Path $PSScriptRoot 'SyncDawn.ps1') -ArtifactsDirectory $ArtifactsDirectory
$out = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$RuntimeIdentifier/native"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$build = Join-Path $dawn.Root "build-$RuntimeIdentifier"
$platforms = @{
    'win-x64' = 'windows-latest'
    'linux-x64' = 'ubuntu-latest'
    'osx-x64' = 'macos-15-intel'
    'osx-arm64' = 'macos-latest'
}
$configure = @('-B', $build, '-DCMAKE_BUILD_TYPE=Release')
if ($RuntimeIdentifier.StartsWith('win-')) {
    $arch = switch ($RuntimeIdentifier) { 'win-x86' { 'Win32' } 'win-arm64' { 'ARM64' } default { 'x64' } }
    $configure += @('-A', $arch)
} elseif ($RuntimeIdentifier.StartsWith('osx-')) {
    $arch = if ($RuntimeIdentifier -eq 'osx-x64') { 'x86_64' } else { 'arm64' }
    $configure += "-DCMAKE_OSX_ARCHITECTURES=$arch"
}

$source = Join-Path $dawn.Root 'source'
if ($RuntimeIdentifier.StartsWith('win-') -or -not $platforms.ContainsKey($RuntimeIdentifier)) {
    if (-not (Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt'))) {
        $archive = Join-Path $dawn.Root 'source.tar.gz'
        Invoke-WebRequest "https://github.com/google/dawn/archive/$($dawn.Revision).tar.gz" -OutFile $archive -UseBasicParsing
        New-Item -ItemType Directory -Path $source -Force | Out-Null
        & tar -xf $archive -C $source --strip-components=1
        if ($LASTEXITCODE -ne 0) { throw 'Failed to extract Dawn sources.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $source '.git/HEAD'))) {
        & git -C $source init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the Dawn source version metadata.' }
        & git -C $source fetch --depth=1 https://github.com/google/dawn.git $dawn.Revision
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the Dawn source revision.' }
        & git -C $source update-ref HEAD $dawn.Revision
        if ($LASTEXITCODE -ne 0) { throw 'Cannot record the Dawn source revision.' }
    }
}

if ($platforms.ContainsKey($RuntimeIdentifier)) {
    $name = "Dawn-$($dawn.Revision)-$($platforms[$RuntimeIdentifier])-Release.tar.gz"
    $asset = @($dawn.Release.assets | Where-Object { $_.name -eq $name })
    if ($asset.Count -ne 1) { throw "Dawn release missing $name" }
    $archive = Get-DawnAsset $asset[0]
    $prefix = Join-Path $dawn.Root "native-$RuntimeIdentifier"
    New-Item -ItemType Directory -Path $prefix -Force | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $prefix 'include/dawn/webgpu.h'))) {
        & tar -xf $archive -C $prefix --strip-components=1
        if ($LASTEXITCODE -ne 0) { throw "Failed to extract $name" }
    }
} else {
    $configure += @('-S', $source,
        '-DDAWN_FETCH_DEPENDENCIES=ON', '-DDAWN_BUILD_MONOLITHIC_LIBRARY=STATIC',
        '-DDAWN_BUILD_TESTS=OFF', '-DDAWN_BUILD_SAMPLES=OFF', '-DDAWN_USE_GLFW=OFF',
        '-DTINT_BUILD_TESTS=OFF', '-DTINT_BUILD_CMD_TOOLS=OFF', '-DDAWN_ENABLE_INSTALL=ON')
}
if (-not $platforms.ContainsKey($RuntimeIdentifier)) {
    & cmake @configure
    if ($LASTEXITCODE -ne 0) { throw "Dawn configure failed for $RuntimeIdentifier" }
    & cmake --build $build --config Release --target webgpu_dawn --parallel 2
    if ($LASTEXITCODE -ne 0) { throw "Dawn build failed for $RuntimeIdentifier" }
    $prefix = Join-Path $dawn.Root "native-$RuntimeIdentifier"
    $include = Join-Path $prefix 'include'
    $lib = Join-Path $prefix 'lib'
    New-Item -ItemType Directory -Path $include, $lib -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $source 'include') | Copy-Item -Destination $include -Recurse -Force
    Get-ChildItem -LiteralPath (Join-Path $build 'gen/include') | Copy-Item -Destination $include -Recurse -Force
    $staticLibrary = if ($RuntimeIdentifier.StartsWith('win-')) { 'webgpu_dawn.lib' } else { 'libwebgpu_dawn.a' }
    $compiled = @(Get-ChildItem -LiteralPath $build -Recurse -Filter $staticLibrary)
    if ($compiled.Count -ne 1) { throw "Expected one Dawn static library for $RuntimeIdentifier" }
    Copy-Item -LiteralPath $compiled[0].FullName -Destination (Join-Path $lib $staticLibrary)
}
if ($RuntimeIdentifier.StartsWith('win-')) {
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/Redist/D3D'
    $sdkArch = if ($RuntimeIdentifier -eq 'win-x86') { 'x86' } elseif ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64' }
    foreach ($compiler in @('dxcompiler.dll', 'dxil.dll')) {
        $redist = Join-Path $sdk "$sdkArch/$compiler"
        if (-not (Test-Path -LiteralPath $redist)) { throw "Missing Windows SDK redistributable: $redist" }
        Copy-Item -LiteralPath $redist -Destination (Join-Path $out $compiler)
    }
    $compilerInfo = (Get-Item -LiteralPath (Join-Path $out 'dxcompiler.dll')).VersionInfo
    if ($compilerInfo.ProductVersion -notmatch '\(([a-f0-9]{7,40})\)') { throw 'Cannot identify the DXC source revision.' }
    $compilerRevision = $Matches[1]
    $licensePath = Join-Path $dawn.Root "LICENSE.dxc-$compilerRevision"
    if (-not (Test-Path -LiteralPath $licensePath)) {
        Invoke-WebRequest "https://raw.githubusercontent.com/microsoft/DirectXShaderCompiler/$compilerRevision/LICENSE.TXT" -OutFile $licensePath -UseBasicParsing
    }
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $out 'LICENSE.dxc')
    $compilerInfo.ProductVersion | Set-Content -LiteralPath (Join-Path $out 'dxc-version.txt') -Encoding UTF8
}
Write-Host "Dawn SDK $($dawn.Tag): $prefix"
