# Compiler tests

Run commands below from the Graphics repository root, using its configured .NET
SDK. The solution includes this project; no extra test packages are required.

```powershell
dotnet test Sia.Spirv.Compiler.Tests/Sia.Spirv.Compiler.Tests.csproj -c Release
```

## Areas and expectations

| Directory | Coverage and assertions |
| --- | --- |
| [IL](IL/) | Metadata/stage/resource discovery, LLVM instruction and ABI selection, runtime IL integer lowering, and expected storage results for loops, switches and helper effects |
| [Resources](Resources/) | Logical/physical struct layout, padding and offsets, CPU/GPU packing and unpacking, resource budgets and storage-to-uniform decisions |
| [Compilation](Compilation/) | Every discovered kernel's optimized artifacts, manifest and stage ABI, physical SPIR-V layout, target configuration and pass-file validation |
| [Translation](Translation/) | Integer boundaries and safety proofs, aggregate/immutable/phi lowering, dead work, WGSL expression and builtin validation, runtime indices, uniformity, native builtin overloads and independent SPIR-V validation |
| [Interoperability](Interoperability/) | Separate Tint/Naga IL storage-result cases and Tint acceptance of collective/subgroup adapters |
| [Fixtures](Fixtures/) | C# kernels, input/output structures and explicit expected storage values; no test methods |
| [Support](Support/) | Compilation fixtures, limited interpreters, optional-tool discovery, bounded processes and owned temporary directories; no test methods |

Namespaces follow directories. Tests never call another test class. Kernel names
use `typeof`/`nameof` so fixture moves cannot silently break string-based lookup.
The offline artifact fixture compiles each configuration once and shares only
completed, read-only artifacts within one xUnit class; it owns their cleanup.

Each input to a bulk native validator or compiler comparison is a theory row.
Failures identify the kernel, operation or source scenario and do not prevent
other rows from running. Expected values, diagnostics, types, layout and target
validation establish behavior. Text/opcode assertions additionally guard output
quality or required instruction selection; they are not performance benchmarks.
Local-storage checks inspect parsed mutable declarations; declaration counts use
tokens, so generated-name and line-format changes cannot make them pass falsely.

## Tool selection

```powershell
# Managed checks, including LLVM text generation, without external executables.
dotnet test Sia.Spirv.Compiler.Tests/Sia.Spirv.Compiler.Tests.csproj -c Release --filter "Category!=Toolchain&Category!=Native"

# LLVM / SPIRV-Tools integration only.
dotnet test Sia.Spirv.Compiler.Tests/Sia.Spirv.Compiler.Tests.csproj -c Release --filter "Category=Toolchain"

# Native upstream comparisons only; each compiler is independently optional.
$env:SIA_TEST_TINT = '<Tint executable>'
$env:SIA_TEST_NAGA = '<Naga executable>'
dotnet test Sia.Spirv.Compiler.Tests/Sia.Spirv.Compiler.Tests.csproj -c Release --filter "Category=Native"
```

Toolchain discovery checks `SIA_SPIRV_TOOLCHAIN`, ancestor `artifacts/llvm-toolchain`
locations, and installed `Sia.Spirv.Toolchain` packs under .NET. A complete directory
contains `llc`, `opt`, `spirv-as`, `spirv-dis`, `spirv-opt` and `spirv-val` (with
`.exe` on Windows). Toolchain facts/theories explicitly skip when unavailable.
Native Tint cases require only `SIA_TEST_TINT`; Naga cases require only
`SIA_TEST_NAGA`. An unset native path skips; a configured invalid path fails.
Check actual skipped counts, which depend on theory discovery and available tools.
External comparison/assembly/disassembly processes have a 30 second timeout,
drain both output streams, and terminate their process tree on timeout.

The native readers receive directly compiled SPIR-V 1.3. Other binary checks use
the default compilation target and its validator environment; no header rewriting
or disabled validation is used. [Historical upstream results](UpstreamComparison.md)
retain their original revisions, profiles, counts, reproduction sources and limits.

## Semantic oracles and scope

`IntegerGraphInterpreter` checks the lowered scalar i32/u32 fixture graphs with
strict raw shift/division behavior. `WgslFixtureInterpreter` checks one invocation
over two u32 storage cells, supported vector snapshots, parallel phi transfers and
ordered store/atomic/barrier events. Both have step bounds and reject unsupported
operations. Their expected numeric/storage results come from the test data;
they reuse the compiler's frontend/IR and are not independent WGSL implementations.
The native builtin fixture uses `ConstantEvaluator` for arithmetic plus a separate
refract formula; its expected types and numbers are explicit case data.

No GPU, multi-invocation synchronization, floating-point conformance or general
SPIR-V execution is claimed. The broader upstream corpus/fuzz campaigns are
characterization evidence, separate from these maintained regression tests.

The former three parse-only runtime-constant cases are covered by the stronger
`WgslSemanticTests` memory/effect assertions and `WgslValueLoweringTests` SPIR-V
round trips. The duplicate vec2 promotion fact is a row in
`WgslAggregateLoweringTests`, which checks SSA, SPIR-V storage and WGSL for every
aggregate shape. Negative source, override/narrowing/zero-lane and effect-order
counterexamples remain distinct checks.
