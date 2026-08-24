# LLVM SPIR-V toolchain

The first toolchain version is based on the `dotnet/main-23.x` branch of
`dotnet/llvm-project`. The upstream source already contains LLVM's official
SPIR-V target; the build script enables it alongside X86 and builds only the
tools required by the managed compiler.

Prepare a shallow source checkout:

```powershell
git clone --depth 1 --branch dotnet/main-23.x --filter=blob:none --sparse `
  https://github.com/dotnet/llvm-project.git artifacts/dotnet-llvm-project
git -C artifacts/dotnet-llvm-project sparse-checkout set llvm cmake third-party
```

Build the host toolchain on Windows:

```powershell
./native/llvm/build-toolchain.ps1
```

The tools are written to `artifacts/llvm-toolchain/bin` and the LLVM license is
staged under `artifacts/llvm-toolchain/licenses` for packaging. Build artifacts,
prebuilt binaries, and third-party sources are intentionally excluded from the
repository. Build the Khronos validation tools described in
`../spirv-tools/README.md` into the same directory before using the managed
compiler.
