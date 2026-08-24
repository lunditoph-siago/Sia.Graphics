# Sia SPIR-V first roll

This repository contains the first end-to-end version of the Sia C# kernel
compiler. LLVM 23 is the only production code-generation path. The managed
compiler discovers attributed methods in a normal .NET assembly, validates the
GPU language subset, lowers CIL semantics to Vulkan-oriented LLVM IR, invokes
LLVM's SPIR-V target, and validates every module with Khronos `spirv-val`.

## Project experience

Once the workload baseline manifest and pack are installed, a normal .NET SDK
project opts in with one property. During local development, the sample imports
the same targets directly.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <EnableSpirvCompilation>true</EnableSpirvCompilation>
    <SpirvTargetEnvironment>vulkan1.2</SpirvTargetEnvironment>
    <SpirvOptimizationLevel>2</SpirvOptimizationLevel>
  </PropertyGroup>
</Project>
```

A normal build writes one set of artifacts per kernel:

```text
bin/Debug/net11.0/spirv/
  MyAssembly.Kernels.Saxpy.ll
  MyAssembly.Kernels.Saxpy.spv
  MyAssembly.Kernels.Saxpy.spv.json
```

The JSON file records the entry point, actual SPIR-V version, workgroup size,
descriptor bindings, push-constant layout, content hash, LLVM version, and
SPIRV-Tools version. A second unchanged build reuses the validated artifact.

## Native toolchain

The Windows x64 first roll pins these sources:

- `dotnet/llvm-project`, branch `dotnet/main-23.x`
- Khronos SPIRV-Tools `v2026.2`
- the SPIRV-Headers commit recorded by SPIRV-Tools `v2026.2`

Build LLVM with `Sia.Spirv.Toolchain.win-x64/BuildLLVM.ps1`, then build the
Khronos tools with `Sia.Spirv.Toolchain.win-x64/BuildSpirvTools.ps1`. Both
scripts install into `artifacts/llvm-toolchain`. The scripts also stage the
upstream licenses. The prebuilt executables are delivered by
`Sia.Spirv.Toolchain.win-x64`; the managed SDK package does not contain native
binaries. Consumers therefore do not use a system LLVM, Vulkan SDK, or
separately installed `spirv-val`.

## Workload packages

Build the initial NuGet and workload packages with:

```powershell
./Sia.Spirv.Workload.Manifest/Pack.ps1
```

The output includes the managed `Sia.Spirv.Sdk` pack, the prebuilt
`Sia.Spirv.Toolchain.win-x64` host pack, and the
`Sia.Spirv.Workload.Manifest-11.0.100` manifest package. The manifest defines
the Windows x64 `spirv-tools` workload. A stock third-party .NET installation
does not know an independent workload ID until its feature-band manifest has
been bootstrapped. For the workspace-local SDK, install the baseline manifest
and then the workload from the generated package source:

```powershell
./Sia.Spirv.Workload.Manifest/Install.ps1
../.dotnet/dotnet workload install spirv-tools `
  --source ./artifacts/packages `
  --skip-manifest-update
```

The published bootstrap installer performs the first step; subsequent installs
use the intended `dotnet workload install spirv-tools` command.

CI or a project that cannot install workloads can use the NuGet fallback. Both
references are private build dependencies; `buildTransitive` imports the same
SDK integration used by the workload:

```xml
<ItemGroup>
  <PackageReference Include="Sia.Spirv.Sdk"
                    Version="0.1.0-preview.1"
                    PrivateAssets="all" />
  <PackageReference Include="Sia.Spirv.Toolchain.win-x64"
                    Version="0.1.0-preview.1"
                    PrivateAssets="all" />
</ItemGroup>
```

## Supported kernel profile

The first roll supports:

- static `void` kernels with fixed nonzero workgroup dimensions;
- `int`, `uint`, and `float` values;
- `StorageBuffer<T>` resources and scalar push constants;
- global, local, and workgroup invocation IDs;
- arithmetic, bit operations, comparisons, conversions, `if`, `for`, and
  `while` control flow;
- LLVM optimization, Vulkan 1.2 or 1.3 output, and mandatory SPIR-V validation.

It diagnoses managed allocation, strings, exceptions, and dynamic dispatch.
Reachable helper-method specialization, user structs, wider scalar types,
barriers, atomics, textures, analyzers, source maps, a native LLVM C ABI, and a
Vulkan dispatch runtime are subsequent slices. `Sia.Spirv.Runtime` in this roll
loads and indexes validated binary/manifest pairs but does not create Vulkan
objects.
