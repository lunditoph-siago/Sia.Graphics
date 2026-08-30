<#
.SYNOPSIS
Builds the wasm32-unknown-emscripten libwgpu_native.a archive the wgpu/GLES
siawebgpu port needs (see siawebgpu.port.py's default archive path:
runtimes/browser-wasm/native/libwgpu_native.a).

.DESCRIPTION
Requires on PATH / in the environment:
  - nightly Rust with the wasm32-unknown-emscripten target and rust-src:
      rustup toolchain install nightly
      rustup target add wasm32-unknown-emscripten --toolchain nightly
      rustup component add rust-src --toolchain nightly
  - $env:EMSCRIPTEN_SYSROOT: a built emscripten sysroot (e.g. the one a Dawn
    `--use-port` build already produced under obj_browser\siawebgpu-cache,
    or any `embuilder build sysroot` output). Used only for bindgen to parse
    wgpu-native's C headers, not for the actual Rust compilation.
  - $env:LIBCLANG_PATH: a directory containing a libclang shared library
    (bindgen's dependency), e.g. the libclang.runtime.<rid> NuGet package.

.PARAMETER OutFile
Where to write the built archive. Defaults to
runtimes/browser-wasm/native/libwgpu_native.a relative to this script.
#>
[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$WgpuNativeTag = 'v29.0.1.1'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PatchFile = Join-Path $ScriptDir 'wgpu-native-v29.0.1.1-emscripten.patch'
if (-not $OutFile) {
    $OutFile = Join-Path $ScriptDir '..\..\runtimes\browser-wasm\native\libwgpu_native.a'
}
$WorkDir = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())

if (-not $env:EMSCRIPTEN_SYSROOT) {
    throw 'Set EMSCRIPTEN_SYSROOT to a built emscripten sysroot.'
}
if (-not $env:LIBCLANG_PATH) {
    throw 'Set LIBCLANG_PATH to a directory containing libclang.'
}

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
try {
    rustup target add wasm32-unknown-emscripten --toolchain nightly
    if ($LASTEXITCODE -ne 0) { throw 'rustup target add failed.' }
    rustup component add rust-src --toolchain nightly
    if ($LASTEXITCODE -ne 0) { throw 'rustup component add failed.' }

    $repoDir = Join-Path $WorkDir 'wgpu-native'
    git clone --quiet --branch $WgpuNativeTag --depth 1 `
        --recurse-submodules --shallow-submodules `
        https://github.com/gfx-rs/wgpu-native.git $repoDir
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }

    git -C $repoDir apply $PatchFile
    if ($LASTEXITCODE -ne 0) { throw 'git apply failed.' }

    $env:RUSTFLAGS = '-C llvm-args=-wasm-use-legacy-eh=0 -C panic=abort'
    try {
        cargo +nightly build `
            --manifest-path (Join-Path $repoDir 'Cargo.toml') `
            --release `
            --target wasm32-unknown-emscripten `
            -Z build-std=std,panic_abort,core,alloc `
            --no-default-features `
            --features wgsl,spirv,glsl,gles
        if ($LASTEXITCODE -ne 0) { throw 'cargo build failed.' }
    }
    finally {
        Remove-Item Env:\RUSTFLAGS -ErrorAction SilentlyContinue
    }

    $built = Join-Path $repoDir 'target\wasm32-unknown-emscripten\release\libwgpu_native.a'
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutFile) -Force | Out-Null
    Copy-Item -LiteralPath $built -Destination $OutFile -Force
    Write-Host "Built $OutFile"
}
finally {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}
