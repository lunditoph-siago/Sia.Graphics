# Native compiler validation

Windows x64 results from 2026-11-06, retained for PR #119. Full per-case outcomes,
source/binary hashes, build commands and reproduction harnesses are archived in
[the original-source record](https://github.com/lunditoph-siago/Sia.Graphics/blob/43dcd6d56fbdb2468a78bbe1e669b39200618ad4/Sia.Spirv.Compiler.Tests/UpstreamComparison.json)
and [the integer fuzz record](https://github.com/lunditoph-siago/Sia.Graphics/blob/43dcd6d56fbdb2468a78bbe1e669b39200618ad4/Sia.Spirv.Compiler.Tests/FuzzOptimization.json).
These are historical characterization results, not conformance or GPU benchmarks.

## Tools and reproduction

| Tool | Pinned source / build |
| --- | --- |
| Tint | [Dawn 77ca1f2](https://github.com/google/dawn/tree/77ca1f229d3ff915d4992fd8c937583871e97237), Release CMake/Ninja target `tint_cmd_tint_cmd`, WGSL/SPIR-V readers and writers enabled |
| Naga CLI 30.0.0 | [wgpu a553529](https://github.com/gfx-rs/wgpu/tree/a55352911bb26a1b33bc8d5fc03bdebb0a967f5a), Rust 1.95.0, `cargo build --release --locked -p naga-cli` |
| SPIRV-Tools 2026.2 | `0539c81f69a3daeb706fd3477dca61435b475156` |

The records include executable SHA-256 fingerprints. Tint's reader requires
Vulkan 1.1/SPIR-V 1.3; runtime IL compiles that version directly. Offline LLVM
23.0.0git output retains its supported Vulkan 1.2/SPIR-V 1.5 profile and is
outside Tint's reader profile. No binary version headers were rewritten.

From this repository, with its SPIR-V toolchain and .NET 10/11 runtimes installed:

```powershell
$env:SIA_TEST_TINT = '<native Tint executable>'
$env:SIA_TEST_NAGA = '<native Naga executable>'
dotnet test Sia.Graphics.slnx -c Release --blame-hang --blame-hang-timeout 5m -m:1 -p:UseSharedCompilation=false
```

The historical full run passed 194 tests with zero failures/skips. The current
[test guide](README.md) describes reorganized coverage and independent optional
Tint/Naga selection. Regressions cover integer boundaries and operand effects,
aggregate/phi lowering, evaluation stages, builtin types, uniformity, and IL
helper/control behavior.

Use the archived revisions when recreating their console projects and scripts,
remap workspace paths to the pinned checkouts, then use their recorded commands:

```text
naga --spirv-version 1.5 INPUT.wgsl OUTPUT.spv
tint INPUT.wgsl --format spirv -o OUTPUT.spv --disable-robustness [--entry-point NAME]
naga INPUT.spv OUTPUT.wgsl
tint INPUT.spv --format wgsl -o OUTPUT.wgsl
spirv-val --target-env vulkan1.2 OUTPUT.spv
naga OUTPUT.wgsl
```

## Original-source and IL coverage

The corpus has all 183 Naga WGSL inputs and the first 100 sorted original Tint
`test/tint/bug/tint/*.wgsl` inputs; expected-output files are excluded. Sia's
before column is the preceding compiler stage, not a clean release checkout.
Tint selects one entry for 27 multi-entry fixtures, so native binary totals are
not matched size comparisons. Overrides without defaults remain unresolved.

| Check | Result |
| --- | --- |
| Sia WGSL → SPIR-V / → WGSL | 262 → 264 / 254 → 260 of 283 |
| Naga WGSL → SPIR-V | 267 binaries, 13 diagnostics, 3 CLI panics |
| Tint WGSL → SPIR-V | 227 binaries, 55 diagnostics, 1 library without a binary |
| Runtime / offline IL → SPIR-V → WGSL | 29 / 29 kernels independently validated |
| Native WGSL from runtime IL | Tint 28/29, Naga 28/29; both reject `IsNan` |
| Naga WGSL from offline IL | 29/29 |
| Sia WGSL text from native Naga / Tint binaries | 242/267 / 214/227 |
| Naga validates that native-input WGSL | 424/456 |
| Independent checks on Sia's own round trips | 519/524 |
| Tint recompiles Sia WGSL in its original-source common profile | 214/215 |

The four extended IL fixtures start from storage `[7, 0]` and check independent
expected results `[1, 2]`, `[7, 11]`, `[10, 18]`, `[10, 66]` through Sia and both
native readers. They cover loop-carried swaps, switch/continue, captured reads
before writes, and nested helper calls repeated in a loop.

## Nonzero-divisor fuzz comparison

Against commit `35da7d4f728fc9fc71290d4e04d580019466036c`, the proof removes checked
unsigned helpers for divisors proven nonzero. Counterexample tests retain checks
for mutable/unknown values, narrowing, zero lanes, overrides and signed overflow.
The corpus deliberately stresses this proof: 160 bounded u32 programs from seeds
`0x5eed` (96) and `0x123400` (64), each with two equivalent variants.

All 320 before/after Sia binaries validate and match an independent Python source
evaluator through a strict integer SPIR-V interpreter on four boundary vectors
(1,280 executions per revision). Both native writers recompile all generated WGSL:
640 more valid binaries and 2,560 matching executions. This run uses SPIR-V 1.3 /
Vulkan 1.1, Tint `--disable-robustness`, and Naga's `Unchecked` index/buffer bounds
policies. It does not execute a GPU, floating point or synchronization semantics.

| Static measure, identical inputs/options | Before | After |
| --- | ---: | ---: |
| Binary bytes | 655,096 | 598,408 (−8.65%) |
| Instructions excluding debug | 35,798 | 33,186 (−7.30%) |
| Function call instructions | 1,536 | 1,168 (−23.96%) |

172 binaries shrink, 148 stay the same size, none grows; loads/stores/phi/local
counts are unchanged. The three-writer record compares only 268 original sources:
52 Naga pointer-argument panics are excluded, never treated as empty binaries.

## Known gaps

[Issue #120](https://github.com/lunditoph-siago/Sia.Graphics/issues/120) tracks
Tint fixture 1518 exceeding nesting/chaining limits and 28 native vertex modules
retaining invalid WGSL `point_size`. Other classified failures include runtime
out-of-bounds cases 1563/1666, two pinned-Naga parser failures on standard
`enable subgroups`, and a no-entry library outside the executable-module profile.
Native reading still has capability/type/memory/uniformity gaps listed per case
in the original record. Private builtin promotion requires one canonical entry
and an unconditional copy before reads/calls. Coherent-memory annotations and
unsupported diagnostics remain visible. No defined out-of-bounds value, general
SPIR-V conformance, GPU speed or driver behavior is established by these checks.
