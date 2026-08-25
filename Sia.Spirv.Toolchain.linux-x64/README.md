# Sia SPIR-V Linux x64 host toolchain

This package contains the prebuilt LLVM 23 SPIR-V backend and Khronos
SPIRV-Tools used by `Sia.Spirv.Sdk` on Linux x64 build hosts. It is a build
tool dependency, not an application runtime dependency or GPU target.

The package is produced only from the pinned sources and reproducible scripts
owned by this project. CI builds the binaries on a native Linux x64 runner;
prebuilt binaries remain package artifacts and are not tracked in Git.

After preparing the pinned sources under `artifacts/`, build and stage the host
tools in dependency order:

```bash
./Sia.Spirv.Toolchain.linux-x64/BuildLLVM.sh
./Sia.Spirv.Toolchain.linux-x64/BuildSpirvTools.sh
```

Both scripts stage tools and licenses under
`artifacts/llvm-toolchain-linux-x64` for this project to pack.
