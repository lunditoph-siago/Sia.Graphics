#!/usr/bin/env bash
set -euo pipefail

readonly script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly repository_root="$(cd -- "$script_directory/.." && pwd)"
readonly install_directory="${1:-$repository_root/artifacts/llvm-toolchain-linux-x64}"
readonly version="${2:-30.0.0}"
readonly cargo_root="$repository_root/artifacts/naga-cli-linux-x64"
readonly naga="$cargo_root/bin/naga"

if [[ ! -x "$naga" ]] || [[ "$($naga --version)" != "$version" ]]; then
  cargo install naga-cli \
    --version "$version" \
    --locked \
    --force \
    --root "$cargo_root"
fi

install -d "$install_directory/bin" "$install_directory/licenses"
install -m 755 "$naga" "$install_directory/bin/naga"
install -m 644 "$script_directory/Naga.LICENSE.txt" \
  "$install_directory/licenses/Naga.txt"

"$install_directory/bin/naga" --version
