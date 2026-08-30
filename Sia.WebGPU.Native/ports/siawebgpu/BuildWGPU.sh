#!/usr/bin/env bash
# Builds the wasm32-unknown-emscripten libwgpu_native.a archive the wgpu/GLES
# siawebgpu port needs (see siawebgpu.port.py's SiaWebGpuWgpuArchive default:
# runtimes/browser-wasm/native/libwgpu_native.a).
#
# Requires on PATH / in the environment:
#   - nightly Rust with the wasm32-unknown-emscripten target and rust-src:
#       rustup toolchain install nightly
#       rustup target add wasm32-unknown-emscripten --toolchain nightly
#       rustup component add rust-src --toolchain nightly
#   - EMSCRIPTEN_SYSROOT: a built emscripten sysroot (e.g. the one a Dawn
#     `--use-port` build already produced under obj_browser/siawebgpu-cache,
#     or any `embuilder build sysroot` output). Used only for bindgen to
#     parse wgpu-native's C headers, not for the actual Rust compilation.
#   - LIBCLANG_PATH: a directory containing a libclang shared library
#     (bindgen's dependency), e.g. the libclang.runtime.<rid> NuGet package.
set -euo pipefail

wgpu_native_tag="v29.0.1.1"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
patch_file="$script_dir/wgpu-native-v29.0.1.1-emscripten.patch"
out_file="${1:-$script_dir/../../runtimes/browser-wasm/native/libwgpu_native.a}"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

: "${EMSCRIPTEN_SYSROOT:?Set EMSCRIPTEN_SYSROOT to a built emscripten sysroot}"
: "${LIBCLANG_PATH:?Set LIBCLANG_PATH to a directory containing libclang}"

rustup target add wasm32-unknown-emscripten --toolchain nightly
rustup component add rust-src --toolchain nightly

git clone --quiet --branch "$wgpu_native_tag" --depth 1 \
  --recurse-submodules --shallow-submodules \
  https://github.com/gfx-rs/wgpu-native.git "$work_dir/wgpu-native"
git -C "$work_dir/wgpu-native" apply "$patch_file"

RUSTFLAGS="-C llvm-args=-wasm-use-legacy-eh=0 -C panic=abort" \
  cargo +nightly build \
    --manifest-path "$work_dir/wgpu-native/Cargo.toml" \
    --release \
    --target wasm32-unknown-emscripten \
    -Z build-std=std,panic_abort,core,alloc \
    --no-default-features \
    --features wgsl,spirv,glsl,gles

mkdir -p "$(dirname "$out_file")"
cp "$work_dir/wgpu-native/target/wasm32-unknown-emscripten/release/libwgpu_native.a" "$out_file"
echo "Built $out_file"
