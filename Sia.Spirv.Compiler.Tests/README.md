# Core compiler tests

Run from the Graphics repository root with its configured .NET SDK:

```powershell
dotnet test Sia.Spirv.Compiler.Tests/Sia.Spirv.Compiler.Tests.csproj -c Release
```

| Area | Core checks |
| --- | --- |
| [Compilation](Compilation/) | Compile all discovered C# kernels, validate SPIR-V through the public compiler, parse WGSL, match manifest/stage ABI, check offline storage results and uniform-buffer packing |
| [IL](IL/) | Stage/resource discovery, required control-flow/memory ABI, runtime integer lowering and storage results |
| [Resources](Resources/) | Layout, padding, CPU/GPU packing and resource-budget decisions |
| [Translation](Translation/) | Integer boundaries and unsafe-proof counterexamples, expected memory/effects through normalization and SPIR-V round trips, core diagnostics, uniformity and valid target binaries |
| [Interoperability](Interoperability/) | Expected IL storage results through Tint/Naga and Tint acceptance of the workgroup-uniform adapter |

[Fixtures](Fixtures/) holds shader inputs/expected values; [Support](Support/)
holds shared tools and limited interpreters. Tests do not call other test classes.
The suite deliberately omits exact optimization shape/size, direct pass/AST
tests, auxiliary pass-file I/O and broad builtin/f16/subgroup overload matrices.
Math fixtures still receive compilation smoke coverage, not numeric conformance.

## Optional tools

Use `--filter "Category!=Toolchain&Category!=Native"` for managed checks,
`--filter "Category=Toolchain"` for LLVM/SPIRV-Tools, or
`--filter "Category=Native"` for upstream comparisons.

```powershell
$env:SIA_TEST_TINT = '<Tint executable>'
$env:SIA_TEST_NAGA = '<Naga executable>'
```

Each native compiler is independently optional: an unset path skips, a configured
invalid path fails. Toolchain discovery uses `SIA_SPIRV_TOOLCHAIN`, ancestor
`artifacts/llvm-toolchain` locations and .NET toolchain packs. It requires `llc`,
`opt`, `spirv-as`, `spirv-dis`, `spirv-opt` and `spirv-val`. Inspect skipped counts.
Native comparison/assembly/disassembly processes have a 30 second timeout with
process-tree termination. Temporary outputs have one disposable owner.

## Limits

The scalar integer and WGSL fixture interpreters reject unsupported operations,
have step bounds, and compare explicit expected values. WGSL execution covers
one invocation over two u32 cells, supported vectors, parallel phi transfers and
ordered effects. It reuses the compiler frontend/IR, not an independent WGSL
implementation. Native builtin arithmetic uses the shared constant folder and a
separate refract formula. Out-of-bounds cases check compilation stages/validity,
not a defined value. Native readers receive SPIR-V 1.3 directly; other binary
checks use the default target. No header rewriting or disabled validation.

GPU execution, general floating-point/synchronization conformance and broad fuzz
are outside this core suite. [Historical upstream results](UpstreamComparison.md)
keep their original profiles and coverage; those counts are not current tests.
