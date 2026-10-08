#!/usr/bin/env bash
set -euo pipefail

readonly script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly repository_root="$(cd -- "$script_directory/.." && pwd)"
readonly install_directory="${1:-$repository_root/artifacts/llvm-toolchain-linux-x64}"
readonly project_directory="$repository_root/Sia.Spirv.Naga"
readonly native_output="$project_directory/target/release"

cargo test \
  --manifest-path "$project_directory/Cargo.toml" \
  --release \
  --locked
cargo build \
  --manifest-path "$project_directory/Cargo.toml" \
  --release \
  --locked \
  --bin naga \
  --lib

install -d \
  "$install_directory/bin" \
  "$install_directory/include" \
  "$install_directory/licenses"
install -m 755 "$native_output/naga" "$install_directory/bin/naga"
install -m 755 \
  "$native_output/libsia_spirv_naga.so" \
  "$install_directory/bin/libsia_spirv_naga.so"
install -m 644 \
  "$project_directory/target/include/sia_spirv_naga.h" \
  "$install_directory/include/sia_spirv_naga.h"
install -m 644 "$script_directory/Naga.LICENSE.txt" \
  "$install_directory/licenses/Naga.txt"

"$install_directory/bin/naga" --version
