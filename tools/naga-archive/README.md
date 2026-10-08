# Managed Naga research archive

Frozen on 2026-10-08 in `chore/naga-managed-archive`. This is an experimental
SPIR-V/WGSL implementation checkpoint, not a complete Naga replacement or a
production release. Existing Rust tools and consumers retain their current paths.

## Contents and validation

- `../../Sia.Spirv.Naga`: managed .NET 10 readers, shared typed IR, validation,
  transformation passes and writers. BCL only; upstream license accompanies it.
- `../../Sia.Spirv.Naga.Tests`: 1240 maintained tests; last full run passed all
  tests, with no skips. The archive publication reruns this suite separately.
- `snapshot/`: all 105 task Python scripts, three PowerShell scripts, custom
  C# harnesses/fixture generators, Rust reference oracle and synthetic root
  shader inputs. `inventory.json` records 178 files with SHA-256 checksums.
- `evidence/`: frozen port history, compact corpus exit codes, prior summaries
  and partial descriptor-identity results. These distinguish passing translated
  behavior, native device differences and intentionally interrupted checks.

The descriptor identity stage has 223/223 native validations and 54/108 completed
GPU cases (13530 recorded checks, 4320 dispatches, 12960 readbacks) on Intel UHD
Graphics. Its atomic GPU cases remain unrun. The 197-input reference corpus still
has failures: forward managed WGSL191pass/6fail and SPIR-V189pass/8fail. Full
uniformity/alias analysis, extensions, browser/AOT execution and consumer migration
remain incomplete. Earlier summaries refer to earlier assembly hashes; do not
treat them as new verification of the final assembly. The frozen final DLL hash
is recorded in `evidence/descriptor-identity-summary.json`; source rebuild hashes
can vary with SDK/path and must not be substituted silently in historical scripts.

Downloaded SDKs, NuGet/Cargo caches, external tool source/build trees, old compiled
baseline DLLs, generated shader outputs and raw logs/readbacks are excluded. All
custom tool source is preserved. Synthetic root SPIR-V inputs needed by legacy
GPU projects are included as test data, not executable tool binaries. The one
machine-specific legacy project now uses relative Includes; the inventory records
that adaptation. Historical scripts requiring old baseline DLLs or old generated
reports need the original retained task area or an appropriate historical build.

## Restore and run

From a Graphics checkout with .NET 10 installed:

```powershell
dotnet test Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release
python tools/naga-archive/restore.py --verify-only
```

The external experiment scripts expect the original workspace layout:

```text
<workspace>/
  .dotnet/dotnet.exe
  .reference/wgpu/
  .work/naga-csharp/
  Sia.Graphics/
```

Restore into a fresh task directory from `<workspace>`:

```powershell
python Sia.Graphics/tools/naga-archive/restore.py --workspace .
./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release
./.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release -- identities
```

Restoration checks all source hashes first and refuses to overwrite different
existing data. `--destination .work/naga-archive-copy` can preserve an existing
experiment, but legacy scripts still expect `.work/naga-csharp` when executed.
They are archived research tools, not a new cross-platform CLI.

External validation needs the pinned upstream wgpu checkout
`34bf26700a92698d619a56f5b76a1aabfac4c0ab` (Naga30.0.1), Rust/Cargo and matching
SPIRV-Tools `spirv-as.exe`/`spirv-val.exe` in
`.work/naga-csharp/spirv-tools-local/tools/Release/`. Build the oracle with
`cargo build --release --locked --manifest-path .work/naga-csharp/oracle/Cargo.toml --target-dir .work/naga-csharp/oracle-target`.
Corpus validation uses `python .work/naga-csharp/scan-corpus.py`. GPU scripts need
Python and a Vulkan1.2 loader/device with the tested capabilities; they are
Windows-oriented and often pin assembly hashes deliberately. Optional legacy
GPU harnesses also reference workspace GPU Diagnostics and Engine sources;
their project files enumerate those dependencies. No tool download, baseline
reconstruction or GPU test runs automatically on restoration.

## Future Compiler integration assessment

The proposed direction is feasible: keep one shader semantic model/legalization
layer, use LLVM for the offline path, and add a managed IL-to-model path with direct
SPIR-V and WGSL emission for runtime compilation. Existing Compiler already has
IL decoding, stack/CFG analysis, resource/layout legalization and LLVM emission.
Its current LLVM path launches external programs and its WGSL path still invokes
the Rust Naga CLI; moving these files into one assembly alone cannot provide a
browser runtime compiler.

Keep LLVM/tool execution optional at the build-host boundary. A browser-capable
compiler core should accept IL/PE bytes plus explicit metadata and emit shader
bytes/text in memory, retaining the input IL through trimming/AOT. This processes
IL as data and does not require executing newly generated managed methods. LLVM
SPIR-V output must select the Vulkan shader environment, not its OpenCL default.
Both paths need matching resource ABI, integer/float semantics, specialization,
control flow, memory behavior and diagnostics. WGSL additionally requires its
control-flow/uniformity rules. Differential tests between paths are essential.

Reuse the archive's useful emitter, validation and regression code under Compiler
before retiring the Naga module; a full general SPIR-V reader is optional if the
future product only compiles its own restricted IL shader language. Delete the
module after desktop/browser shader creation, GPU equivalence, and every existing
consumer's migration pass. Wasm here means the compiler running in a browser; a
shader-to-CPU-Wasm backend is a separate scope. This section is an assessment,
not authorization or implementation of that migration.

References: [LLVM SPIR-V target guide](https://llvm.org/docs/SPIRVUsage.html),
[WGSL uniformity analysis](https://www.w3.org/TR/WGSL/#uniformity),
[.NET WebAssembly features](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md).
