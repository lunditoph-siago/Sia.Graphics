#!/usr/bin/env bash
set -euo pipefail

readonly script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly repository_root="$(cd -- "$script_directory/.." && pwd)"
readonly source_directory="${1:-$repository_root/artifacts/spirv-tools}"
readonly build_directory="${2:-$repository_root/artifacts/spirv-tools-build-linux-x64}"
readonly install_directory="${3:-$repository_root/artifacts/llvm-toolchain-linux-x64}"
readonly headers_directory="$source_directory/external/spirv-headers"
readonly expected_tools_commit="0539c81f69a3daeb706fd3477dca61435b475156"
readonly expected_headers_commit="ad9184e76a66b1001c29db9b0a3e87f646c64de0"

if [[ ! -f "$source_directory/CMakeLists.txt" ]]; then
  echo "SPIRV-Tools source was not found at '$source_directory'." >&2
  exit 1
fi
if [[ ! -f "$headers_directory/include/spirv/unified1/spirv.core.grammar.json" ]]; then
  echo "The pinned SPIRV-Headers source was not found at '$headers_directory'." >&2
  exit 1
fi
if [[ "$(git -C "$source_directory" rev-parse HEAD)" != "$expected_tools_commit" ]]; then
  echo "The SPIRV-Tools checkout does not match pinned commit '$expected_tools_commit'." >&2
  exit 1
fi
if [[ "$(git -C "$headers_directory" rev-parse HEAD)" != "$expected_headers_commit" ]]; then
  echo "The SPIRV-Headers checkout does not match pinned commit '$expected_headers_commit'." >&2
  exit 1
fi

if [[ "${SIA_NATIVE_SKIP_CONFIGURE:-0}" != "1" ]]; then
  cmake --fresh \
    -S "$source_directory" \
    -B "$build_directory" \
    -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DSPIRV_SKIP_TESTS=ON \
    -DSPIRV_WERROR=OFF \
    -DSPIRV_TOOLS_BUILD_STATIC=ON \
    -DBUILD_SHARED_LIBS=OFF
elif [[ ! -f "$build_directory/build.ninja" ]]; then
  echo "The cached SPIRV-Tools build tree '$build_directory' is not configured." >&2
  exit 1
fi

cmake --build "$build_directory" \
  --target spirv-as spirv-dis spirv-link spirv-opt spirv-val \
  --parallel "${SIA_NATIVE_BUILD_JOBS:-2}"

install -d "$install_directory/bin" "$install_directory/licenses"
for tool in spirv-as spirv-dis spirv-link spirv-opt spirv-val; do
  install -m 755 "$build_directory/tools/$tool" "$install_directory/bin/$tool"
done
install -m 644 "$source_directory/LICENSE" \
  "$install_directory/licenses/SPIRV-Tools.txt"
install -m 644 "$headers_directory/LICENSE" \
  "$install_directory/licenses/SPIRV-Headers.txt"

"$install_directory/bin/spirv-val" --version
