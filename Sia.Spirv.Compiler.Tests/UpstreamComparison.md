# Native compiler comparison

Local Windows x64 results, 2026-11-06. The accompanying
[evidence](UpstreamComparison.json) identifies original fixtures by pinned
repository path and SHA-256, records each outcome, and preserves the comparison
harness. This is a characterization corpus, including extensions and deliberately
problematic upstream fixtures, rather than a language conformance percentage.

## Builds and profiles

Both executables were built from source for this comparison:

| Tool | Source | Build |
| --- | --- | --- |
| Tint | [Dawn 77ca1f2](https://github.com/google/dawn/tree/77ca1f229d3ff915d4992fd8c937583871e97237) | Release CMake/Ninja, target `tint_cmd_tint_cmd`; WGSL/SPIR-V readers and writers enabled |
| Naga CLI 30.0.0 | [wgpu a553529](https://github.com/gfx-rs/wgpu/tree/a55352911bb26a1b33bc8d5fc03bdebb0a967f5a) | Rust 1.95.0, `cargo build --release --locked -p naga-cli` |

Tint SHA-256:
`8dacbd9f880cda3a5a11ab8f6787a40576d57f900160111301762e97b37558e3`.
Naga SHA-256:
`85fe42dd056746845f2be9c195a87e89a230f39f84ffc95980ea003f64d5860a`.
Tint has no `--version` option; its source revision and executable hash identify
the build. Its reader validates Vulkan 1.1/SPIR-V 1.3. Runtime IL comparison
therefore compiles SPIR-V 1.3 directly, with no binary header rewriting.
The installed LLVM 23.0.0git toolchain rejects the requested SPIR-V 1.3 target;
offline IL comparison retains its supported Vulkan 1.2/SPIR-V 1.5 profile.

WGSL-to-SPIR-V commands use Naga `--spirv-version 1.5` and Tint
`--disable-robustness`, matching Naga CLI's unchecked indexing policy. Validation
stays enabled. Tint selects the first entry point for 27 multi-entry fixtures;
those binaries are not size comparisons with Naga's whole modules. Overrides
without defaults are not assigned arbitrary values. Sia uses its default WebGPU
target. Independent binary validation uses SPIRV-Tools 2026.2
`0539c81f69a3daeb706fd3477dca61435b475156`, `--target-env vulkan1.2`.

## Coverage and results

The source corpus contains all 183 Naga WGSL inputs and the first 100 sorted
original Tint `test/tint/bug/tint/*.wgsl` inputs. Expected-output WGSL files are
excluded. An initial run accidentally included expected-output fixtures; it is
excluded from the counts below. The Sia baseline is the preceding compiler stage
before this native follow-up, rather than a clean release checkout.

| Check | Result |
| --- | --- |
| Sia WGSL → SPIR-V | 262 → 264 of 283 compile |
| Sia WGSL → SPIR-V → WGSL | 254 → 260 of 283 complete |
| Naga WGSL → SPIR-V | 267 binaries; 13 diagnostics, 3 CLI panics |
| Tint WGSL → SPIR-V | 227 binaries; 55 diagnostics, 1 library emits no binary |
| Runtime IL → SPIR-V → WGSL | 29 kernels compiled and independently validated |
| Same runtime IL binaries → native WGSL | Tint 28/29, Naga 28/29 |
| Offline IL → LLVM → SPIR-V → WGSL | 29 kernels compiled and independently validated |
| Same offline IL binaries → Naga WGSL | 29/29 |
| Sia reads native Naga/Tint SPIR-V and emits WGSL text | 242/267 and 214/227 |
| Naga validates that native-input WGSL text | 424/456; failures classified below |
| Independent checks on Sia's own WGSL round trips | 519/524; failures classified below |
| Tint recompiles Sia WGSL within its original-source common profile | 214/215; fixture 1518 fails |
| Full Release solution tests with native tools supplied | 172 passed, 0 failed, 0 skipped |

Both native readers reject runtime `ShortCircuitShaders.Fragment` because it
contains `IsNan`. Sia preserves that operation; replacing it with a constant to
make another compiler accept the binary would change its semantics. Offline
Tint translation is outside the common input profile and is not counted as a
compiler failure or a passed check.

The new IL kernels exercise cyclic loop values, switch/continue edges, reads
captured before helper writes, and nested helper calls repeated in a loop.
Starting with storage `[7, 0]`, their independent expected results are `[1, 2]`,
`[7, 11]`, `[10, 18]`, and `[10, 66]`. The managed WGSL interpreter checks these
results for Sia output and native Tint/Naga output from identical runtime
SPIR-V inputs. This establishes the tested scalar/control/effect behavior;
GPU execution and driver performance were not measured.

## Changes exercised

- Preserve WGSL integer division/remainder and shift boundaries while replacing
  provably safe constant operations and using native float vector multiplication.
- Promote nonescaping aggregate reads, remove unused pure SSA work, compact
  structured expressions and phi transfers, and promote safe local stores to
  immutable declarations. Retain ordered effects and runtime evaluation stages.
- Materialize abstract bitcast operands according to overload rules. Discarded
  abstract constants need no unnecessary i32 representation; invalid source
  expressions still report errors before optimization.
- Fold constant texture offsets into legal SPIR-V constants; keep runtime
  negative indices out of constant `OpAccessChain` operands. Preserve native
  dynamic index stages without allowing invalid constant source indexing.
- Recover native collective reads with a conservative proof of private builtin
  copies, then expose those values directly in generated WGSL. Apply the proof
  only during native recovery; source WGSL private reads retain standard
  uniformity diagnostics. Conditional, partial, divergent and helper writes
  remain disqualifying.
- Accept and emit the standard `subgroups` enable directive for subgroup
  operations and entry builtins, including native inputs without source metadata.
- Pass actual buffer addresses and specialize runtime helper signatures by
  resource shape. Offline single-path helper inlining supports independent local
  storage, nested calls and Debug CIL return blocks. Allocate locals in the kernel
  entry block and initialize at each call so loops do not retain prior state.
- Decode additional native GLSL.std.450 overloads with correct scalar shapes and
  signedness, including integer min/max/clamp, ldexp and geometric operations.

For 25 byte-identical offline SPIR-V inputs preserved before the earlier
aggregate/SSA quality pass, LF-normalized UTF-8 WGSL falls from 86,513 to 34,159
bytes. On the 28 common runtime inputs, totals are Sia 46,552, Naga 45,577 and
Tint 34,782 bytes; `var` token counts are 121, 126 and 107 respectively. These
format-sensitive static measures leave substantial room to improve Sia and do
not establish GPU speed, register pressure or compile-time improvements.

## Remaining gaps

- Tint fixture `1518` passes as original WGSL but the generated structured WGSL
  exceeds Tint's nesting/chaining limit of 127. CFG structuring remains a quality
  target.
- Native SPIR-V reading still has gaps for narrow integers, packed dot-product
  capabilities, storage-image memory decorations, subgroup barrier metadata,
  ray-query state and some derivative-uniformity propagation. The evidence keeps
  unsupported inputs and diagnostics separate from successful translations.
- Twenty-eight vertex modules emitted by Tint introduce a `point_size` output
  that Sia currently retains in WGSL, where it is not a valid builtin. Producing
  text does not establish successful WGSL interoperability; these outputs fail
  independent validation and require proper target lowering or a diagnostic.
- Naga rejects original and generated runtime out-of-bounds fixtures `1563` and
  `1666`; Tint accepts both generated WGSL files. No defined value is asserted for
  these out-of-bounds accesses. A no-entry-point constant library fails the Vulkan
  executable-module validator by profile.
- The pinned Naga implements subgroup operations but explicitly rejects the
  standard `enable subgroups` directive as unimplemented. Two generated WGSL
  fixtures therefore fail its parser after standard directive emission is fixed.
  The same two original fixtures do not contain that directive. Native-input WGSL
  validation has those two parser failures, the 28 `point_size` failures and two
  runtime out-of-bounds failures; none is hidden in an all-pass total.
- Naga CLI panics on the three `pointer-function-arg*` original fixtures. These
  are recorded as upstream outcomes rather than Sia regressions.
- Private builtin promotion currently requires one fully canonical entry point
  and a single unconditional copy preceding reads/calls. Multi-entry/deferred
  variants and general CLR buffer-descriptor locals need further work.

## Verification and reproduction

Run the complete solution tests after installing the repository's SPIR-V
toolchain and .NET 10/11 runtimes. The native tests skip explicitly unless both
executables are supplied; they were supplied locally for the recorded run.

```powershell
$env:SIA_TEST_TINT = '<native Tint executable>'
$env:SIA_TEST_NAGA = '<native Naga executable>'
dotnet test Sia.Graphics.slnx -c Release --blame-hang --blame-hang-timeout 5m -m:1 -p:UseSharedCompilation=false
```

Before-fix tests reproduced abstract bitcast/index/collective failures and invalid
SPIR-V ConstOffset/dead pure work. Full-suite runs then exposed offline helper
locals and Debug return-block limitations; those tests were retained and the
implementation corrected. A new subgroup regression exposed missing standard
enable parsing/emission; that implementation was added and the test retained.
No assertions or compiler validation checks were disabled.

The evidence contains source hashes, per-fixture outcomes, build commands,
comparison harness source and exact compiler options. Recreate its console
project against `Sia.Spirv.Compiler`, remap the manifest to the pinned checkouts,
and run the recorded harness commands. Native commands are:

```text
naga --spirv-version 1.5 INPUT.wgsl OUTPUT.spv
tint INPUT.wgsl --format spirv -o OUTPUT.spv --disable-robustness [--entry-point NAME]
naga INPUT.spv OUTPUT.wgsl
tint INPUT.spv --format wgsl -o OUTPUT.wgsl
spirv-val --target-env vulkan1.2 OUTPUT.spv
naga OUTPUT.wgsl
```

The [nonzero-divisor follow-up](FuzzOptimization.md) adds three-writer integer
fuzz validation and a measured removal of redundant safety helpers. Its results
are separate from this original-source characterization snapshot.
