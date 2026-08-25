#!/usr/bin/env bash
set -euo pipefail

readonly script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly repository_root="$(cd -- "$script_directory/.." && pwd)"
readonly source_directory="${1:-$repository_root/artifacts/dotnet-llvm-project}"
readonly build_directory="${2:-$repository_root/artifacts/llvm-build-linux-x64}"
readonly install_directory="${3:-$repository_root/artifacts/llvm-toolchain-linux-x64}"
readonly expected_commit="10eea834e26333cb0d3e71e75faa536144ecc099"

if [[ ! -d "$source_directory/llvm/lib/Target/SPIRV" ]]; then
  echo "The LLVM source at '$source_directory' does not contain the SPIR-V target." >&2
  exit 1
fi
if [[ "$(git -C "$source_directory" rev-parse HEAD)" != "$expected_commit" ]]; then
  echo "The LLVM checkout does not match pinned commit '$expected_commit'." >&2
  exit 1
fi

if [[ "${SIA_NATIVE_SKIP_CONFIGURE:-0}" != "1" ]]; then
  cmake --fresh \
    -S "$source_directory/llvm" \
    -B "$build_directory" \
    -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DLLVM_TARGETS_TO_BUILD=SPIRV \
    -DLLVM_BUILD_TOOLS=ON \
    -DLLVM_INCLUDE_TESTS=OFF \
    -DLLVM_INCLUDE_BENCHMARKS=OFF \
    -DLLVM_INCLUDE_EXAMPLES=OFF \
    -DLLVM_ENABLE_BINDINGS=OFF \
    -DLLVM_ENABLE_TERMINFO=OFF \
    -DLLVM_ENABLE_ZLIB=OFF \
    -DLLVM_ENABLE_ZSTD=OFF \
    -DLLVM_ENABLE_LIBXML2=OFF \
    -DLLVM_ENABLE_CURL=OFF
elif [[ ! -f "$build_directory/build.ninja" ]]; then
  echo "The cached LLVM build tree '$build_directory' is not configured." >&2
  exit 1
fi

cmake --build "$build_directory" \
  --target llc opt llvm-as llvm-dis \
  --parallel "${SIA_NATIVE_BUILD_JOBS:-2}"

install -d "$install_directory/bin" "$install_directory/licenses"
for tool in llc opt llvm-as llvm-dis; do
  install -m 755 "$build_directory/bin/$tool" "$install_directory/bin/$tool"
done
install -m 644 "$source_directory/llvm/LICENSE.TXT" \
  "$install_directory/licenses/LLVM.txt"

"$install_directory/bin/llc" --version
