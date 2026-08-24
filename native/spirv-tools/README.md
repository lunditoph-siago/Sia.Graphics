# Khronos SPIRV-Tools

The validation toolchain is pinned to SPIRV-Tools `v2026.2` and the exact
SPIRV-Headers revision recorded by that release.

Prepare the sources:

```powershell
git clone --depth 1 --branch v2026.2 `
  https://github.com/KhronosGroup/SPIRV-Tools.git artifacts/spirv-tools
git clone https://github.com/KhronosGroup/SPIRV-Headers.git `
  artifacts/spirv-tools/external/spirv-headers
git -C artifacts/spirv-tools/external/spirv-headers checkout `
  ad9184e76a66b1001c29db9b0a3e87f646c64de0
```

Build the command-line tools:

```powershell
./native/spirv-tools/build-toolchain.ps1
```

The script installs `spirv-as`, `spirv-dis`, `spirv-link`, `spirv-opt`, and
`spirv-val` beside LLVM in `artifacts/llvm-toolchain/bin`.
