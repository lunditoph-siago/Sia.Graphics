# Sia SPIR-V Windows x64 host toolchain

This package contains the prebuilt LLVM 23 SPIR-V backend and Khronos
SPIRV-Tools used by `Sia.Spirv.Sdk` on Windows x64 build hosts. It is a build
tool dependency, not an application runtime dependency or GPU target.

The package is produced only from the pinned sources and reproducible scripts
under `native/`. Prebuilt binaries remain package artifacts and are not tracked
in Git. Source revisions and licenses are included in the package.
