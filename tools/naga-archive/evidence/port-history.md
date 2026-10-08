---
status: active
---

# Plan: Managed SPIR-V and WGSL translation

## Objective and acceptance

Implement the SPIR-V/WGSL portion of reference Naga in C# under
`Sia.Graphics/Sia.Spirv.Naga`. Other shader languages are excluded. Interpret
the user's `wgpu` as WGSL, the source language accepted by WebGPU.
Acceptance requires both frontends, semantic validation, both backends,
reference-corpus comparisons, independent SPIRV-Tools validation and meaningful
roundtrip tests. Native Rust delegation does not count as the C# implementation.
Do not describe a supported subset or scaffolding as complete parity.

## Baseline

- Workspace root: `<workspace>`.
- Reference: `.reference/wgpu`, commit
  `34bf26700a92698d619a56f5b76a1aabfac4c0ab`, workspace 30.0.1.
- Target starts as a Rust Naga 30 bridge with native ABI, CLI and Wasm outputs.
  Existing SDK/compiler/WebGPU paths depend on these outputs.
- Graphics working tree was clean. Workspace and Engine have unrelated changes;
  preserve them. No nested instruction files found in Graphics or docs.
- Read docs index, architecture, development, testing, plans, writing and core
  guidance. Workspace synchronization passed once on 2026-10-07.

## Stages and progress

1. Managed BCL-only project, binary codec, typed model, layouts and structured
   diagnostics exist. Public `ShaderTranslator` exposes both translation directions.
2. Both frontends and backends operate on the shared model. Implemented structured
   control flow, simultaneous phi copies, IO wrappers, coordinate adjustment,
   readonly access, array-stride wrappers, uniform matrix-column decomposition,
   function-pointer copy-in/copy-out, integer division/remainder guards, workgroup
   initialization, common math, texture sampling/load/store/queries and integer
   atomics including 64-bit integer operations. Added packed 8-bit math,
   float-to-integer clamp bounds shared by folding and code generation, matrix
   arithmetic/conversions by column, predeclared math-result adapters, base-level
   half-texel sampling clamps, descriptor arrays and shared workgroup/buffer layout
   variants. Added subgroup vote, ballot, reductions/scans, broadcast, shuffle and
   quad operations with distinct subgroup IO mappings. Memory barriers remain
   distinct from execution barriers in SPIR-V.
3. Independent IR validation is invoked by both writers. It checks types, places,
   mutability, constructors/operators, builtin argument shapes, stage restrictions,
   call recursion, IO, control flow and returns. This is not full Naga validation:
   uniformity, aliasing, complete host layout and resource usage analysis remain.
4. Full local input scan now covers 165 WGSL and 32 SPIR-V assembly fixtures.
   Results and logs are in `.work/naga-csharp/full-corpus/manifest.json`; the plan
   records confirmed counts below. Do not treat passing syntax/validation as
   execution equivalence. Known-invalid `operators` expression (upstream TODO
   #8440) remains rejected, not weakened to improve counts.
5. Pending: remaining corpus failures, complete WGSL/SPIR-V extension and override
   handling, robustness/bounds policies, uniformity/alias analysis, broader GPU
   equivalence, AOT/browser compatibility, integration migration and cleanup.

## Decisions and blockers

Add a managed `Sia.Spirv.Naga` assembly with direct typed parse/write/translate
entry points and structured diagnostics. Use only the .NET BCL initially.
Keep the Rust bridge operational while the C# implementation is developed;
switch integration only when evidence supports compatibility. Retain upstream
license/provenance for translated source. Explicitly reject unsupported semantic
instructions rather than discard them or fabricate output.

## Verification

Passed, workspace root:

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/sync-workspace.ps1`
  (exit 0).
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup-env.ps1`
  (exit 0), SDK `11.0.100-rc.2.26461.102`, .NET 10 SDK/runtime present.

Passed, workspace root:

- `./.dotnet/dotnet.exe build Sia.Graphics/Sia.Spirv.Naga/Sia.Spirv.Naga.csproj -c Release`
  (exit 0, zero warnings/errors).
- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release`
  (latest exit 0, 328 passed, zero build warnings/errors). Binary corruption,
  storage/IO, phi copies, expression/control-flow/stage rejection, matrix layout,
  pointer calls, signed remainder, predeclared atomic results, inferred numeric
  constructors, sampling/comparison/storage/multisample roundtrips, barriers,
  bit widths, wide numeric literals, float conversion limits, packed math,
  descriptor-array addressing, atomic workgroup uniform loads, unordered float
  comparisons, NaN/Infinity bit predicates, half/wide bitcast packing, 16-bit
  integers, portable narrow/wide bit operations, extended graphics IO and
  required-result checks, storage-buffer memory decorations, diagnostic directives,
  language requirements, per-vertex fragment inputs and pipeline-sized arrays.
- `./.dotnet/dotnet.exe pack Sia.Graphics/Sia.Spirv.Naga/Sia.Spirv.Naga.csproj -c Release --no-build --output .work/naga-csharp/packages`
  (exit 0). Package inspected: managed net10.0 DLL and upstream MIT notice are
  included. NuGet reports a missing package README recommendation. Nothing published.
- Temporary reference oracle built from the exact read-only local Naga source:
  `cargo build --release --manifest-path .work/naga-csharp/oracle/Cargo.toml --target-dir .work/naga-csharp/oracle-target`
  (exit 0). Initial attempt failed due to a manifest relative-path error, fixed.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .work/naga-csharp/check-spv.ps1`:
  all seven selected existing inputs passed reference validation: collatz,
  access, boids, control-flow, operators, conversions, struct-layout.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .work/naga-csharp/check-wgsl.ps1`:
  eight passed: collatz, access, boids, control-flow, conversions, struct-layout,
  Unicode identifiers and break-from-loop. Operators rejected for the upstream
  known-invalid mixed numeric expression; do not count it as a parity pass.

Earlier corpus runs exposed missing unreachable lowering, integer-minimum
literal syntax, boolean-vector logic, non-natural array stride, pointer argument
dereference, switch selector inference and eager constant short-circuit evaluation.
Those issues were fixed before the results above. These are parsing/validation
checks, not GPU equivalence evidence.

Independent validation is available from task-local SPIRV-Tools, built from the
read-only UnrealEngine reference source. The reference snapshot lacks `CHANGES`
and `spirv.hpp11`: copied source into the task directory, supplied clearly marked
build-version metadata, and generated the missing C++11 header using the matching
reference grammar/header generator. No validator sources or reference files were
modified. Initial builds failed for these missing inputs before repair. CMake
Visual Studio 18 2026 build of `spirv-val`, `spirv-as`, `spirv-dis` succeeded.

The full scan command is
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py` from the
workspace root, after rebuilding `.work/naga-csharp/harness` in Release. It uses
the managed assembly, the exact reference oracle and `spirv-val --target-env vulkan1.1`
without relaxed layout flags. The latest confirmed scan reported:

| Stage | Passed | Rejected or failed |
| --- | ---: | ---: |
| Reference input validation | 197 | 0 |
| Managed input -> WGSL -> reference validation | 181 | 16 |
| Managed input -> SPIR-V generation, without supplied pipeline constants | 179 | 18 |
| Independent Vulkan validation of generated outputs | 178 | 1 |
| Reference validation of generated SPIR-V | 164 | 15 |
| Managed generated SPIR-V -> WGSL -> reference validation | 179 | 0 |

The remaining independent failure is `abstract-types-const`, a module with no
entry point, rejected as a Vulkan executable shader. Do not add a fabricated
entry point to hide this result. Subgroup barriers now use WorkgroupMemory with
subgroup scope and pass independent validation. Fifteen reference SPIR-V discrepancies
remain: three descriptor-array cases reject ShaderNonUniform capability; `interface`
rejects a Vulkan-required SampleMask array initializer; `math-functions` reports
MissingSpecialType for a predeclared result; `image` reports an out-of-bounds query
component; `subgroup-operations` rejects GroupNonUniformQuad capability; `int16`
reports InvalidTypeWidth(2); six half-precision IO fixtures reject StorageInputOutput16;
`draw-index` rejects DrawParameters. All fifteen pass independent Vulkan validation
and managed SPIR-V -> WGSL -> reference validation.
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-reference-gaps.py`
isolated modf, frexp, texture array layer queries and draw-index. Both managed and reference
SPIR-V pass Vulkan validation, and the reference reader rejects its own generated
SPIR-V with the same MissingSpecialType / OutOfBoundsIndex / UnsupportedCapability(DrawParameters) errors. These four
minimal reproductions establish reference reader limitations for those cases.
Keep each validator's evidence rather than changing output to satisfy one oracle.

Dynamic descriptor-array indices are conservatively marked non-uniform; this can
require more device features than a future uniformity analysis would. Buffer binding
array elements currently require structures; uniform binding arrays needing matrix
layout conversion are explicitly unsupported. Float
atomics, specialization-sized arrays, some subgroup instructions, task/mesh/ray features,
diagnostics, member/per-access memory decorations and per-vertex IO remain incomplete. GPU equivalence
must cover wide signed/unsigned casts, rounding/NaNs and constant/runtime behavior;
syntax and structural validation cannot establish those semantics.
NonSemantic.Shader.DebugInfo.100 metadata is accepted at module/function scope,
including after terminators, and omitted from generated shader semantics.
SPIR-V unordered comparisons now retain NaN acceptance, and ordered not-equal
excludes NaNs. WGSL classification uses bit masks rather than unavailable isNan /
isInf builtins; half values widen before classification. Literal half NaNs/Infinity
use packed bitcasts. `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-special-floats.py`
passed 24 independent generation/validation/translation checks for f32, vec2f,
f16 and f64 unordered comparisons. This does not establish GPU execution results.

`Proc.PipelineConstantResolver.Resolve` validates, clones and resolves override
defaults and their dependency expressions. Numeric API values use explicit decimal
IDs when present, otherwise names. It resolves global initializers and workgroup
dimensions, preserves local shadowing, rejects unknown/missing values and applies
Naga's scalar conversion rules (including bool NaN/infinity and integer truncation).
`SpirvWriteOptions.PipelineConstants` invokes this pass; null retains direct scalar
SPIR-V specialization emission, which requires defaults. Required WGSL overrides
are no longer silently assigned zero. Unnumbered direct specializations receive
unused IDs without colliding with explicit IDs. The SPIR-V reader treats undecorated
specialization scalars as fixed defaults and composite specializations as dependent
expressions, rather than illegal vector overrides. Specialization expression opcodes
and specialization-sized arrays remain unsupported.

`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-pipeline-constants.py`
passed 78 checks across ten fixtures (including both upstream override samples),
managed/reference generation, independent Vulkan validation, roundtrip parsing
and four invalid-value cases. The first run had two harness-only failures because
.NET numeric parsing did not accept Rust's `inf` spelling; normalized the temporary
test argument to `Infinity` and reran successfully. No production conversion change
was needed. `check-specialization.py` passed 16 checks for assigned IDs, a mutated
OpSpecConstantComposite and undecorated specialization defaults. QuadSwap direction
was initially emitted as a literal; the independent validator rejected ID zero.
Both readers/writers now use the direction constant ID and the full subgroup fixture
passes. Workgroup sizes preserve concrete i32/u32 types. Dynamic indexing first
materializes abstract arrays/vectors; the upstream const-exprs case now passes.

16-bit integer types, constants, casts, pipeline values, storage and subgroup
operands now use typed short/ushort payloads. WGSL parsing requires
`enable wgpu_int16`; output discovers required f16/int16 enables without mutating
the input module. Narrow integer bitcasts use packed 32-bit words and generated
WGSL helpers, evaluating runtime arguments once; constant expressions remain
inline. Constant and runtime bitfield operations clamp their offset/count.
Independent Vulkan validation initially rejected 16-bit FindMSB and BitCount:
GLSL find operations and Vulkan bit-count/reverse/field operations require 32-bit
words here. Narrow/wide variants now use core binary search or 32-bit word
decomposition, without enabling relaxed validator options. Stage IO using
16-bit components declares StorageInputOutput16. Integer inter-stage IO requires
explicit flat interpolation. Literal payload widths and constant division by
zero are validated; enable directives must precede global declarations.

`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-int16.py`
reported 56/64 passing checks across eight fixtures. All eight generated SPIR-V
outputs pass independent Vulkan validation and all eight roundtrip WGSL outputs
pass reference validation. Eight oracle checks remain failed: five reference
SPIR-V reads report InvalidTypeWidth(2); the custom narrow-bitcast input fails
reference validation and its SPIR-V read reports InvalidStoreTypes; the 16-bit IO
read reports UnsupportedCapability(StorageInputOutput16). Keep these failures
visible. The reference reader only decodes 32/64-bit integer constants, and its
own 16-bit IO output also encounters the capability rejection. The temporary
oracle now explicitly emits SPIR-V 1.3 to match the managed writer; its former
default 1.0 output could not encode subgroup instructions. Pipeline constant
checks were rerun with this oracle and still pass 78/78.

Graphics IO now supports view_index, draw_index, clip_distances and both
barycentric modes, including SPIR-V capabilities/extensions and WGSL enables.
The SPIR-V reader adapts signed 32-bit builtin index inputs to unsigned WGSL
interfaces while bitcasting back into the original private storage type.
It culls unused glslang output-block point-size/clip/cull bindings after all
functions are parsed. Member access in helpers and whole-aggregate loads/stores
retain those outputs. WGSL emits member IO attributes only for entry interface
structures. Eighteen new tests cover valid boundaries, missing enables, invalid
types/stages/directions, signed adaptation and helper/aggregate output use.

WGSL @must_use is checked on function declarations and standalone calls, including
forward calls; void/argument-bearing/duplicate/misplaced annotations are rejected.
Constructors and non-atomic value builtins require their results to be used or
explicitly phony-assigned. Unannotated user functions and atomic operations retain
implicit discard, matching the reference. This source-only rule does not add IR
metadata, just as reference Naga omits it after frontend lowering. WGSL output
explicitly phony-assigns non-void IR Evaluate expressions. Eighteen new tests and
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-must-use.py` passed
54/54 comparison checks: four valid fixtures through both writers, independent
Vulkan/reference validation and roundtrip; eleven invalid fixtures rejected by
both managed and reference frontends.

Storage-buffer globals now retain coherent/volatile flags in shared IR. WGSL
attributes and SPIR-V variable decorations 23/21 roundtrip without dropping these
requirements, including through override resolution and layout cloning. Validation
rejects non-storage placement, attribute arguments and unknown IR bits. Repeated
memory attributes are idempotent, matching reference Naga. Fourteen new tests pass;
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-memory-decorations.py`
passed 52/52 independent checks across both upstream fixtures, duplicate/read-only/
atomic variants and six rejection cases. A temporary test initially used a
nonexistent WriteBinary API; corrected it to parse the existing byte writer.
Per-member and per-access SPIR-V memory semantics remain separate pending work.

### GPU execution evidence

Desktop device: Intel UHD Graphics, Vulkan. Direct Vulkan feature queries report
shaderFloat16, shaderInt16 and shaderInt64 supported; shaderFloat64 unsupported.
All fixtures use task-owned non-production buffers and bounded compute dispatches.

Passed, workspace root:

- `./.dotnet/dotnet.exe run --project .work/naga-csharp/gpu-runner/Runner.csproj -c Release -- --mode native --filter naga/integer/ --output .work/naga-csharp/gpu-integer-direct-spv.json`
  exited 0: six paths passed (original WGSL, managed WGSL, managed/reference SPIR-V
  read back to WGSL, direct managed/reference SPIR-V). Each checks 257 inputs,
  15 integer results and four output sentinels against independent CPU results.
  Includes zero/minimum/-1 division, remainders, leading/trailing bits, population,
  reversal, clamped bitfields and signed extraction.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-vulkan-compute.py`
  exited 0: direct managed/reference 32-bit integer and managed packed f16 SPIR-V
  each have zero mismatches. The system Vulkan loader receives SPIR-V directly,
  with no intervening shader translator. Half checks retain finite values and
  signed zeros and compare both packed words and widened components.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-wide-vulkan.py`
  exited 0: scalar int16/int64, each 257 inputs and 15 results, zero mismatches.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-vector-vulkan.py`
  exited 0: vec2 int16/int64, same operations and inputs, zero mismatches in both
  components. These scripts independently validate generated SPIR-V with Vulkan
  1.1 SPIRV-Tools before dispatch. Reports include actual/expected words and hashes.

Failed/blocked evidence retained:

- The `sia-gpu-diagnostics` controller native run against the current Engine
  checkout failed to compile because its default renderer fixtures reference
  removed GpuResources/PbrFrame/IblEnvironmentAsset types. No production/skill
  sources or default assertions were changed. The task-only runner links the
  existing catalog/assertion/report contracts and uses minimal owned WebGPU
  buffers instead of renderer fixtures. Initial standalone build had an unused
  Sia.Math import, removed before successful execution.
- `./.dotnet/dotnet.exe run --project .work/naga-csharp/gpu-runner/Runner.csproj -c Release --no-build -- --mode native --filter naga/half/ --output .work/naga-csharp/gpu-half-direct-spv.json`
  exited 1: four ERRORs in wgpu/Naga shader reading and two reference-generation
  BLOCKED cases. Reference WGSL bitcast lowering loses the scalar/vector width
  change; native wgpu SPIR-V reading also rejects that packed f16 access. Do not
  count these as passes. Independent validation and direct Vulkan execution of
  the same managed SPIR-V pass. No output rewrite or assertion was weakened to
  accommodate the reference defect.

Not run: broader GPU/image equivalence, browser/AOT, dependent solution checks,
integration migration. The current code is an incomplete managed implementation;
the existing Rust bridge remains in use by consumers.

## WGSL diagnostics, directives and per-vertex IO

The C# reader now accepts module `diagnostic` directives and function
`@diagnostic` attributes with off/info/warning/error severity, standard/unknown
rule names and namespace-qualified names. Identical module directives are
idempotent; conflicting module severities and duplicate same-rule function
attributes are rejected. Module and function lists remain separate so a function
can override the module rule. Both WGSL layout lowering and pipeline resolution
retain the settings. WGSL writing preserves them; SPIR-V intentionally omits them,
as the reference does. Direct IR metadata is validated before writing.

Do not turn stricter WGSL uniformity rules into reference-parity requirements.
The pinned `valid/analyzer.rs` sets
`DISABLE_UNIFORMITY_REQ_FOR_FRAGMENT_STAGE = true`, disabling derivative and
implicit sample requirements. WorkgroupUniformLoad control-flow rejection is
explicitly commented out. Barrier statements record requirements but the analyzer
does not reject them at the statement or function-call site. Minimal divergent
derivative, barrier, helper-barrier and uniform-load probes all pass the exact
reference oracle and the managed readers/writers. Compound-statement diagnostic
attributes are explicitly unimplemented in the reference parser and remain
rejected here. Unknown-rule warnings are not surfaced as a separate warning stream
by the current C# parse API; settings are retained.

Implemented `requires` names match the pinned reference:
readonly_and_readwrite_storage_textures, packed_4x8_integer_dot_product and
pointer_composite_access. As upstream does, parsing checks these and discards the
directives. Unknown and unimplemented requirements are rejected. `enable` names
are checked against the pinned implemented-name list; enabling an extension
does not imply its entire shader feature has been ported. `enable subgroups`
remains rejected as upstream does; existing subgroup operations do not need it.

Per-vertex fragment inputs accept array<T,3> where T is a numeric scalar/vector,
including int16/f16 and structure members. WGSL reading requires
`enable wgpu_per_vertex`; writing discovers it from IO metadata. SPIR-V reading
retains PerVertexKHR; writing emits that decoration with FragmentBarycentricKHR
and SPV_KHR_fragment_shader_barycentric. Sixteen-bit array IO also emits
StorageInputOutput16. Invalid lengths, element types, stages and explicit sampling
are rejected. The reference currently rejects even explicit center sampling
through its interpolation/sampling combination check. Per-vertex output arrays
are rejected by C# as invalid fragment interfaces; no GPU fragment execution has
been performed for this feature.

Passed, workspace root:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  exit 0, 301 passed, none failed/skipped, zero build warnings/errors.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  exit 0, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-diagnostics.py`:
  exit 0, 136/136 checks. Includes source/reference validation, managed WGSL
  validation, generated SPIR-V independent/reference validation, roundtrip
  validation and malformed/conflicting/directive-placement rejection checks.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  latest exit 0, full 197-input scan; confirmed counts are in the verification
  table. Compared with the preceding 178-WGSL baseline, diagnostic-filter and
  both per-vertex fixtures now pass. Remaining 16 managed WGSL failures are ray
  query/pipeline, float atomics, atomic textures, cooperative matrix, mesh/task
  features, external texture and the known-invalid mixed numeric operators case.
  Pipeline-constant-dependent outputs still need supplied values.

Failed checks retained:

- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-per-vertex.py`:
  exit 1, 129/133 checks pass. Four 16-bit per-vertex SPIR-V outputs are rejected
  by the reference reader with UnsupportedCapability(StorageInputOutput16).
  All four independently pass Vulkan 1.1 SPIRV-Tools and managed SPIR-V -> WGSL ->
  reference validation. Scalar/vector, integer/float, direct/structure and the two
  upstream per-vertex fixtures pass their other checks. Keep the four failures
  visible; do not remove a Vulkan-required capability to satisfy the oracle.

## Pipeline-sized arrays

`ShaderType.Array.OverrideLength` and `ShaderType.BindingArray.OverrideLength`
retain the name of an integer override separately from a fixed or runtime length.
WGSL parsing retains direct overrides and synthesizes a dependent override for
compound length expressions. Module validation checks the reference type/usage
rules: data arrays with pending lengths are workgroup globals, pending arrays
cannot be array elements or structure members, ordinary source local declarations
and return types must be constructible, and resource binding arrays can have
pending lengths. Nonpositive defaults are accepted before pipeline creation and
rejected when selected; a supplied valid value can replace them. Unresolved data
layout queries explicitly fail rather than return a guessed size.

PipelineConstantResolver now maps types as well as values across structures,
constants, globals, parameters, local declarations and every expression variant.
This retains pointer/load/call type agreement and leaves the original module
unchanged. Resolving the length requires a positive u32 count and checked layout.
SpirvWriter requires PipelineConstants (an empty dictionary selects all defaults)
for pending array types. It does not silently bake defaults into specialized
arrays. The reader retains an OpTypeArray length referring to an OpSpecConstant
with SpecId. A SPIR-V null array value may be retained until pipeline resolution;
the WGSL frontend still rejects pending-length array construction.

The reference validator only permits Function/Private pointer parameters. A first
probe incorrectly assumed Workgroup pointer parameters were accepted; the oracle
rejected that assumption. C# now enforces the same restriction, with explicit
negative coverage. An unused Private pointer parameter and by-value pending-array
parameter are accepted and their types resolve correctly.

Unresolved WGSL writing emits valid array<T,override> syntax. The pinned reference
WGSL writer omits the element type/comma for direct pending lengths (array<n>) and
panics while writing a compound-length override initializer. These outputs are
retained in override-array-probes, not accepted as output validation. C# inlines a
single-use array snapshot into an immediately following single-argument call,
preserving evaluation order. More complex pending-array snapshots, including
multiple arguments or intervening writes, still require pipeline resolution;
they are explicitly rejected by WGSL writing rather than moved unsafely. General
unresolved snapshot emission remains work for full parity. The next section
records subsequent core SpecConstantOp reader support; direct emission of
dependent specialization expressions remains incomplete.

Passed, workspace root:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  exit 0, 328 passed, zero failed/skipped and zero build warnings/errors.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  exit 0, zero build warnings/errors.

Failed checks retained:

- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-override-arrays.py`:
  exit 1, 112/114 checks pass. The reference SPIR-V reader rejects one fixed
  binding-array image output as InvalidImage and one independently valid
  specialized-array input as InvalidArraySize(3). Both pass Vulkan 1.1 validation;
  managed resolution/roundtrip WGSL and native resolved output checks pass.
  Earlier failures include the incorrect Workgroup pointer assumption and the
  subsequently repaired unresolved by-value WGSL snapshot case.

GPU evidence uses the existing task-only direct Vulkan runner described above,
without production or skill changes. Ordinary and atomic arrays use a dependent
count=n+1, five groups and workgroup sizes 3/17/32. CPU expectations independently
sum input words after lane/group XOR and check untouched array cells and output
sentinels. The report records source artifacts, SPIR-V hashes, device/features and
actual/expected words. Reference outputs fail the unchanged independent validator
on VUID-StandaloneSpirv-None-10684: explicit array layout decoration on Workgroup
variables. They are not dispatched as valid shaders and are not counted as passes.
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-override-array-vulkan.py`
exited 1 with 12 managed GPU passes and six reference validation failures.
Both direct WGSL -> resolved SPIR-V and WGSL -> managed WGSL -> resolved SPIR-V
paths have zero mismatches for all six cases. Reports/logs are in
`.work/naga-csharp/override-array-gpu/`; reference failures remain in the report.
The managed assembly SHA256 for this verification is
`0FB9B8B53FA9EA0E618DC69F2DF6CE2BF9AE35234EEECD50DA842594E04E1E94`.

`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`
exited 0 after the final production edits. The 197-input counts remain unchanged:
181 WGSL/reference passes, 179 generated SPIR-V, 178 independent Vulkan passes,
164 reference SPIR-V passes with 15 known discrepancies, and 179 managed
SPIR-V -> WGSL/reference passes. Pipeline-sized arrays are covered by the new
targeted checks and GPU cases rather than added to the upstream fixture counts.
Root and Graphics `git diff --check` exit 0; root emits existing CRLF notices.

## Core specialization operation translation

The reader now handles core Shader specialization operations: integer/float
width conversion, half quantization, modular integer add/subtract/multiply and
negation, signed/unsigned division and remainder (including divisor-sign SMod),
shifts, bitwise/logical operations, comparisons, select, composite extraction/
insertion and vector shuffle. Operand signedness is reinterpreted according to
the instruction, including valid integer arithmetic with differently signed
operand types. Composite select is lowered component by component. Fixed array
length expressions are folded; dependent lengths retain a named override and
reuse it across array types. Kernel-only operations remain explicitly rejected;
cooperative-matrix extension specialization operations remain unimplemented.

Modular operations use half-width limbs so every unsigned intermediate fits its
type under WGSL constant evaluation. Simple replacement with ordinary arithmetic
would incorrectly reject SPIR-V overflow. Unit expectations use independent
BigInteger modulo arithmetic at widths 16/32/64, scalar and vec2, over boundary
pairs. Additional tests cover logical operations, integer conversion signedness,
float conversion/quantization, composite operations, API replacement, derived
array lengths, malformed operand counts/IDs/indices and unsupported operations.

WGSL overrides reject i64/u64, matching the pinned validator's explicit scalar
list in naga/src/valid/mod.rs. Shared IR still retains SPIR-V wide specialization
values. WgslWriter requires PipelineConstantResolver resolution first for those
values. Minimum i64 emission now uses i64(9223372036854775808lu); the prior
subtraction expression fails the reference WGSL frontend. Ordinary constant i64
values and resolved wide specialization expressions roundtrip successfully.
Pure expression trees may duplicate nested specialization dependencies; compact
DAG handling and broader dependency-depth coverage remain pending. Dependent
override defaults and pending arrays still require resolution before SPIR-V
writing. No public API, dependency, Rust bridge or consumer migration changed.

Passed from the workspace root after the final production changes:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  exit 0, 372 passed, zero failed/skipped and zero build warnings/errors.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  exit 0, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0; the 197-input counts remain 181 WGSL/reference passes, 179 emitted
  SPIR-V, 178 independent Vulkan passes, 164 reference SPIR-V passes and 179
  managed/reference roundtrip passes. Existing failures are unchanged.

Targeted independent/GPU check:
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-specop-vulkan.py`
exits 1 and retains all failures in specop-gpu/report.json. All three original
16/32/64-bit inputs and all nine managed output variants pass unchanged
SPIRV-Tools Vulkan 1.1 validation. For 16/32, generated unresolved WGSL passes
reference validation. For 64, unresolved writing explicitly rejects wide
overrides; resolved WGSL passes reference validation, reference SPIR-V writing
and independent validation. The pinned reader rejects Int16 type width and
32/64-bit SpecConstantOp inputs. The reference process_overrides pass reports
ExpressionAlreadyInScope for the 16/32-bit generated WGSL; these are failures,
not successful equivalent paths.

GPU comparisons use independent Python integer expectations and untouched
sentinels. Original, managed direct SPIR-V, managed resolved SPIR-V and resolved
WGSL roundtrip each have zero mismatches at widths 16 and 64; the reference
resolved-WGSL path also passes at 64. All four 32-bit paths have the same two
mismatched words. Investigation with
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-specop-failures.py`
and minimize-specop-gpu.py reduced this to nine ordinary constant stores:
outputs[24]=1, [25]=0, [26]=1, [27]=0, [120]=2147483649, [121]=0,
[122]=2147483649, [123]=0, [124]=2147483648. Both C# and reference WGSL-to-SPIR-V
outputs pass independent validation but read back 2147483648 at indices 120/122
on Intel UHD Graphics. Removing the early pair or using input-buffer values
avoids the error. This evidence points to a device/compiler optimization defect;
the failing GPU paths remain failed. No production workaround, test weakening
or reference/validator modification was applied. Minimal source, binaries and
readback evidence are retained in specop-probes while the task remains active.

The managed assembly SHA256 for this verification is
`FBB4A856523F6A0BBF8380FD12B1BD9BDFD3F2E3C441BA18889B256922209491`.

## Specialization dependency graph and unresolved array emission

The next increment addresses the repeated expression expansion observed above.
Before the change, one/two/three/four dependent IAdd instructions emitted
194/605/1838/5537 bytes of WGSL. The same probe now emits
217/377/537/697 bytes. The original measurements are in tool output; the probe
was rerun and its task-local report overwritten, so the file named baseline.json
contains the newer measurements. after.json is the current probe output.
Do not mistake that task filename for preserved before-change evidence.

ShaderConstant now has IsSpecialization metadata for derived scalar values.
It is mutually exclusive with IsOverride and OverrideId, requires an initializer,
and cannot be independently supplied through PipelineConstantResolver's API.
The reader folds override-independent operations and captures scalar results;
composite results are reconstructed from captured scalar components. Named
references preserve the dependency graph without changing its base parameter
identities. Override-expression validation memoizes expressions/constants within
one module validation and distinguishes expression nesting from dependency
edges; cyclic dependencies still fail. Resolution clears the derived metadata.
WGSL writing emits generated override declarations for derived scalar aliases:
WGSL has no syntax for a non-overridable alias of an override expression.
Reparsing that text makes those generated names ordinary WGSL overrides; callers
should keep the original IR when using pipeline parameter identities.

The SPIR-V backend emits derived core operations as SpecConstantOp and composite
values as SpecConstantComposite. Integer signedness reinterpretation uses IAdd
with zero because SpecConstantOp Bitcast requires Kernel. Unsigned width
conversion uses SConvert plus a low-bit mask for widening, preserving zero
extension in SPIR-V 1.3 without requiring an additional extension. Private global
initializers may use the same specialization expressions. Independently
overridable WGSL declarations with dependent defaults still require resolution:
SPIR-V SpecId cannot decorate a derived SpecConstantOp, so assigning one would
misrepresent that API parameter. Shader-disallowed operations, including float
arithmetic, still explicitly require resolution.

Pending workgroup and binding-array lengths now retain their specialization ID
in OpTypeArray. Anonymous compound WGSL array lengths use IsSpecialization and
cannot be independently supplied through the original IR API. Default lengths
must be positive and fit u32 before unresolved emission; invalid defaults require
explicit replacement through PipelineConstants. Unresolved array output does not
silently fix the length at its default. Ordinary arrays initialize with a null
aggregate; atomic arrays use a structured loop bounded by the specialization
value. Function variables for counters precede all entry-block instructions.

A valid unresolved by-value array output exposed an IR restriction: the SPIR-V
reader's mutable SSA array temporaries were rejected as WGSL local declarations.
The shared IR now permits these specialization-sized data temporaries and the
SPIR-V writer preserves them. WGSL source still rejects explicit pending locals,
with retained negative tests, and the WGSL writer explicitly requires resolution
for mutable pending temporaries. This does not move snapshots across writes.

Current focused suite:
`./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`
passes 384 tests, zero failed/skipped, zero build warnings/errors. Added tests
cover 512-level scalar/vec2 chains with linear IR/text/binary size, modular
resolution, one retained SpecId, derived API identity rejection, metadata/cycle
failures, unresolved ordinary/atomic/binding arrays and by-value array SPIR-V
temporaries with unchanged WGSL source rejection. The harness build also passes
with zero warnings/errors.

The task-only Vulkan runner now accepts VkSpecializationInfo payloads, retaining
the native map/data structures until pipeline creation completes. This change
does not affect production or the diagnostics skill. Evidence:

- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-specop-dag-vulkan.py`:
  exit 0, 220/220 checks, including 64 GPU comparisons. 128-level scalar/vec2
  chains use defaults and an explicit parameter causing integer wraparound.
  Twelve signed/unsigned conversion fixtures cover all width pairs among
  16/32/64 with unsigned result types. Original SPIR-V, unresolved managed output
  with native specialization, explicitly resolved output and reference-compiled
  resolved WGSL each match independent CPU words and untouched sentinels.
  Every dispatched binary passes independent validation (Vulkan 1.2 for original
  UConvert specialization inputs, Vulkan 1.1 for all managed outputs).
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-unresolved-array-vulkan.py`:
  exit 0, 57/57 checks, including 27 GPU comparisons. Ordinary/atomic dependent
  lengths and direct-length by-value parameters use values 5/17/32, five groups,
  direct unresolved output, managed SPIR-V roundtrip and resolved output. CPU
  sums, zeroed unwritten cells and sentinels all match. The initial by-value
  roundtrip failure motivated the IR correction above and is retained in prior
  tool output; the final report records the repaired run.
- The core specop GPU script rerun after graph capture no longer triggers the
  reference ExpressionAlreadyInScope errors at 16/32 bits: both reference
  compilation paths and their independent validation pass. It still exits 1:
  the original reference-reader limitations and the independently reproduced
  Intel constant-store defect remain. Its GPU summary is 10 passed, five failed
  paths; all five 32-bit paths share the two bad words, including reference output.

Temporary scripts, fixtures, independent tools and reports remain task-owned
active inputs. No task services or worktrees were created. Full parity and
consumer migration remain incomplete; completion cleanup is not yet triggered.

Final production verification additionally passed
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-unresolved-binding-arrays.py`:
exit 0, 18/18 checks. Image, sampler and uniform-buffer binding arrays retain
their specialization lengths through unresolved output, managed SPIR-V roundtrip
and explicit resolution; all three variants of each pass Vulkan 1.1 validation.
The final DAG and unresolved-array GPU scripts were rerun after the default
length check was added; both exit 0 with the counts above.
`D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`
exits 0 after the final production edits. The 197-input counts remain unchanged:
181 WGSL/reference passes, 179 emitted SPIR-V, 178 independent Vulkan passes,
164 reference SPIR-V passes and 179 managed/reference roundtrip passes. Existing
failures remain in the manifest. Root and Graphics git diff --check pass; root
retains the existing CRLF notices. The final managed assembly SHA256 is
`F4924511635CBF3CA7AE267DA43C7A79ADF2C529E65961723C7BF0C197297490`.

## Float32 atomic translation

Added `atomic<f32>` to the WGSL frontend and retained the reference's precise
atomic scalar widths in shared validation. Float read-modify-write operations
allow add, subtract and exchange only in storage memory; load/store also remain
valid in workgroup memory. Unsupported float operators, compare/exchange and
16/64-bit float atomics are rejected. SPIR-V output declares
`AtomicFloat32AddEXT` and `SPV_EXT_shader_atomic_float_add`; subtraction lowers
to `OpFNegate` followed by `OpAtomicFAddEXT`. The reader recognizes the extension,
upgrades float scalar/array/structure memory paths and rejects integer-only
atomic instructions with float operands.

Verification:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  401 passed, zero failed/skipped. Seventeen new cases cover nested storage
  paths, subtraction, workgroup load/store, invalid operators/spaces/widths and
  a deliberately mistyped SPIR-V atomic opcode.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  passed, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-float-atomic.py`:
  reference admission probe passed. Its 16 storage/workgroup cases establish
  operation restrictions before implementation.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-float-atomics.py`:
  exit 0, 93/93 checks. Managed and reference SPIR-V pass independent Vulkan 1.1
  validation; both convert to reference-valid WGSL. The upstream
  `atomicOps-float32.wgsl` fixture also passes SPIR-V and WGSL roundtrips from both
  producers. Reports remain in `.work/naga-csharp/float-atomic`.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-float-atomic-device.py`:
  query passed. Intel UHD Graphics exposes `VK_EXT_shader_atomic_float` and
  float32 buffer/shared atomic load/store/exchange, but reports
  `shaderBufferFloat32AtomicAdd = false` and
  `shaderSharedFloat32AtomicAdd = false`. Float atomic GPU execution was not run:
  the emitted capability requires atomic addition, which this device lacks.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0. Of 197 reference-valid inputs, 182 managed WGSL outputs pass reference
  validation (15 unsupported), 180 managed SPIR-V outputs are emitted (17
  unsupported), 179 pass independent Vulkan validation (one pre-existing
  entrypoint-free input), 165 pass the reference SPIR-V reader (15 existing
  reference-reader discrepancies), and all 180 managed WGSL roundtrips pass the
  reference validator. This adds the float atomic fixture at each successful
  stage without changing the existing failures.

Full parity, GPU coverage on a device supporting float atomic addition, and
consumer migration remain incomplete. Temporary tools and reports remain active
task inputs; no task-owned services or worktrees were created.
Root and Graphics `git diff --check` pass; root retains existing CRLF notices.
Final managed assembly SHA256:
`89BA4E0A40CC6B19EE9E04DAC1B4B71673AD6AAE28D5F37E70FAA88F808253EC`.

## Storage-image atomic translation

Added `StorageAccess.Atomic = 4`; the WGSL `atomic` spelling represents
read/write plus this bit, including reference-supported buffer access spelling.
Image atomics retain the reference's global/binding-array origin requirement,
integer coordinate shape, exact explicitly typed scalar values and operation
restrictions: r32sint/r32uint support add/min/max/and/or/xor; r64uint supports
min/max. Other formats may declare atomic access but cannot perform an atomic
operation, matching the reference. Load/store remain usable with atomic access.

SPIR-V writing uses Image-storage-class texel pointers to the selected image or
binding-array element, preserving descriptor indexing and nonuniform decoration.
The r64uint image type uses `Int64ImageEXT`/`SPV_EXT_shader_image_int64` and atomic
operations additionally require Int64Atomics. Reading handles texel pointers,
copied pointers, array layers and image permissions. Unsupported image opcodes,
signedness/pointee mismatches and consumed atomic old-value results are rejected:
WGSL texture atomics cannot represent a returned value. Image pointer metadata
stays internal; no new public pointer address space was added.

Mixed-width array layers are resized before coordinate packing. WGSL output
folds constant numeric conversions, avoiding reference-invalid forms such as
`u32(0lu)` after SPIR-V width conversion while retaining truncation semantics.

Verification:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  443 passed, zero failed/skipped. Forty-two new cases cover each operation/type,
  dimensions, binding arrays, atomic load/store access, invalid calls/formats,
  an independently constructed SPIR-V input, copied pointers, consumed return
  values and boundary-value constant width conversion.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  passed, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-image-atomic.py` and
  `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-image-atomic-extra.py`:
  reference admission evidence covers formats/access, load/store, abstract
  operands, buffer atomic access and the texture-parameter restriction.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-image-atomic-fixtures.py`:
  exit 0. Both upstream atomic texture fixtures compile using each producer;
  managed conversion of both producers' binaries yields reference-valid WGSL,
  and emitted/re-emitted SPIR-V passes independent Vulkan 1.1 validation.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-image-atomics.py`:
  exit 0, 423/423 checks. Covers the admission matrix; 1D/2D/2D-array/3D images;
  all width pairs among 16/32/64-bit coordinates and layers; constant/dynamic
  descriptor indexing; and an independently valid SPIR-V atomic result which
  is deliberately consumed and must produce a translation error.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-image-atomic-vulkan.py`:
  exit 0, 52/52 checks including 16 GPU comparisons on Intel UHD Graphics.
  Six image operations execute with 16 competing lanes. Signed/unsigned 2D and
  2D-array variants compare direct managed SPIR-V, original reference output,
  managed SPIR-V roundtrip and reference compilation of managed WGSL against
  independent CPU expectations. Image and buffer readbacks both match, including
  untouched pixels/layers and buffer sentinels. Every dispatched binary passes
  independent validation. The task-only ctypes runner uses system Vulkan and
  fields checked against local Vulkan 1.4.324 headers; image ownership, barriers
  and readback are independent of the translator.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-image-atomic-device.py`:
  query passed; the device lacks `VK_EXT_shader_image_atomic_int64` and reports
  shaderImageInt64Atomics false. 64-bit image atomic GPU execution was not run.
  1D/3D images and binding arrays also have static/roundtrip evidence only.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0. All 197 inputs pass reference validation. 184 managed WGSL outputs
  pass reference validation (13 unsupported inputs), 182 SPIR-V outputs are
  emitted (15 unsupported), 181 pass Vulkan validation (the existing no-entry
  module remains invalid for Vulkan), and all 182 managed WGSL roundtrips pass
  the reference validator. Reference SPIR-V reading passes 165 and fails 17;
  the two new image fixtures hit upstream UnsupportedStorageClass(11) and
  UnsupportedCapability(Int64ImageEXT), while managed reading and independent
  validation pass.

Final assembly SHA256:
`2B153444A3762906206F290112CFD6CDA9F0E9928B188AFD4EF8EA32BF5F2739`.
Root and Graphics `git diff --check` pass; root retains the existing CRLF notices.
No task-owned services or worktrees. GPU runners/reports remain active task
inputs and still need promotion to maintained regressions before final cleanup.
Full parity and consumer migration remain incomplete.

## Complete reference storage-image format mapping

The format audit found 27 of the reference's 41 WGSL storage formats could be
compiled by the managed implementation. Added the missing 14 formats and replaced
the separate reader/writer switches with one internal format/component table.
WGSL parsing and shared IR validation now reject unknown formats and component
types which disagree with the declared format. SPIR-V parsing checks the sampled
component against the typed storage format. The public image representation and
dependencies are unchanged.

All 40 typed SPIR-V formats roundtrip through managed readers/writers and
reference-valid WGSL. BGRA8 uses SPIR-V Unknown, matching reference output; its
binary does not contain a recoverable WGSL format. Reading reports this ambiguity
explicitly. BGRA writes declare StorageImageWriteWithoutFormat and reads declare
StorageImageReadWithoutFormat. The baseline reference compiler emits both BGRA
operations but omits the latter capability: unchanged SPIRV-Tools rejects that
reference output. Managed output supplies both and passes independent validation.
The reference failure remains in `.work/naga-csharp/storage-formats/baseline.json`;
the original 27/41 managed result is also preserved there.

Verification:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  497 passed, zero failed/skipped. Fifty-four new cases cover all 41 mappings,
  scalar types, extended-format/formatless capabilities, unknown formats and
  deliberately mismatched IR/SPIR-V image declarations.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  passed, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-storage-formats.py`:
  baseline audit, 41 reference-valid formats, 27 managed compilations before
  edits, 41 reference compilations, with the BGRA native validation failure above.
  Do not rerun this baseline script over its retained pre-change report.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-storage-formats.py`:
  exit 0, 405/405 checks. All 41 managed emissions pass Vulkan 1.1 validation
  and canonical WGSL passes the reference. All 40 typed formats additionally
  pass managed SPIR-V roundtrip, independent revalidation and reference validation
  of WGSL translated from both managed and original reference binaries. The
  formatless BGRA reverse-translation error is checked as an explicit limitation.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-storage-format-device.py`:
  Intel UHD Graphics supports storage-image read/write for 39 formats. It lacks
  r64uint image support and formatless read support, but supports BGRA8 formatless
  writes. Device format/feature values are retained in
  `.work/naga-csharp/storage-formats/device-formats.json`.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-storage-formats-vulkan.py`:
  exit 0, 513/513 checks, including 158 GPU comparisons over 40 formats.
  For each of 39 typed formats, direct managed output, reference output, managed
  SPIR-V roundtrip and reference recompilation of managed WGSL agree with
  independent CPU expectations. Sixteen pixels exercise component patterns;
  buffer readback includes default absent channels and untouched sentinels;
  raw image bytes check channel order, integer storage, normalized 8/16-bit
  formats, half floats, RGB10A2 and RG11B10 packing. BGRA8 validates writes and
  raw image bytes for both producers, without dispatching unsupported reads.
  Every dispatched binary passes independent validation. r64uint is explicitly
  skipped for device support; its static/roundtrip checks above still pass.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0 after final production edits. Counts remain 197 reference-valid inputs,
  184 reference-valid managed WGSL outputs, 182 SPIR-V emissions, 181 independent
  Vulkan passes, 165 reference SPIR-V passes and 182 reference-valid managed
  WGSL roundtrips. Existing failures and unsupported extensions are unchanged.

Final assembly SHA256:
`01844077B87AE8793B5515FAC3EA0ADBD23E17BFEDB68A59E7A0AF65F081059F`.
Root and Graphics `git diff --check` pass, with the existing root CRLF notices.
No task services or worktrees. Temporary format runners, baseline and readbacks
remain active evidence and must be promoted/preserved before completion cleanup.
Full parity and consumer migration remain incomplete.

## Workgroup specialization follow-up, 2026-10-08

The remaining ordinary `operators.wgsl` failure is caused by its two deliberately
invalid mixed-type short-circuit operands (reference issue 8440). A task copy
removing only those lines passes managed emission, native Vulkan 1.1 validation
and reference WGSL validation. The original fixture and strict typing remain
unchanged; the full-corpus failure is retained.

Unresolved compute workgroup dimensions now emit a specialized `BuiltIn
WorkgroupSize`, with positive default `LocalSize` values. Equal dimensions in
multiple entries share one decorated composite. Distinct entry dimensions require
explicit pipeline resolution or the new `SpirvWriteOptions.UseLocalSizeId` option.
The option emits `OpExecutionModeId LocalSizeId`; callers need maintenance4
(Vulkan 1.3) or equivalent target support. This public option was explained before
addition. The SPIR-V reader preserves dimension dependencies from either
representation, with builtin priority over execution modes. Signed defaults and
dependent expressions are folded strictly; zero/negative defaults require valid
pipeline constants before emission.

Executed verification:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  504 passed, zero failed/skipped. Seven new cases cover both representations,
  dependent signed/unsigned dimensions, shared and distinct entries, explicit
  resolution, invalid defaults and WGSL/SPIR-V roundtrips.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  passed, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-workgroup-specialization-vulkan.py`:
  exit 0, 54/54 checks including 16 GPU comparisons. Four specialization tuples
  exercise direct and roundtrip managed binaries, explicit resolution, and a
  reference control. Independent CPU expectations cover invocation counts,
  local XYZ values, shared atomic-array initialization/barriers and sentinels.
  Every dispatched binary passes native validation. The original reference
  binary for the shared-array source fails native validation with
  `VUID-StandaloneSpirv-None-10684` (explicit layout on the Workgroup array).
  Its original source, binary, validation log and failed baseline report are
  retained under `.work/naga-csharp/workgroup-specialization/`. The passing
  reference control computes the same sum directly from inputs without shared
  storage; it supplements numeric/thread-count comparison and does not certify
  the original reference shared-memory emission.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-local-size-id-vulkan.py`:
  exit 0, 40/40 checks including 16 GPU comparisons. Two entries with distinct
  dependent dimensions and two override values agree for direct LocalSizeId,
  SPIR-V roundtrip, resolved managed and resolved reference output. All binaries
  pass Vulkan 1.3 validation; the runner explicitly queries/enables maintenance4.
  Device probe reports Intel UHD Graphics, Vulkan 1.4.323, maintenance4 supported.
  Static checks also confirm Vulkan 1.1 rejects LocalSizeId without that feature.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0; counts unchanged: 197 reference-valid inputs, 184 reference-valid
  managed WGSL outputs, 182 SPIR-V emissions, 181 independent Vulkan passes,
  165 reference SPIR-V passes and 182 reference-valid managed WGSL roundtrips.

Assembly SHA256:
`0011E82272E44A7C13E1C46A634EE786E72CB088A10924D951B39176937A6815`.
Temporary runners now accept entry names and maintenance4 explicitly while
preserving their existing defaults. No task services or worktrees; active inputs
and GPU evidence remain required until maintained regressions and final cleanup.
Full parity and consumer migration remain incomplete.

## Ray-query extension follow-up, 2026-10-08

The managed frontend now admits `ray_query`, `acceleration_structure`, their
`vertex_return` forms, predeclared `RayDesc`/`RayIntersection`, ray constants and
all reference WGSL query operations. `RayDesc` uses the actual reference member
names `tmin`/`tmax` (the corpus's introductory comment uses older names).
The shared IR gained the two corresponding opaque type variants and two builtin
structure markers; this was explained before addition. Existing expression calls
carry operations; no dependency, separate registry or new runtime owner was added.

WGSL-to-SPIR-V emission uses `SPV_KHR_ray_query` and optional
`SPV_KHR_ray_tracing_position_fetch`. Local query handles are never loaded,
copied or initialized with null. Companion state and distance variables guard
uninitialized/repeated proceed, invalid descriptor distances, nonfinite vectors,
conflicting flags, candidate/committed states and primitive-specific fields.
Generated intersections require an AABB candidate and a distance within the
current interval; the upper bound reads the committed intersection selector.
Descriptor arrays support both constant and nonuniform acceleration-structure
indices. Vertex-return query initialization requires a matching acceleration
structure type.

The SPIR-V reader now preserves raw query operations, including minimum-distance
and flag getters, and can reemit both managed and reference query binaries.
UniformConstant acceleration-structure parameters become immutable opaque values;
query pointer helper parameters remain representable in IR and pass directly
without the data-pointer copy-in/copy-out path. Boolean-containing logical
structures normalize irrelevant offset decorations: the reference's logical
RayIntersection places a vec2 at offset 28, which is not a host buffer layout.
Host-buffer admission of boolean structures still fails and has a regression.

Reverse WGSL coverage remains deliberately incomplete. SPIR-V permits committed
intersection reads during active traversal, while the pinned WGSL backend's
committed getter returns an intersection only after traversal completes. Raw
getters retain their selectors in IR and SPIR-V output; WGSL writing reports an
explicit limitation for committed/state getters rather than silently changing
their behavior. Candidate-only local query paths can write reference-valid WGSL.
Query-pointer helper parameters also require an inlining/lowering pass before
WGSL writing; source admission reports that current limitation. An initial draft
mapping raw committed getters directly to WGSL getters was rejected during the
semantic audit and replaced before final verification.

Executed verification:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  524 passed, zero failed/skipped. Twenty new cases cover source operations,
  pipeline constants, handle aliases, candidate WGSL roundtrips, committed-read
  diagnostics, vertex positions, descriptor arrays, invalid/copying uses,
  descriptor member names, raw query helpers, opaque pointer parameters and
  logical-structure offsets versus host-buffer rejection.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  passed, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-ray-query.py`:
  120/120 expected outcomes: 109 successful commands and 11 expected rejections.
  There are 38 successful independent Vulkan 1.1 validations, covering ten
  managed emissions and their SPIR-V roundtrips, eight original reference
  emissions and their managed reemissions, and two reference recompilations of
  managed reverse WGSL. The four reference ray-query WGSL corpus fixtures now
  pass canonical WGSL/reference validation; three also emit with defaults, and
  `overrides-ray-query` emits with the explicitly supplied `o=0.25` value.
  Additional sources cover operations before initialization/after traversal,
  candidate-only writing, both vertex getters and descriptor-array indexing.
  The expected rejections include eight reverse-WGSL limitations, the invalid
  reference constant-index descriptor-array binary, and two reference validator
  panics on the nonuniform descriptor-array source/canonical output.
  Original reference binaries and logs, `first-native-baseline.json` and
  `nonuniform-reference-baseline.json` remain under `.work/naga-csharp/ray-query/`.
  The reference constant-index binary has an OpFunctionCall argument/parameter
  type mismatch; the nonuniform case panics at `valid/analyzer.rs:589` because
  Handle-space acceleration structures reach its buffer-only fallback. Neither
  failing reference case is used as a successful behavioral baseline.
- The same runner queries the actual Vulkan feature and extension support.
  Intel UHD Graphics reports `rayQuery=false`, without `VK_KHR_ray_query` or
  `VK_KHR_acceleration_structure`. Ray-query GPU execution is **not run**; native
  validation and roundtrips do not establish device execution equivalence.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0, 197 reference-valid inputs, 188/197 managed WGSL outputs (all 188
  reference-valid), 185 SPIR-V emissions, 184 independent Vulkan passes,
  165 reference SPIR-V passes and 182 reference-valid managed WGSL roundtrips.
  The three newly emitted query binaries explicitly fail reverse WGSL lowering;
  previous 182 roundtrips still pass. The reference SPIR-V reader rejects the
  three new query binaries. Existing entry-free abstract-module native failure,
  missing override values, invalid operators fixture and other extensions remain.

An early custom fixture mistakenly used the reserved identifier `alias`; the
reference parser caught it, and the fixture now uses `handle`. This also exposed
an existing managed reserved-keyword admission gap, retained as next work rather
than treating the invalid source as reference-valid.

Assembly SHA256:
`84C2C10C2FD0B7C00EC735A045D72304DA0C95FCD5729A2179F72D7B32A68067`.
No task services or worktrees. Task runners, reference failures and device evidence
remain active inputs; maintained GPU regression promotion and completion cleanup
are still pending. Full parity and consumer migration remain incomplete.

## Query helper lowering and keyword follow-up

The managed WGSL frontend now rejects all 172 reserved identifiers from the
pinned reference table. Keyword attributes remain legal in their own grammar.
Reserved SPIR-V entry names receive an identifier prefix on import, allowing
those binaries to write legal WGSL.

An internal query-helper inliner runs in both writers. It clones functions,
renames locals with lexical scope tracking, captures arguments in order, keeps
query parameters as pointer aliases, and propagates early returns through
nested loops and switches. A single-arm switch supplies the outer exit target,
so helpers without loops can inline inside continuing blocks. Short-circuit
conditions keep their conditional effects, eager operands retain evaluation
order, and assignment target addresses are captured before value preludes.
The original module remains unchanged. Function diagnostic filters still need
block-scope lowering and cause an explicit diagnostic; helpers whose own bodies
contain loops cannot inline inside WGSL continuing blocks.

Reverse WGSL writing records minimum-distance and flag fields for raw SPIR-V
query initialization, tracking local queries, pointer aliases and repeated
initializations. State getters use those captured fields. A public IR module
mixing guarded WGSL initialization with raw state getters is explicitly rejected:
an invalid WGSL descriptor may leave the previous native query initialized, so
unconditionally replacing its cached descriptor would be wrong. Raw committed
intersection reads during traversal remain unconverted. The two previously
failing reference imports, candidate-only and overrides-ray-query, now produce
reference-valid WGSL and independently valid reference recompilations.

The early-return switch regression exposed an existing SPIR-V reader control-flow
gap: an inner conditional branch to its enclosing switch merge walked into the
following code instead of emitting a switch break. In the larger control source,
this duplicated enough code to emit a 391,614,960-byte module with ID bound
16,384,064, rejected by SPIRV-Tools. The reader now carries the enclosing switch
merge through nested selections. The same roundtrip emits 36,864 bytes and
executes correctly. A maintained regression requires the call after such a
switch to occur once. The continuing helper regression also passes.
The initial failure's validator output, binary header and size are preserved in
`.work/naga-csharp/query-helpers/initial-switch-break-baseline.json`; the failing
temporary binary was replaced by the corrected runner output. An earlier control
source lacked a syntactic trailing return required by reference validation;
its failure is retained separately, and the reference-valid source adds that
unreachable return without changing executed paths.

Executed from the workspace root:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --nologo`:
  545 passed, zero failed/skipped. Twenty-one new cases cover reserved names and
  attributes, reserved entry import, nested returns, shadowing, continuing,
  switch-break import, module nonmutation, state aliases/reinitialization,
  generated-name collisions and explicit unsupported combinations.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --nologo`:
  passed, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-wgsl-keywords.py`:
  2067/2067 expected outcomes. All 172 words are rejected by both implementations
  in six identifier positions (2064 rejection checks); source and canonical
  keyword attributes pass the three positive checks. The runner asserts that
  the managed word set equals the pinned reference set.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-query-helpers.py`:
  34/34 checks, including eight actual GPU comparisons on Intel UHD Graphics.
  A nested-return/evaluation-order source and a continuing-block source compare
  managed inlined emission, its native SPIR-V roundtrip, reference compilation
  of inlined WGSL, and reference control sources. Query pointers are unused by
  these fixtures: the task runner proves that only address/alias declarations
  remain and removes them before device compilation. All other effects remain.
  Checks include short-circuit skips, eager operands, argument order, nested
  loop/switch exits, assignment addresses, four continuing iterations and output
  sentinels. This is control/data-flow evidence, not ray traversal evidence.
- `./.dotnet/dotnet.exe run --project .work/naga-csharp/raw-state-harness/raw-state.csproj -c Release --no-launch-profile`:
  passed; emits the maintained raw-descriptor IR fixture for independent tools.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-raw-query-state.py`:
  20/20 checks, including four actual GPU comparisons of descriptor bookkeeping
  only. Two aliased initializations produce flags 1/2 and float bit patterns for
  minimum distances 0.25/0.5 through direct WGSL and SPIR-V import, with both
  compilers. Native input/reference binaries also validate. The task-only GPU
  controls remove opaque declarations and query initialize/terminate operations;
  they do not establish native traversal equivalence.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-ray-query.py`:
  135/135 expected outcomes, including 40 successful independent Vulkan 1.1
  validations. Reference reverse imports now assert their expected outcomes;
  the two successful imports are reference-validated, reference-recompiled and
  natively validated. Committed-read limitations, the invalid constant-index
  reference descriptor-array binary and reference nonuniform-array panics remain
  explicit expected rejections. Ray query is still unavailable on the actual
  Intel device, so ray traversal execution is not run.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  final exit 0. The manifest has 197 valid reference inputs, 188 reference-valid
  managed WGSL outputs (nine input failures), 185 managed SPIR-V emissions
  (12 failures), 184 native passes (one failure), 165 reference SPIR-V passes
  (20 failures) and 182 reference-valid WGSL roundtrips (three managed failures).
  The remaining three ray reverse paths need committed lowering. Cooperative
  matrices, mesh/task, ray pipeline, external textures and the invalid operators
  fixture account for the nine WGSL failures; three missing pipeline values
  account for the additional SPIR-V failures. The entry-free abstract module
  still fails native validation. These counts match the previous completed scan;
  they do not establish full parity.
- Root and Graphics `git diff --check` and `git diff --cached --check`: passed;
  untracked managed C# and the active plan have no trailing-whitespace matches.

Final assembly SHA256:
`AD6CE4DBD8439811C1C508FC1ED3E140080C9E40FDD88ACEAF44D7B31C937D2F`.
No task services or worktrees. Oracle tools, runners, failure records and GPU
fixtures remain active inputs for parity work and maintained-regression promotion.
Full parity, uniformity/aliasing analysis, remaining extensions, consumer migration
and completion cleanup are still pending; the goal remains active.

## Cooperative matrices, 2026-10-08

Added `ShaderType.CooperativeMatrix` and `CooperativeRole` to the existing typed
IR without dependencies. The WGSL frontend requires the pinned extension enable,
accepts 8x8/16x16 f16/f32 and A/B/C roles, and normalizes optional load/store
strides. Plain memory operations use column-major layout; their T variants use
row-major layout, matching the reference. Load component types follow the memory
pointer even when the template specifies another component, as in the reference.
Validation checks matrix roles/dimensions/scopes, access rights, numeric memory,
integer strides and compute-stage use. Cooperative variables and containers use
private/function allocation; host buffers cannot contain distributed matrices.

Both SPIR-V directions support cooperative types, loads, stores and multiply-add.
Emission enables `SPV_KHR_cooperative_matrix`, `SPV_KHR_vulkan_memory_model` and
Vulkan memory model 3 when a cooperative type is emitted. Device-scope atomics
and storage barriers additionally enable capability 5346. f16 inputs and f32
accumulation retain distinct types. Arithmetic supports add/subtract and scalar
scaling; native negation writes WGSL multiplication by -1 because the pinned WGSL
validator rejects direct cooperative negation. Zero constructors and matrix
containers roundtrip. Enabling the WGSL extension without using a cooperative
type leaves the ordinary SPIR-V memory model unchanged.

Explicit MemoryAccess None is accepted. Nonzero cooperative per-access memory
operands are rejected pending equivalent lowering. Native scalar splats and
numeric pointer reinterpretation survive SPIR-V roundtrips but explicitly reject
WGSL writing. Nonzero multiply-add flags and other native cooperative operations
still need support. Coherent/volatile buffers combined with the Vulkan memory
model explicitly reject SPIR-V emission; retaining their old decorations would
produce invalid Vulkan SPIR-V, and dropping them would lose memory requirements.
No robustness/uniformity or general native cooperative parity is claimed.

Executed from the workspace root:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore`:
  final 577 passed, zero failed/skipped or warnings. Thirty-two new cases cover
  geometries, component widths, mixed accumulation, operands, memory model,
  types/operations/stages/access restrictions, aliases/containers, native
  negation/splats/reinterpretation, explicit None and unsupported memory operands.
  An initial splat regression incorrectly required runtime CompositeConstruct;
  the implementation folded it into ConstantComposite. The corrected assertion
  checks the cooperative type, single component and exact 2.0 value in either
  legal encoding. No behavior or validation was weakened.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore`:
  passed, zero warnings/errors.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-cooperative-matrix.py`:
  final 291/291 expected outcomes: 277 successful commands and 14 expected
  rejections, including 88 successful independent Vulkan 1.1 validations.
  Thirteen sources cover both upstream fixtures, all size/precision combinations,
  mixed accumulation, workgroup/vector memory, unresolved specialization strides,
  device-scope operations, inferred components and private containers. Both
  compiler outputs are imported and re-emitted; WGSL is reference-validated and
  reference-recompiled where supported. Stride values 8/16 are resolved by both
  compilers. Native fixtures cover negation, scalar splats, pointer reinterpretation
  and optional memory operands. Manifests and binaries remain in
  `.work/naga-csharp/cooperative/`.
- The runner queries the real Vulkan device. Intel UHD Graphics reports
  `cooperativeMatrix=false`, `cooperativeMatrixRobustBufferAccess=false` and no
  `VK_KHR_cooperative_matrix`. Actual cooperative GPU execution is **not run**;
  static validation does not prove device support or numerical equivalence.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0. Of 197 reference-valid inputs, 190 managed WGSL outputs pass reference
  validation (seven input failures); 187 SPIR-V emissions produce 186 independent
  Vulkan passes (the entry-free abstract module still fails). Reference SPIR-V
  import accepts 165 and rejects 22, including both new cooperative binaries.
  Managed reverse writing produces 184 reference-valid WGSL modules; the same
  three ray committed-read paths still fail explicitly. Remaining input failures
  are four mesh/task fixtures, ray pipeline, external texture and invalid operators;
  three missing pipeline values account for additional SPIR-V emission failures.
- Root and Graphics `git diff --check` and `git diff --cached --check`: passed.
  Untracked managed C# and this active plan have no trailing-whitespace matches.

The fixed reference produces independently invalid binaries in five custom
combinations. Workgroup arrays have forbidden explicit layout decorations;
device-scope atomics/barriers lack VulkanMemoryModelDeviceScope; a load-only source
has CooperativeMatrix capability but lacks its extension and Vulkan memory model;
coherent/volatile decorations are banned under that memory model. The final runner
asserts nine native reference rejections across these combinations, while managed
ordinary/workgroup/device-scope outputs validate. Coherent/volatile managed
emission reports its missing lowering. `initial-checks.json`,
`device-scope-reference-baseline.json`, `type-only-reference-baseline.json` and
the reference binaries preserve failures rather than accepting them as behavioral
baselines. Task fixture mistakes are also retained: direct unary negation rejected
by the reference, override declarations preceding directives, and two raw binary
mutations with dependencies declared after use. Corrected fixture dependencies
are declared first; original failures remain separate.

Final assembly SHA256:
`C3D500663F128CB1A01EBAAAD93046B17D7F3D501F8D6F53B8ACA43E3AA4F4C0`.
No task services or worktrees. All runners and oracle artifacts remain active
inputs. Full parity, remaining extensions/core analysis, consumer migration,
maintained GPU fixture promotion and completion cleanup remain pending.

## Mesh/task translation increment (2026-10-08)

Objective and acceptance for this increment: translate the four upstream
mesh/task fixtures plus independent triangle/line/point, f16, clip-distance,
resource-use and workgroup-specialization cases through both languages. Require
reference-valid WGSL, managed roundtrips and independent native validation for
each entry. Full Naga parity and consumer replacement remain the goal.

The shared IR now represents task/mesh stages, selected task payload and mesh
workgroup output variables, task_payload memory and per-primitive IO. Clone
passes retain the entry associations. Validation checks aggregate shape,
position/index/count types, primitive locations, selected payload use, read-only
mesh payloads and task/mesh workgroup operations. WGSL writing retains the mesh
aggregate's bindings. The writer enables SPIR-V 1.4 and SPV_EXT_mesh_shader,
tracks transitive global interface uses, reuses LocalInvocationIndex, clamps
output counts and copies mesh data after a workgroup barrier in the entry
wrapper. Early returns from the source body reach this wrapper. Task returns
terminate with OpEmitMeshTasksEXT and the selected payload.

SPIR-V reading reconstructs WGSL mesh data from per-field arrays and arrays of
interface blocks. Whole-array/whole-block loads, stores and OpCopyMemory lower
to field copies. Generated copy counters use distinct names, including when one
instruction requires both a read and write loop. A regression exposed and fixed
that collision. Block-decorated output arrays no longer become descriptor arrays;
buffer binding arrays still require a Uniform/StorageBuffer pointer to the array.
Fragment input blocks inherit variable-level PerPrimitiveEXT. The writer detects
per-primitive IO in unregistered public IR structures and adds the f16 IO
capability for mesh outputs. Per-primitive attributes without locations and
workgroup_size on other stages now fail explicitly.

Checks actually run from the workspace root:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore`:
  **599 passed**, zero failed/skipped, including 22 new mesh/task regressions.
  Three topology cases exercise both directions. Maintained raw SPIR-V factories
  cover native output blocks, whole-array/block copies and fragment input blocks.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore`:
  passed with zero warnings/errors.
- `./.dotnet/dotnet.exe run --project .work/naga-csharp/mesh-fixtures/mesh-fixtures.csproj -c Release --no-restore`:
  passed; exports the maintained fixture factories for independent validation.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-mesh-shader.py`:
  **483/483 expected outcomes**, comprising 444 successful commands and 39
  asserted rejections. Includes 120 successful Vulkan 1.2 validations and 38
  successful universal SPIR-V 1.4 validations. Eleven sources cover the four
  upstream inputs and seven custom cases. Managed and supported reference
  binaries are imported, WGSL is reference-validated and managed SPIR-V is
  re-emitted. Every managed entry validates after entry selection and dead
  function/variable/constant removal; managed isolation does not use aggressive
  dead-code elimination. Native fixtures validate before and after import.
  Resolved workgroup sizes are compiled by both implementations; unresolved
  LocalSizeId is checked with `--allow-localsizeid` (maintenance4 requirement).
  The reference writer's unresolved-override rejection is asserted separately.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-unresolved-binding-arrays.py`:
  **18/18 passed**, including independent validation before/after SPIR-V
  roundtrip and resolution for image, sampler and uniform descriptor arrays.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-mesh-device.py`:
  Intel UHD Graphics reports no VK_EXT_mesh_shader and all five queried mesh/task
  features false. Actual mesh/task GPU execution is **not run**. Static validation
  and source roundtrips do not establish numerical/image equivalence.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0. Of 197 reference-valid inputs, **194** managed WGSL outputs pass
  reference validation; the remaining failures are invalid operators, ray
  pipeline and external texture. There are 191 managed SPIR-V emissions,
  186 whole-module native passes and five native failures (four mixed mesh/task
  modules described below, plus the pre-existing entry-free abstract module).
  Reference SPIR-V import accepts 165 and rejects 26, including the four new mesh
  modules it does not support. Managed reverse writing produces **188**
  reference-valid WGSL outputs; the same three ray committed-read paths fail
  explicitly. Three additional SPIR-V emission failures lack pipeline values.
  The runner selects Vulkan 1.2 for SPIR-V 1.4 binaries and Vulkan 1.1 otherwise.
- Root and Graphics `git diff --check` and `git diff --cached --check`: passed.
  Untracked managed C# and this plan have no trailing-whitespace matches.

The 39 expected rejections remain failures of their specific commands: four
managed imports reject duplicate reference input bindings; ten reference Vulkan
checks report duplicate LocalInvocationIndex; thirteen report forbidden workgroup
layout decorations; one reference compilation rejects unresolved overrides;
eleven whole-module native checks report topology requirements on mixed entries.
The latter validator implementation loops over every entry when checking a mesh
indices builtin, applying a mesh topology requirement to task entries as well.
Selecting each actual entry gives passing managed Vulkan results. Reference
mesh entries independently expose duplicate builtin inputs or workgroup layout
decorations; these invalid binaries are not behavioral baselines. Reference
task/fragment entries are also imported after isolation and checked through WGSL.
Reference isolation uses aggressive dead-code removal to eliminate unused
builtin-block types, in addition to the managed isolation passes.

Reports and binaries remain in `.work/naga-csharp/mesh/`. Initial reference
duplicate/layout/isolation reports are retained. Fixture mistakes are separate:
an unused Function pointer to a Position-decorated block was invalid, a temporary
export project initially had an incorrect relative path, and an initial
LocalSizeId check omitted its required validator environment flag. Corrected
fixtures remove the invalid pointer and use the intended environment. Original
aggregate-copy naming and unresolved-override failures are retained too.

Known limits in this increment: native task emission in helpers, optional task
payload operands, native mesh count setting in helpers, zero-capacity output and
partially shared output interfaces require additional lowering. Payload atomic
types and unused payloads below four bytes currently remain over-restricted.
This does not complete uniformity, aliasing, robustness or arbitrary SPIR-V
extension parity. No consumers have migrated and the Rust bridge is intact.

Final assembly SHA256:
`83924015E6D601E8F3D00042F90CC580378962D0CF037C6576D127EAB6FC9F36`.
No task-owned services or worktrees. Runners, reports and oracle outputs remain
required inputs for this active goal; completion cleanup is not yet due.

## Native mesh/task control flow and payloads (2026-10-08)

The previous goal turn made source, test and independent-verification progress.
This continuation closes helper-level task termination, optional payload operands,
helper-level mesh count setting, payload atomics and unused small payloads from
the preceding increment's known limits. Full SPIR-V/WGSL parity remains active.

The reader derives reachable functions for each entry. Task emission records
dispatch counts and a private per-invocation termination flag, then returns from
the current function. Every affected caller checks that flag before continuing,
so stores and other effects after native termination do not execute. Function
signatures and ordinary return paths remain intact, including non-void helpers.
If the call occurs in a continuing region, later statements are gated and the
function return happens at the next loop body before its original instructions.
Declarations retain scope; native normal returns are not silently discarded.
The task wrapper emits the recorded counts after the call chain has unwound.
Omitted native payloads receive an unobserved u32 payload because WGSL requires
one. Existing explicit or interface payloads are retained and multiple payloads
per entry fail explicitly.

Mesh count helpers write each reachable entry's synthetic count fields from the
first invocation. A private invocation index is initialized after native input
assignment. Fully shared and disjoint output aggregates can use the same helper;
partially shared native output interfaces still need alias-preserving lowering.
Task payload atomics are accepted by both frontends, validation and emission.
Managed WGSL emission uses Workgroup scope. Mesh payload atomic loads are allowed;
atomic writes, including helper calls, are rejected. Unselected nonempty payloads
may be smaller than four bytes; selected payloads retain the four-byte minimum.

Checks actually run from the workspace root:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore`:
  **615 passed**, zero failed/skipped and no warnings in the final run. Sixteen
  new cases cover eight task control-flow combinations, two shared-helper mesh
  modules and six payload cases. Task tests execute imported IR to verify counts
  and observable writes before/after termination, then repeat after managed
  SPIR-V re-emission/import. The small executor is fixture-only CPU evidence;
  it does not establish GPU synchronization or cross-invocation equivalence.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore`:
  passed, zero warnings/errors.
- `./.dotnet/dotnet.exe run --project .work/naga-csharp/mesh-fixtures/mesh-fixtures.csproj -c Release --no-restore`:
  passed; the maintained native factories and WGSL payload cases are exported
  unchanged for independent checking.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/check-mesh-control.py`:
  **651/651 expected outcomes**, consisting of 602 successful commands and 49
  asserted rejections, including **160 successful Vulkan validations**. This
  includes the previous 483-outcome mesh suite, ten native helper fixtures and
  two WGSL atomic-payload cases. Native helper inputs validate before import;
  managed output and reference recompilation are independently checked, with
  both managed/reference WGSL validation after roundtrips. Tests cover explicit
  and omitted payloads, nested non-void helpers, normal and termination paths,
  continuing-region calls, and two mesh entries with shared or disjoint outputs.
  The report is `.work/naga-csharp/mesh/control-checks.json`.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-task-payload.py`:
  confirms that selected bool payloads fail reference validation even without
  source reads/writes, while atomic payloads validate. The exported positive
  atomic source additionally verifies an unselected bool payload.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/probe-mesh-device.py`:
  Intel UHD Graphics again reports no mesh extension and all queried features
  false. Actual mesh/task GPU execution is **not run**.
- `D:/ToolKit/Tool-Miniforge/python.exe .work/naga-csharp/scan-corpus.py > .work/naga-csharp/full-scan.log`:
  exit 0. Counts remain 194/197 reference-valid WGSL outputs, 191 managed SPIR-V
  emissions, 186 whole-module native passes plus the same five failures,
  165 accepted/26 rejected reference SPIR-V imports and 188 reference-valid
  reverse WGSL modules. The three committed-ray getter paths still fail reverse
  writing explicitly. No existing corpus acceptance was lost.
- Root/Graphics `git diff --check` and `git diff --cached --check`: passed.
  Untracked managed C# and this active plan have no trailing-whitespace matches.

The 49 expected rejections are the previous 39 plus five reference workgroup
layout rejections, one mixed-entry topology rejection and four native-reference
atomic imports. The fixed reference emits Invocation scope for task-payload
atomics; these binaries pass native validation but the managed reader explicitly
rejects that scope until per-access memory requirements can be retained/lowered.
Do not classify those four as invalid reference binaries or claim equivalent
native memory behavior. This is a remaining managed import limitation.

Early task executor failures identified missing Continue support in the fixture
executor, which was added without changing compiler behavior. An initial test
analyzer warning was corrected. The first atomic test run found the WGSL builtin
address-space gate still excluded payload memory; that frontend gate was fixed.
An intermediate runner used a broad glob and accidentally read earlier reference
outputs as native inputs; final selection matches only exact exported fixture
names. `control-input-pattern-baseline.json` and `atomic-scope-baseline.json`
retain those failures. Original tests were neither weakened nor deleted.

Remaining mesh/task work includes partially shared native output aliases,
zero-capacity outputs, output-pointer helper parameters and general per-access
memory/scope retention. Uniformity, remaining shader extensions, committed-ray
lowering, consumer migration and broader execution equivalence remain pending.
The Rust bridge and consumers are unchanged. Final assembly SHA256:
`742766A857162126F830B3ADB6D0C03F30DCE58E1B691D9E0C5C78C6B127C800`.
No task-owned services or worktrees; temporary runners, reports and binaries remain
required active goal inputs. Completion cleanup is not yet due.

## Native atomic requirements and strong compare/exchange (2026-10-08)

The shared IR now retains `Expression.Call.AtomicMemory` (`SpirvAtomicMemory`)
and `Module.VulkanMemoryModel`. These are optional native requirements on the
existing call/module nodes, with no new dependencies. Scope, success/failure
semantics and the Vulkan memory model survive native writing and cloning passes.
Shader scope/semantics operands are ordinary 32-bit constants; unsupported Kernel
`OpAtomicCompareExchangeWeak` has an explicit diagnostic. Validation checks known
bits, ordering combinations, invocation scope, queue-family requirements,
availability/visibility classes and matching compare/exchange volatile bits.
Automatic cooperative-matrix memory-model selection cannot silently make a
sequentially consistent atomic invalid.

Native strong compare/exchange has a scalar old-value result rather than an
inferred weak-result structure. WGSL writing generates a helper that retries
spurious weak failures and returns the observed value. The helper refers to the
module memory path and takes captured indices/operands by value, because the
pinned Naga does not implement unrestricted pointer parameters. Dynamic indices,
expected values and desired values are each evaluated once. Workgroup memory and
calls from continuing regions are covered. Unsupported pointer paths still have
an explicit diagnostic. The generated helper now breaks out to a final return;
the first implementation's return-only infinite loop failed reference Naga's
return-path validation and was corrected.

Relaxed scopes may widen to WGSL's address-space scope for race-free programs;
workgroup memory has no accesses outside its owning group. Relaxed storage-class
bits impose no other-memory ordering and can be removed for WGSL. Cross-device
scope and acquire/release, availability, visibility or volatile requirements
remain explicit WGSL diagnostics. Native output retains the original operands.
This reasoning follows the [WGSL memory model](https://www.w3.org/TR/WGSL/#memory-model),
[Vulkan scoped modification order/data races](https://github.khronos.org/Vulkan-Site/spec/latest/appendices/memorymodel.html)
and [SPIR-V atomic rules](https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html).
An initially conservative WGSL scope check rejected six previously accepted
reference atomic inputs; it was replaced by the safe relaxed mapping above,
restoring all six. The baseline manifest is retained, not treated as acceptance.

Accepting the reference payload scopes exposed its whole-structure initialization
of payloads containing atomic fields. The reader now decomposes whole loads,
stores and CopyMemory into atomic/non-atomic leaves, using loops for arrays and
complete source snapshots before aliased destination writes. Original ordinary
value types are retained separately from upgraded atomic memory types.

Executed from the workspace root:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj --no-restore -c Release --nologo`:
  **674 passed**, zero failed/skipped, zero warnings/errors. This adds 59 cases
  over the previous 615, including forced spurious weak failures on emitted WGSL
  IR, memory semantics, float/image atomics and aggregate aliasing.
- `.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj --no-restore -c Release --nologo`:
  passed, zero warnings/errors. Fixture export used
  `.dotnet/dotnet.exe run --project .work/naga-csharp/atomic-fixtures/atomic-fixtures.csproj --no-restore -c Release --nologo`.
- `python .work/naga-csharp/check-atomic-memory.py`: **231/231 expected outcomes**,
  including 94 successful Vulkan SPIRV-Tools validations and 33 actual GPU
  readbacks on Intel UHD Graphics. Of 198 commands, 180 succeeded and 18 asserted
  explicit WGSL diagnostics for stronger memory requirements. GPU cases compare
  independent native input, managed native roundtrip and reference compilation of
  managed WGSL for aggregate loads/stores/aliased copies, compare success/mismatch,
  dynamic index side effects, workgroup memory and continuing blocks. They do not
  certify inter-workgroup acquire/release execution or unsupported device features.
- `python .work/naga-csharp/check-mesh-control.py`: **665/665 expected outcomes**,
  620 command successes and 45 asserted rejections. All four formerly rejected
  reference invocation-scope payload imports now translate. Native reference
  re-emission is additionally checked. The remaining 45 are invalid/unsupported
  reference layout/duplicate-IO/topology cases and unresolved reference overrides,
  using the previous exact diagnostics; none are hidden or disabled.
- `python .work/naga-csharp/scan-corpus.py`: 197 reference-valid inputs; 194 WGSL
  outputs accepted by reference Naga, three failures unchanged (operators TODO,
  ray-tracing pipeline and external texture). Native emission remains 191/197;
  whole-module native validation remains 186/191, with the previous four mixed
  mesh/task validator defects and one entry-free abstract module. Reference
  SPIR-V import remains 165 accepted/26 rejected. Reverse WGSL remains 188
  accepted/three raw committed-ray getter limitations. All emitted WGSL outputs
  in the accepted groups passed independent reference validation.

The first new tests found a valid MakeAvailable fixture mislabeled invalid and
frontend snapshot declarations in the dynamic-operand fixture; both test setups
were corrected. Later checks found a reserved identifier in a GPU source and a
nullable-mask bug in the new cooperative/atomic conflict guard; both were fixed.
`atomic-memory/helper-return-baseline.json`, `fixture-keyword-baseline.json`,
`full-corpus/conservative-scope-baseline.json` and
`mesh/atomic-aggregate-baseline.json` retain the relevant failed attempts. Original
tests were neither weakened nor deleted. Final reports use the final assembly
SHA256 `F4E8E327A72FDC65247EA94F4688D28762E6DAC51AB163AB61CFD679F45C3E88`.

The full goal remains active. General per-access memory operands/barriers,
uniformity/alias validation, remaining extensions, raw committed-ray lowering,
mesh interface limits, consumer migration and broader AOT/browser/GPU acceptance
are still pending. The Rust bridge and consumers remain unchanged. No task-owned
services or worktrees; runners/oracle/reports in `.work/naga-csharp` remain
required active inputs, so completion cleanup is not yet due.

## Native barrier requirements and safe WGSL fence lowering (2026-10-08)

Objective: retain native barrier execution/memory scopes and semantics, keep
memory-only fences distinct, and admit WGSL lowering only when it does not lose
ordering or introduce an unproven collective wait. Scope is the shared IR,
SPIR-V reader/writer, WGSL memory lowering and maintained regressions. Acceptance
uses exact operand roundtrips, independent Vulkan validation and GPU readback.

`SpirvBarrierMemory` is optional metadata on control/memory barrier statements.
Readers retain ordinary constant operands; cloning preserves them and native
writing restores the exact scopes/semantics, Vulkan model/device-scope capability
and subgroup capability. Validation checks memory classes, ordering combinations,
availability/visibility, model requirements and stage scope. Sequential ordering
cannot silently become invalid when another feature selects the Vulkan model.
Default WGSL barriers now use their specified memory scope: workgroup for storage,
workgroup and texture barriers; subgroup for subgroup barriers. The old storage
barrier used device scope and could subsequently be narrowed on reverse writing.

`WgslAtomicLowering` became `WgslMemoryLowering`, retaining its atomic behavior.
Control barriers may strengthen acquire/release ordering and bounded workgroup
memory scope. Storage/image ordering at device/queue-family scope, sequential
ordering and availability/visibility requirements produce explicit diagnostics.
Pure execution barriers retain execution synchronization. Memory-only fences
lower to control barriers only if every reachable entry context is a resolved
single-invocation compute/task/mesh workgroup. The proof follows the complete
call graph and excludes helpers shared with larger workgroups. Override defaults
are not proof; explicitly resolved pipeline values are. Conditional single-thread
fences are safe; conditional multi-thread fences remain native or diagnose WGSL
lowering. General uniformity/equivalence analysis remains pending.

The initial blanket memory-fence rejection failed an existing single-invocation
regression; the proven lowering above restored it. New tests exposed abstract
integer workgroup-size expressions and the proof now evaluates their explicit
u32 conversion. Default memory fences now also check their actual memory scope
against graphics stages; native device fences remain valid in vertex/fragment.
Existing regressions were not removed or weakened.

Executed from the workspace root, with the already successful task sync/setup:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj --no-restore -c Release --nologo`:
  **738 passed**, zero failed/skipped. Adds 64 cases over 674, including native
  scope/semantics, graphics-stage fences, conditional fences, shared helpers,
  pending/resolved workgroup-size overrides and default barrier scope.
- `.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj --no-restore -c Release --nologo`:
  passed, zero warnings/errors. Fixture export used
  `.dotnet/dotnet.exe run --project .work/naga-csharp/barrier-fixtures/barrier-fixtures.csproj --no-restore -c Release --nologo`.
- `python .work/naga-csharp/check-barrier-memory.py`: **210/210 expected outcomes**,
  136 successful commands, 16 asserted diagnostics and 58 successful actual GPU
  readbacks on Intel UHD Graphics. Includes 73 successful independent Vulkan
  validations, exact native input/roundtrip paths and reference-compiled managed
  WGSL where the reference output is Vulkan-valid. Four invocations also exchange
  workgroup-array values across a collective barrier, producing `[2,3,4,1]`.
  Native vertex/fragment fences pass independent validation. GPU checks do not
  certify cross-workgroup ordering, Vulkan-memory-model execution or sequential
  ordering: the current runner does not enable the required device feature.
- `python .work/naga-csharp/check-atomic-memory.py` and
  `python .work/naga-csharp/check-mesh-control.py`: **231/231** atomic and
  **665/665** mesh/control expected outcomes passed, including 33 atomic GPU
  readbacks. Final reruns after the default fence stage checks passed with the
  same counts. All reports use the final assembly below.
- `python .work/naga-csharp/scan-corpus.py`: 197 reference-accepted inputs;
  191 WGSL outputs accepted by reference Naga, six rejected inputs; 189 native
  emissions, 184 whole-module Vulkan validations, 163 reference SPIR-V imports
  accepted/26 rejected and 185 reverse WGSL outputs accepted. Three new rejected
  inputs are explained below; other failure groups are unchanged. The previous
  full manifest remains in `full-corpus/pre-barrier-baseline.json`.

Independent validation found two reference-output differences. Its subgroup
barrier uses SubgroupMemory (`136`), rejected by Vulkan with VUID 04650. Its
workgroup array has explicit layout without the corresponding workgroup-layout
capability, rejected with VUID 10684. The runner asserts those exact diagnostics,
does not execute either invalid reference binary, and additionally compiles the
same managed WGSL with C#, validates it and checks actual GPU output. Both failed
attempts remain in `barrier-memory/reference-subgroup-baseline.json` and
`reference-workgroup-layout-baseline.json`.

The new full-corpus rejections are `barrier.spvasm` (multi-thread memory-only
fences and device-scope storage/image ordering need equivalent WGSL lowering),
`subgroup-barrier.spvasm` and `subgroup-operations-s.spvasm`. The latter two use
SubgroupMemory-only or ordering without a supported memory class. Independently
validating the original binaries with `spirv-val --target-env vulkan1.1` rejects
them with VUID 04733/04650; logs are retained in `barrier-memory`. Reference
acceptance alone is not Vulkan acceptance. A separate generic-SPIR-V validation
policy, including these broader core forms, still needs review; do not silently
change native operands to make such inputs Vulkan-valid.

An additional audit now reproduces a remaining defect: a Vulkan-valid OpLoad and
OpStore with per-access Volatile operands read and re-emit successfully but lose
those operands. `barrier-memory/per-access-audit.json` records both exact commands
and input/output operand lists. This is not a passing equivalence check. Next
retain per-access flags/alignment/availability/visibility in IR and cloning,
emit them natively, and diagnose WGSL when no equivalent exists. Cover ordinary,
aggregate/aliased CopyMemory and cooperative accesses; member decorations also
need preservation. Cooperative nonzero operands currently diagnose explicitly.

Final assembly SHA256:
`319B6851B57C5518F412992F565AAE34564A3AACECE60F662D7CE0825CBDCA46`.
Root/Graphics staged and unstaged `git diff --check` passed. Source/project/plan
trailing-whitespace search returned no matches. Git reported existing CRLF-to-LF
normalization notices in root docs; no whitespace failures.
The full goal remains active. No task-owned services or worktrees; task runners,
oracles and reports remain necessary active inputs in `.work/naga-csharp`.
Completion cleanup is not yet due.

## Per-access memory preservation and Vulkan volatile lowering (2026-10-08)

The preceding turn made progress: it implemented barrier fidelity and produced a
minimal, independently Vulkan-valid volatile-loss reproduction. This turn fixes
that reproduced defect, retaining the full SPIR-V/WGSL goal and existing scope.
Acceptance covers ordinary/cooperative access operands, source/target copies,
cloning, independent Vulkan validation, reference WGSL compilation and GPU data.
Task sync/setup were already successful; they were not repeated. Graphics had
only this goal's project/solution changes; unrelated root/Engine edits remain.

`IR.SpirvMemoryAccess` records the memory-access mask, literal alignment and
constant availability/visibility scopes. Loads, stores and ordinary/cooperative
memory calls retain optional metadata. Readers parse operand lengths and reject
unknown masks; validation checks alignment powers, required scope fields, Vulkan
model/non-private requirements, read/write direction, address spaces and scope.
Native writing restores operands/capabilities. Explicit None remains represented.
Pipeline resolution, query helper/state passes and WGSL layout cloning retain
metadata rather than accidentally constructing unqualified replacement nodes.

CopyMemory splits a shared mask into read and write requirements and supports
separate SPIR-V 1.4 masks. Availability applies after the destination write;
visibility applies before the source read. Atomic-containing structures/arrays
retain ordinary access flags on every real leaf, rather than converting volatile
ordinary accesses into relaxed atomic opcodes. Base alignment promises are
removed on split leaves because arbitrary member offsets need not share them.
Aliased atomic copies snapshot all source values before writes; projected mesh
destinations now also snapshot an ordinary source exactly once before their
split writes, avoiding duplicate volatile loads. Existing mesh regressions pass;
mesh/task GPU execution remains outside the current runner's enabled features.

WGSL ignores alignment/nontemporal hints and can strengthen shared non-private
ordering. Proven storage-buffer volatile roots lift to WGSL `@volatile`, including
relaxed volatile storage atomics. Other address spaces and unknown roots still
diagnose; local/parameter shadowing cannot be mistaken for a global binding.
Native per-access availability/visibility still require equivalent WGSL lowering.
Volatile strong compare/exchange explicitly diagnoses WGSL conversion: its weak
retry implementation would duplicate a volatile operation. Native output retains
one strong compare/exchange, including matching volatile failure semantics.
These choices follow the [SPIR-V memory operands and atomics](https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html)
and the pinned Naga's WGSL volatile attribute support.

Under the Vulkan memory model, a volatile buffer attribute lowers to actual
ordinary/cooperative access flags and atomic volatile semantics. Pointer aliases
are covered; unknown shared aliases conservatively keep their existing accesses
volatile without introducing accesses. Cooperative type use is detected before
body emission so earlier functions use the correct model. The legacy ordinary
atomic default scope was preserved: an intermediate change to automatic queue
scope failed an existing capability regression and was corrected. Native Vulkan
inputs now reject banned legacy coherent/volatile variable/member decorations.
Global coherent requirements under Vulkan still diagnose until equivalent
availability/visibility lowering is implemented.

Executed from the workspace root on final assembly
`E796366120B8D0025A1948C12F3406A591D993CB1F89D884FFFA0204D70A374E`:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj --no-restore -c Release --nologo`:
  **784 passed**, zero failed/skipped, adding 46 cases over 738. Covers native
  access masks/scopes, source/target copy separation, volatile aggregate aliases,
  cooperative accesses, Vulkan/global/atomic volatile behavior, pointer aliases,
  shadowed roots, malformed operands/alignment, old-version copies and banned
  Vulkan decorations. Existing aligned-cooperative and Vulkan-volatile limitation
  assertions became positive native preservation checks for implemented behavior;
  coherent limitations retain their explicit diagnostic. No regression was deleted
  or weakened to hide a failure.
- `.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj --no-restore -c Release --nologo`:
  passed, zero warnings/errors. Export used
  `.dotnet/dotnet.exe run --project .work/naga-csharp/access-fixtures/access-fixtures.csproj --no-restore -c Release --nologo`.
- `python .work/naga-csharp/check-access-memory.py`: **263/263 expected outcomes**;
  224 commands comprise 202 successes and 22 asserted diagnostics, plus 39 actual
  successful Intel UHD Graphics GPU readbacks. Includes 106 successful independent
  Vulkan validations. Original/native roundtrip/reference-compiled managed WGSL
  agree for scalar accesses, shared-mask copies and atomic-containing aggregate
  loads/stores/aliased copies. Vulkan volatile ordinary/atomic cases also compile
  the managed WGSL through both writers and read back `[9,10]`. Legacy volatile
  strong compare/exchange executes natively/after native roundtrip with one
  strong operation and returns `[9]`; WGSL diagnoses the forbidden retry. Native
  Vulkan-model and cooperative GPU execution are not run: the current executor
  does not enable their device features. These results do not certify broader
  inter-thread visibility or multi-device ordering.
- `python .work/naga-csharp/check-atomic-memory.py`: **231/231** expected outcomes,
  including 33 successful GPU readbacks; previous explicit stronger-semantics
  diagnostics remain unchanged.
- `python .work/naga-csharp/check-barrier-memory.py`: **210/210** expected outcomes,
  including 58 successful GPU readbacks and the previous exact reference-output
  diagnostics.
- `python .work/naga-csharp/check-mesh-control.py`: **665/665** expected outcomes,
  preserving 620 command successes/45 exact expected rejections.
- `python .work/naga-csharp/scan-corpus.py`: outcome codes unchanged from
  `full-corpus/pre-access-baseline.json`: 197 reference-accepted inputs, 191 WGSL
  outputs accepted/six rejected, 189 native emissions/eight rejected, 184 whole
  Vulkan validations/five rejected, 163 reference SPIR-V imports accepted/26
  rejected, and 185 reverse WGSL outputs accepted/four rejected. Original known
  failures are still reported, not reclassified as success.

The first independent cooperative-volatile reference compilation emitted a
legacy Volatile decoration with Vulkan memory model, which SPIRV-Tools rejects.
`access-memory/reference-volatile-baseline.json` retains that failure. Final
checks assert the exact banned-decoration diagnostic for both such reference
outputs, do not execute them, and compile/validate the same WGSL with C# instead.
The C# output uses legal per-access flags. Four additional independent invalid
variable/member coherent/volatile inputs are rejected by both C# and SPIRV-Tools.
The original failed audit in `barrier-memory/per-access-audit.json` remains as
historical reproduction evidence; maintained/independent final checks now cover
its volatile-loss behavior successfully. Build issues during implementation were
corrected before final verification. A new shadowing fixture initially used
reference-disallowed storage pointer parameters; it now uses permitted private
pointer parameters, preserving the validator's existing restriction.

Remaining work includes member coherent/volatile preservation, native uniform
layout conversion with per-access semantic requirements, Vulkan coherent buffer
lowering, broader proven WGSL volatile pointer roots, general memory-fence
uniformity/equivalence and the preceding extension/validation/migration gates.
The goal remains active. No task-owned services or worktrees; runners, oracle,
fixtures and reports in `.work/naga-csharp` remain required active work inputs,
so completion cleanup is not yet due.

## Member memory requirements and coherent Vulkan buffers (2026-10-08)

The preceding goal turn was progress: it fixed per-access operand loss and
established native/reference/GPU regression evidence. This continuation targets
the remaining member-decoration loss and coherent Vulkan buffer lowering. Accept
exact native member preservation, legal Vulkan access flags/scopes, equivalent
storage-buffer WGSL attributes, no extra qualified accesses in generated
snapshots, and independent validation/readback of affected paths. The full goal
and its earlier extension/validation/consumer migration gates remain unchanged.
Sync/setup already succeeded for this task/worktree and were not repeated.

Added `StructMember.MemoryDecorations` to the existing member record. The reader
retains native Volatile/Coherent member decorations; existing type clones retain
the property. Legacy native writing preserves member selection, including nested
arrays, atomically upgraded structures and uniform matrix column splitting.
WGSL lifts nested member requirements to their owning storage buffer. Native
Vulkan writing replaces coherent storage requirements with NonPrivatePointer
and queue-family MakePointerVisible/MakePointerAvailable, and volatile members
with per-access/atomic volatile flags. Known access paths do not qualify adjacent
members; unknown shared aliases conservatively strengthen existing operations.
Explicit Device visibility/availability scopes are not narrowed.

Queue-family visibility/availability on proven storage roots can lift back to
WGSL `@coherent`. Device and other scopes retain explicit diagnostics until their
equivalence is proved. WGSL requirement collection now precedes rewriting all
functions: a later volatile access cannot silently make an earlier strong-CAS
retry helper duplicate volatile operations. Strong volatile CAS still executes
one native operation and rejects WGSL retry conversion.

New aggregate tests exposed accidental qualifier inheritance on generated SSA
registers and copy snapshots. Original function-memory member volatility is now
captured on accesses by the reader; the native writer does not infer requirements
from the logical value type of newly created function temporaries. Independent
function-memory input/output validation and GPU checks cover the original
initializer store, selected field store and load; WGSL rejects this non-storage
volatile memory explicitly. Uniform root conversions now also reject reads of
unqualified fields when the current whole-root conversion would introduce reads
of a qualified neighbor. Workgroup/task-payload member requirements explicitly
reject Vulkan conversion until initialization-aware lowering is implemented;
their automatic initialization cannot silently discard or duplicate accesses.

Primary implementation cross-check: pinned SPIRV-Tools
`source/opt/upgrade_memory_model.cpp`, especially instruction attributes,
UpgradeMemoryAndImages and UpgradeAtomics, plus the
[Khronos memory-model extension](https://github.khronos.org/SPIRV-Registry/extensions/KHR/SPV_KHR_vulkan_memory_model.html).
No new runtime dependency. The member record property was explained before
addition. Coherent cooperative limitation tests now assert exact positive native
flags/scopes and WGSL attributes, rather than the obsolete diagnostic. The
original failure evidence is retained; no independent validation is disabled.

The task-only Vulkan runner now optionally queries/enables
`VkPhysicalDeviceVulkanMemoryModelFeatures`, using the locally inspected Vulkan
header ABI. Intel UHD Graphics reports memory-model, DeviceScope and
availability/visibility-chain support. Isolated devices/buffers execute original,
C# native, reference WGSL and SPIRV-Tools upgraded variants. Cooperative matrix
GPU execution remains not run because that runner does not enable the feature.
Single-invocation readbacks do not establish multi-thread/device memory ordering.

Final verification passed on matching project/harness assembly SHA256
`992E9596E98C20C31D9BA2A4D079EC0E35D76D37CDD19C65AD9ECC0AD6EFC47F`.
The maintained suite has passed 816 tests, adding 32 over 784, with zero failures
or skips. All independent runner handles were polled to terminal exit 0.

Executed from the workspace root:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj --no-restore -c Release --nologo`:
  **816 passed**, zero failed/skipped.
- `.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj --no-restore -c Release --nologo`:
  passed, zero warnings/errors. Fixture export used
  `.dotnet/dotnet.exe run --project .work/naga-csharp/member-fixtures/member-fixtures.csproj --no-restore -c Release --nologo`
  and the existing `access-fixtures` project. The new export project was restored
  on its first run; subsequent runs used `--no-restore`.
- `python .work/naga-csharp/check-member-memory.py`: **505/505 expected outcomes**,
  including **163 actual successful GPU readbacks**, 57 with explicitly queried
  and enabled Vulkan memory-model features. Checks selected fields, nested
  arrays, aggregate/aliased atomic copies, ordinary pointer aliases, relaxed
  atomics, strong CAS and original function memory. Native validation covers
  legacy inputs, managed Vulkan conversion, native roundtrips, both WGSL writers
  and the independent SPIRV-Tools `--upgrade-memory-model` pass. Two volatile
  strong-CAS WGSL diagnostics and two original-function-memory WGSL diagnostics
  are expected. Two cooperative reference outputs contain Coherent decorations
  banned under Vulkan; exact SPIRV-Tools diagnostics are asserted, and those
  reference binaries are not executed. The managed cooperative equivalents
  independently validate with legal per-access flags.
- `python .work/naga-csharp/check-access-memory.py`: **344/344 expected outcomes**,
  **100 actual successful GPU readbacks**, 32 with Vulkan memory-model features.
  This replaces the preceding narrower 263-check run: queue-family coherent cases
  now compile to WGSL and native Vulkan inputs/outputs now execute with correctly
  enabled device features. The preceding report is preserved as
  `access-memory/pre-coherent-checks.json`. Other visibility scopes retain their
  diagnostics. The earlier cooperative volatile reference failures remain
  exact expected rejections.
- `python .work/naga-csharp/check-atomic-memory.py`: **231/231**, including 33 GPU
  readbacks; `python .work/naga-csharp/check-barrier-memory.py`: **210/210**,
  including 58 GPU readbacks; `python .work/naga-csharp/check-mesh-control.py`:
  **665/665**. Existing expected limitations remain explicit.
- `python .work/naga-csharp/scan-corpus.py`: step outcome codes exactly unchanged
  from `full-corpus/pre-coherent-baseline.json`: 197 reference-accepted inputs,
  191 accepted managed WGSL outputs/six failures, 189 native emissions/eight
  failures, 184 whole Vulkan validations/five failures, 163 reference SPIR-V
  imports accepted/26 rejected, 185 reverse WGSL outputs/four failures, and all
  191/185 emitted WGSL results accepted by the reference. Earlier known failures
  remain failures; this scan does not certify full Naga parity.

Initial test setup issues were corrected: annotations must precede the first
type, not merely TypeVoid; the native storage wrapper is separate from its Data
member; original function memory has both an initializer store and a field store;
stage IO private globals are not storage bindings; `shared` is a reserved WGSL
identifier. The aggregate qualifier leak was a real defect and was fixed before
rerunning checks.

Remaining: direct uniform access/layout lowering; workgroup/task-payload
initialization-aware member lowering; broader proven volatile pointer roots;
non-queue-family visibility/availability equivalence; general fence uniformity;
the earlier corpus/extension/interface/migration/AOT/browser/full-GPU gates.
No services or worktrees. `.work/naga-csharp` remains required active work input,
including reference-output failures and independent runners; completion cleanup
is not due while the full goal remains active.

## Uniform direct access continuation (2026-10-08)

Objective: replace whole-root Uniform layout reads with accesses to the selected
logical location, preserving native memory operands and captured pointer indices.
Acceptance: selected scalar loads do not read neighboring fields/columns; dynamic
column selection performs one actual read; whole loads reconstruct disjoint
leaves; native validation and independent GPU readbacks preserve byte layout.
Other formats and the existing Rust integration remain outside this increment.

The first volatile component regressions reproduced the old conversion guard.
The native writer now captures a logical member/index path and follows it through
the physical Uniform layout. Matrix columns flattened into structure members use
constant access chains or a structured switch; only the selected branch loads.
Component reads remain scalar. Alias declarations capture evaluated index IDs
once, so changing the original index variable does not retarget the pointer.
Nested aliases reuse that location; index calls are evaluated once. Whole matrix,
array and structure loads reconstruct disjoint leaves, retaining volatile/cache
and visibility requirements while discarding split base-alignment promises.
Member requirements qualify their selected columns without reading qualified
neighbors. The per-access decorator calculation is shared with ordinary memory
operations; no public interface or dependency was added.

Uniform binding arrays now use and Block-decorate their mapped physical descriptor
element. The reader previously ignored non-natural MatrixStride decorations and
could silently change the buffer ABI. It now explicitly rejects those strides,
including arrays of matrices, until explicit stride lowering exists. The pinned
reference reader similarly reports UnsupportedMatrixStride for the direct matrix
fixture. A valid original native Storage fixture with stride 16 independently
validates and reads the expected value on GPU; both readers reject its translation.
Row-major and general explicit matrix-stride lowering remain pending.

The obsolete Uniform neighbor diagnostic test now asserts exact selected load
types/counts. New maintained regressions cover 18 concrete f16/f32 matrix shapes,
native access masks/scopes, whole loads, member requirements, aliases, nested
arrays, once-only calls, descriptor Block/type mapping and unsupported stride.
The task-only Vulkan runner gained an optional Uniform descriptor/input usage;
its default Storage behavior is covered again by the existing independent runners.

Reference failures were retained rather than counted as successful validation:
both constant/dynamic original WGSL Uniform binding-array outputs have an invalid
OpAccessChain result type; both flattened reverse WGSL reference outputs lack
the descriptor element's Block decoration (VUID-StandaloneSpirv-Uniform-06676).
The local pinned writer's binding-array Block branch only handles Storage.
All four precise SPIRV-Tools failures are asserted, and those binaries are not
executed. Corresponding C# binaries independently validate. The initial failure
report remains at `uniform-memory/first-reference-binding-failure.json`.

Final project/harness assembly SHA256 matches:
`33FFFA52C82BFD1520318449E6BD7018B50C0620A368AEADB4D8CF9ACF54F62A`.
All launched runner handles reached terminal exit 0; no checks are left running.
Executed from the workspace root:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj --no-restore -c Release --nologo`:
  **853 passed**, zero failures/skips, 37 more than the preceding 816.
- `.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj --no-restore -c Release --nologo`:
  passed, zero warnings/errors. Fixture export:
  `.dotnet/dotnet.exe run --project .work/naga-csharp/uniform-fixtures/uniform-fixtures.csproj --no-restore -c Release --nologo`.
  The initial export restored successfully but failed on an ambiguous Module type;
  adding an explicit IR alias fixed it. Its first dependent runner invocation had
  no fixture manifest and failed; both were rerun successfully in dependency order.
- `python .work/naga-csharp/check-uniform-memory.py`: **394/394 expected outcomes**:
  194 successful commands, 37 exact expected rejections and **163 GPU readbacks**,
  including 28 with explicitly enabled Vulkan memory-model features. There are
  111 successful native validations. Original/managed-native roundtrips execute
  for volatile and visible Uniform reads; expressible ordinary WGSL also executes
  after both compilers. Alias readbacks vary the captured index; nested-array
  readbacks verify offsets and one index-call side effect. Flattened dynamic
  out-of-range column cases return zero without a memory read, a permitted WGSL
  invalid-load result; this does not establish a general bounds policy.
- `python .work/naga-csharp/check-member-memory.py`: **505/505**, 163 GPU readbacks;
  `python .work/naga-csharp/check-access-memory.py`: **344/344**, 100 GPU readbacks;
  `python .work/naga-csharp/check-atomic-memory.py`: **231/231**, 33 GPU readbacks;
  `python .work/naga-csharp/check-barrier-memory.py`: **210/210**, 58 GPU readbacks;
  `python .work/naga-csharp/check-mesh-control.py`: **665/665**. Existing expected
  limitations and independent reference failures remain asserted.
- `python .work/naga-csharp/scan-corpus.py`: all per-input step outcome codes
  exactly match `full-corpus/pre-uniform-baseline.json`: 197 reference inputs,
  managed WGSL 191 pass/six fail, native emission 189 pass/eight fail, Vulkan
  validation 184 pass/five fail, reference native import 163 pass/26 fail,
  reverse WGSL 185 pass/four fail. All 191/185 emitted WGSL results are accepted
  by the reference. This preserves known failures, not full compatibility.
- `git diff --check` and `git -C Sia.Graphics diff --check`: passed. Git reports
  existing CRLF normalization warnings on workspace docs.

Not run: Uniform descriptor-array GPU execution (runner feature/layout support),
mesh/task or cooperative GPU execution, multi-thread memory-order equivalence,
AOT/browser and integration migration. No service or worktree was created.
The full goal remains active; `.work/naga-csharp` remains required for oracle,
independent runners and failure reproduction until completion cleanup.

## Workgroup memory continuation (2026-10-08)

Objective: retain implicit Workgroup coherence and selected member volatility
through native accesses, initialization, aliases and generated mesh publishing.
Acceptance: native outputs validate without banned legacy decorations; captured
pointers retain their member requirements; native roundtrips do not duplicate
initialization; specialized initialization follows the pipeline length; independent
GPU readbacks cover scalar, nested, atomic and multi-invocation Workgroup memory.
Task-payload volatility is included; its coherence remains an explicit diagnostic.
Other formats and the existing Rust integration remain outside this increment.

The initial nine regressions reproduced the previous broad conversion guard and
missing Workgroup access flags. With the Vulkan memory model, Workgroup ordinary
loads/stores now retain implicit coherence with masks 48/40 and Workgroup scope;
selected Volatile accesses add bit 1. Existing wider Device/QueueFamily scopes
remain wider. Immutable pointer aliases capture the selected requirements at
declaration. An audit also found implicit member/index reads bypassing the flag
calculation; two raw IR regressions and eight independent fixtures cover those
paths. Workgroup load lowering now requires an explicit memory argument.
Atomic operations, including initialization and workgroupUniformLoad, retain
Volatile semantics. Generated mesh publishing applies requirements from the
selected counts, aggregate arrays and fields to its Workgroup reads.

`Module.WorkgroupInitializationRequired` is a new public IR property (default
true). WGSL requires implicit zeroing; native input already describes its own
initialization operations. The native reader sets this property false and all
module cloning paths preserve it, preventing duplicate Volatile stores on native
roundtrips. Initialization splits qualified structures/atomic leaves as required.
Specialization-sized arrays use a structured loop with the actual pipeline
length, including unqualified arrays: the previous zero-array constructor could
not be expressed by reverse WGSL without prematurely resolving the override.
This metadata is the smallest distinction needed to preserve both source models;
no new dependency was added.

WGSL accepts proven Workgroup-scope coherence because it already supplies that
behavior. Workgroup volatility and wider availability/visibility scopes retain
diagnostics. TaskPayloadWorkgroupEXT ordinary and atomic Volatile operations use
legal access flags/semantics. Coherent TaskPayload remains rejected: the pinned
SPIRV-Tools memory validator excludes storage class 5402 from NonPrivatePointer.
The mesh extension's shared payload/barrier rules do not justify dropping the
coherence requirement or emitting a forbidden pointer flag.

Primary implementation references: the pinned SPIRV-Tools
`source/opt/upgrade_memory_model.cpp` treats Workgroup as implicitly coherent;
`source/val/validate_memory.cpp` defines allowed non-private pointer classes.
The [Vulkan memory model extension](https://github.khronos.org/SPIRV-Registry/extensions/KHR/SPV_KHR_vulkan_memory_model.html)
and [mesh shader extension](https://github.khronos.org/SPIRV-Registry/extensions/EXT/SPV_EXT_mesh_shader.html)
define the corresponding native operands and task-payload behavior.

Reference failures remain explicit. Naga's SPIR-V writer decorates ordinary
Workgroup structure members with offsets; SPIRV-Tools rejects these non-Block
explicit layouts with VUID-StandaloneSpirv-None-10684. Each affected reference
output is checked for that exact diagnostic and is not executed. Managed native
equivalents validate. SPIRV-Tools' upgrade pass is additionally used for ordinary
coherence fixtures, but not as a Volatile Workgroup oracle: its Workgroup shortcut
does not propagate Volatile member requirements.

Initial runner failures are preserved in `workgroup-memory`: the first reference
layout failure, the specialized zero-constructor failure fixed in production,
and the unresolved reference override failure fixed by supplying `0=2` only to
the reference compiler. Mesh reference output has the same exact layout rejection.
The final fresh runner uses the final production assembly; earlier resume runs
are not substituted for it.

Final project/harness assembly SHA256 matches:
`F51669815FEC7C51C2D7125B4F84DD46C13C43B93CD083E9505E69765BFAC5BE`.
Executed from the workspace root:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj --no-restore -c Release --nologo`:
  **873 passed**, zero failures/skips, 20 more than the preceding 853.
- `.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj --no-restore -c Release --nologo`:
  passed, zero warnings/errors. Final fixture export passed:
  `.dotnet/dotnet.exe run --project .work/naga-csharp/workgroup-fixtures/workgroup-fixtures.csproj --no-restore -c Release --nologo`.
- `python .work/naga-csharp/check-workgroup-memory.py`: **1188/1188 expected
  outcomes**, terminal exit 0: 581 successful commands, 111 exact expected
  rejections, 292 successful native validations and **496 successful GPU
  readbacks**, including 272 with enabled Vulkan memory-model features. Thirty-two
  readbacks explicitly specialize the array length to 3 and access element 2.
  Thirty-seven fixtures cover scalar/alias/nested/specialized/atomic/multi/raw
  member/raw array cases with four qualifier combinations, three mesh cases and
  two TaskPayload cases. The 64-invocation neighbor/barrier fixture runs on Intel
  UHD reporting subgroup size 32, so its workgroup spans two subgroups. Task/Mesh
  fixtures receive native validation rather than GPU execution. All final runner
  handles are terminal; no launched checks remain running.
- `python .work/naga-csharp/check-uniform-memory.py`: **394/394**, 163 GPU readbacks;
  `python .work/naga-csharp/check-member-memory.py`: **505/505**, 163 GPU readbacks;
  `python .work/naga-csharp/check-access-memory.py`: **344/344**, 100 GPU readbacks;
  `python .work/naga-csharp/check-atomic-memory.py`: **231/231**, 33 GPU readbacks;
  `python .work/naga-csharp/check-barrier-memory.py`: **210/210**, 58 GPU readbacks;
  `python .work/naga-csharp/check-mesh-control.py`: **665/665**. All six final
  runner handles reached terminal exit 0 on the final assembly. Existing exact
  expected reference failures/unsupported WGSL diagnostics remain asserted.
- `python .work/naga-csharp/scan-corpus.py`: terminal exit 0. Against
  `full-corpus/pre-workgroup-baseline.json`, only one per-input step code changed:
  `8151-barrier-reorder.spvasm` reference native import now reports
  `InvalidOperandCount(Store, 5)`. Its managed output retains Workgroup ordering
  with scoped availability/visibility operands and passes native validation and
  managed reverse translation. The reference reader rejects the extra scoped
  Store operand. Managed outcome codes are unchanged: 197 reference inputs,
  managed WGSL 191 pass/six fail, native emission 189 pass/eight fail, Vulkan
  validation 184 pass/five fail, reverse WGSL 185 pass/four fail. Reference native
  import is now 162 pass/27 fail (previously 163/26). All 191/185 emitted WGSL
  results are reference-accepted. This is not full compatibility.
- `git diff --check` and `git -C Sia.Graphics diff --check`: passed; existing
  workspace CRLF normalization warnings remain. A trailing-whitespace search over
  managed project/test C# and project files returned no matches. Final assembly
  hashes were independently compared after the runners launched.

Not run: Mesh/Task or cooperative GPU execution, Uniform descriptor-array GPU
execution, general multi-device memory-order equivalence, AOT/browser and
integration migration. TaskPayload coherence remains diagnosed. No services or
worktrees were created. The full goal remains active; `.work/naga-csharp` remains
required for oracle, independent runners and precise failure reproduction until
completion cleanup.

## Native matrix layout continuation (2026-10-08)

Objective: consume native MatrixStride/RowMajor member decorations into exact
physical memory paths while preserving logical column-major matrix operations.
Acceptance: valid native input, managed native output and both reverse-WGSL
compilers validate; independent GPU output preserves selected components, columns,
whole matrices and neighbors for padded/non-square/dynamically indexed layouts.
Array/nested layouts, memory operands and helper pointer paths require their own
coverage. This increment must not weaken native validation or simply remove the
old guards. No public IR metadata or dependency is planned.

Eight initial maintained regressions all failed with the old explicit-stride or
row-major diagnostic. All eight original binaries independently passed
`spirv-val --target-env vulkan1.2`; evidence is
`matrix-layout/initial-native-validation.json`. Their temporary fixture exporter
first had an incorrect relative ProjectReference; correcting four parent steps
to three fixed the setup. A single local switch-variable name collision was
fixed before successful compilation. The focused regressions now pass and an
intermediate full suite passes 881 tests; these are preliminary, not final gates.

The new private reader lowering separates memory and value representations.
Affected memory fields become strided vector arrays at their original offsets;
ordinary matrix computations retain logical column-major types. Row-major column
loads gather selected scalar locations and stores scatter those same locations.
Split stores snapshot the whole source before any destination write. Native
AccessChain indices are captured at their original evaluation point when this
lowering is active. Original initialization metadata and memory operands survive;
split accesses discard only base-alignment promises. Entire native-layout arrays
currently require resolved finite lengths; pointer projections escaping through
helper parameters and constant module-initializer reconstruction still diagnose
until equivalent lowering is implemented. Function locals now use their logical
types, including SSA snapshot structures; logical-type mapping is idempotent to
avoid registering the normalized structure twice. An extended root-copy regression
first exposed an implicit SSA reference type mismatch, which was fixed. Moving
locals to logical layouts then exposed duplicate module-name registration, also
fixed before the final full suite. Neither assertion was weakened.

The obsolete explicit-stride rejection regression now asserts both writers and
the retained ArrayStride decoration. The Uniform independent runner now checks
the formerly unsupported stride through managed native and reverse WGSL/native
outputs, retaining the pinned original reference reader's UnsupportedMatrixStride
diagnostic. Its final run passed **400/400** (previously 394), including two new
GPU readbacks. An initial eight-case independent matrix run passed **128/128**;
its report remains `matrix-layout/initial-eight-checks.json`. Expanded verification
uses 82 exported fixtures: all f16/f32 shapes with both major orders, dynamic
selected stores, whole/root snapshots, array swaps, nested arrays, Uniform buffers
and per-access Volatile/Aligned/Nontemporal flags. The task-only Vulkan runner can
optionally return the entire input buffer; default behavior remains unchanged.
Every expanded path checks output values and all input bytes, including padding,
in separate executions. Uniform Volatile WGSL translation remains an exact
expected storage-root diagnostic; corresponding native paths still execute.

Final maintained suite command:
`.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj --no-restore -c Release --nologo`:
**927 passed**, zero failures/skips, 54 more than the preceding 873. Final harness
build and fixture export both passed:
`.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj --no-restore -c Release --nologo`
(zero warnings/errors), and
`.dotnet/dotnet.exe run --project .work/naga-csharp/matrix-fixtures/matrix-fixtures.csproj --no-restore -c Release --nologo`.
Project/harness assembly SHA256 matches
`80DE35957882BEB95CAAAFBF9F8C74AA11C2F7E36EE4BEA5CC8862344FFF87E7`.
Production is unchanged since those builds and the final runner launches.

Final `python .work/naga-csharp/scan-corpus.py` reached exit 0; every per-input
step code matches `full-corpus/pre-matrix-baseline.json`. Counts remain 197
reference inputs; managed WGSL 191 pass/six fail, native emission 189 pass/eight
fail, native validation 184 pass/five fail, reference native import 162 pass/27
fail, reverse WGSL 185 pass/four fail. All 191/185 WGSL outputs are reference-
accepted. Known corpus failures remain failures. Final atomic/barrier/member/
access/Mesh independent runs also reached terminal exit 0: respectively 231/210/
505/344/665 expected outcomes. The final Workgroup runner also reached terminal
exit 0 at **1188/1188**, including 496 GPU readbacks.

Final `python .work/naga-csharp/check-matrix-layout.py` reached terminal exit 0:
**1920/1920 expected outcomes** across 82 fixtures: 636 successful commands,
four exact expected Uniform Volatile WGSL rejections, **320 successful native
validations**, **640 successful output-value GPU readbacks** and **640 successful
whole-input-byte GPU readbacks**. All bytes, including padding and unselected
matrix/structure elements, matched the independent expectation on Intel UHD.
Native input/managed native roundtrip and both reverse-WGSL compilers execute for
expressible cases; Uniform Volatile executes its two native paths. Array swaps
verify that split writes read both original sources before writing destinations.
All eight runner handles plus the full-corpus handle are now terminal exit 0;
no launched checks remain running. `summarize-matrix-continuation.py` confirms
those report counts and zero corpus step-code deltas. The project/harness assembly
hash was checked again after all runners completed and still matches.

`git diff --check` and `git -C Sia.Graphics diff --check` passed; existing root
CRLF normalization warnings remain. The managed C#/project-file trailing-space
search returned no matches. No service, subagent or worktree was created; no
branch, commit or integration migration was performed. Not run: additional GPU
devices, Mesh/Task/cooperative GPU execution, Uniform descriptor-array GPU
execution, AOT/browser and full integration. Projected matrix pointer helpers,
native-layout module initializers, unresolved whole-array access and shared-type
member-specific combinations still need further implementation/coverage. The
full goal remains active; task data and oracle artifacts remain required for
ongoing work and precise failure reproduction until completion cleanup.

While final checks ran, the remaining `operators.wgsl` failure was reproduced
directly with the harness. Its diagnostic span 1620:2 points to
`false && (0u + 1.0f > 0)`. The pinned source itself labels this a reference
acceptance bug (wgpu issue 8440), not valid mixed concrete arithmetic. Do not
weaken type validation merely to turn that corpus count green. The subgroup
fixture was also inspected: it uses Simple memory model and semantics 136
(AcquireRelease plus SubgroupMemory). Investigate actual model/address-space
equivalence before broadening the shader-class validation; a blanket mask change
would not prove legal Vulkan/WGSL output.

Primary semantic reference: the [SPIR-V specification](https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html)
keeps matrix indices column-based independently of memory decorations, and defines
MatrixStride on a member's matrix or nested matrix array. The pinned Naga reader's
row-major load overrides and UnsupportedMatrixStride guard were inspected.

## Matrix helper and initialization continuation (2026-10-08)

Objective: remove the matrix layout pointer-escape/constant-initializer gaps while
preserving pointer identity, argument evaluation and native memory requirements.
Acceptance: valid original native fixtures, maintained regressions for nested and
aliased helper calls, independent GPU comparison of original/managed/reverse-WGSL
paths, and exact physical initialization values for both major orders. Keep the
full SPIR-V/WGSL goal intact; other formats remain excluded.

Plan: reuse the existing helper expansion engine for non-function pointer calls
before native layout projection; keep function/query behavior covered. Native
Storage/Workgroup derived-pointer calls require the variable-pointer capabilities;
accept declarations only with corresponding provenance-safe lowering and retain
diagnostics for unsupported pointer-producing operations. Reconstruct constant
physical arrays/structures directly rather than inserting initialization stores.
No public abstraction or runtime dependency is needed. Primary sources are the
[SPIR-V specification](https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html),
the [variable pointers extension](https://github.khronos.org/SPIRV-Registry/extensions/KHR/SPV_KHR_variable_pointers.html)
and pinned SPIRV-Tools `source/val/validate_function.cpp` memory-object checks.
Six initial maintained regressions reproduced the capability/initializer guards.
The input fixture builder initially inserted annotations before TypeVoid instead
of the first type declaration; after correction all six original fixtures passed
native validation. Expansion subsequently added whole-matrix Storage/Workgroup
helpers, finite matrix-array initialization, members sharing a matrix value type
but using different major orders/strides, and a later argument mutating the
captured matrix index. Four separate native-valid fixtures cover explicit
diagnostics for selected, merged, loaded and returned pointer values. Their first
validation exposed missing descriptor bindings in the fixture itself; adding the
required set/binding decorations made all four inputs native-valid. These input
setup failures remain in the task evidence and were not production fixes.

Implementation reuses `Proc.QueryHelperInliner` with a private selection mode;
query behavior and public IR remain unchanged. The native reader expands
non-function pointer helpers before matrix projection when member layout or
variable-pointer declarations require it. The native writer also expands private
pointer helpers after input validation. The WGSL writer does not unconditionally
expand private helpers: loop-containing helpers in continuing blocks still need
separate equivalent lowering. Initializers construct physical vectors/arrays/
structures as constant expressions; no runtime initialization writes are added.
Unresolved whole arrays, pointer provenance operations and helper diagnostic
scopes remain explicit limitations. Native source aliases are retained without a
copy-in/copy-out convention; the WGSL fixture text is used only to build those
native inputs through the raw test emitter, not to claim WGSL alias validity.

Final maintained command
`.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`
passed **950 tests**, zero failures/skips (23 more than 927). Harness build
`.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore --nologo`
passed with zero warnings/errors. Fixture export
`.dotnet/dotnet.exe run --project .work/naga-csharp/matrix-helper-fixtures/matrix-helper-fixtures.csproj -c Release --no-restore --nologo`
passed. All **18 positive native inputs** and **four unsupported pointer-producer
inputs** independently passed `spirv-val --target-env vulkan1.2`.
Project/harness assembly SHA256 matches
`8963060E5A3448F5749B9928228A2A2B34B7B188B40CCD7D03904DA2339C9DB4`.
Production source has not changed since the initial successful harness build.

`python .work/naga-csharp/check-matrix-helpers.py --resume` reached terminal exit
0 with **998/998 strict implementation/control checks**, including **840 GPU
readbacks** (420 output values and 420 complete input buffers). Native back,
reverse WGSL compiled by both implementations, and independently expanded native
controls match the same explicit expected values and bytes. Across all paths the
report records **1059 pass / 37 fail / 1096 total**: the 37 failures are retained
external observations, not counted as successful implementation checks.

**35 raw native GPU observations fail** on Intel UHD: six for row-major projected
column helper calls, eleven for padded column-major whole-matrix Storage calls,
twelve for row-major whole-matrix Storage calls, and six for row-major calls with
later index mutation. Untouched SPIR-V inputs pass native validation. Running
SPIRV-Tools `--eliminate-dead-branches --merge-return
--inline-entry-points-exhaustive --eliminate-dead-functions` preserves the logical
shader operation and produces independently native-valid controls that match
all expected outputs and bytes. A plain inline pass was insufficient because
multiple returns prevented full expansion; merge-return first required dead
branch elimination. This establishes a device/compilation-path difference; it
does not establish a general driver bug or excuse a translator mismatch.
The raw failures remain in `first-gpu-row-pointer-failure.json` and the final
`checks.json`. No expectation was changed to match the observed wrong values.
Untouched-native GPU acceptance is not met for those comparisons; independent
controls support the managed logical lowering but do not resolve that external
execution path. The full goal remains active.

**Two reference emitter outputs fail native validation** with
`VUID-StandaloneSpirv-None-10684`: the Workgroup structure receives explicit
Offset/MatrixStride decorations without the necessary explicit Workgroup layout
model. Both WGSL inputs are accepted by the pinned reference, and both managed
native outputs pass native validation and GPU comparisons. Invalid reference
binaries were not executed. This limitation is retained as failed external
checks, rather than reported as an expected-success outcome. See the
[Vulkan standalone SPIR-V rule](https://docs.vulkan.org/spec/latest/appendices/spirvenv.html#VUID-StandaloneSpirv-None-10684).

`python .work/naga-csharp/check-query-helpers.py` reached terminal exit 0:
**34/34 checks**, including eight GPU controls for nested early returns,
short-circuiting, argument evaluation, alias identity and continuing-block calls.
`python .work/naga-csharp/scan-corpus.py` reached terminal exit 0; all per-input
step codes match `full-corpus/pre-matrix-helper-baseline.json`. Counts remain
197 reference inputs, managed WGSL 191 pass/six fail, managed native 189 pass/eight
fail, native validation 184 pass/five fail, reference native import 162 pass/27
fail, and reverse WGSL 185 pass/four fail. All 191/185 WGSL outputs remain
reference-accepted. The final `python .work/naga-csharp/check-matrix-layout.py`
rerun reached terminal exit 0: **1920/1920 expected outcomes**, including 320
native validations and 1280 GPU readbacks. The four Uniform Volatile WGSL
storage-root rejections remain exact expected diagnostics, not newly accepted
shaders. All 82 fixtures retain correct values and every input byte.

Private pointer emission additionally uses a reference-accepted WGSL source with
two distinct private arrays and dynamic element addresses. Its first vector-based
version was invalid WGSL: the reference correctly rejects taking a vector
component's address, while the managed frontend currently accepts it. The fixture
was corrected to arrays; preserve `first-private-vector-source.wgsl` as a pending
frontend-validation reproduction, not as a valid shader requirement. This is a
separate remaining correctness gap.

`python .work/naga-csharp/check-private-helpers.py` reached terminal exit 0:
**92/92 strict checks**, including **80 GPU readbacks** (40 outputs and 40 input
buffers) across managed native, native roundtrip, both reverse-WGSL compilers and
a hand-expanded reference control. The report also retains **one failed external
reference emission**: the reference accepts the legal source but panics with
`Expression [6] is not cached!` at pinned `back/spv/block.rs:3847`.
`first-private-reference-panic.json` retains that first failure. No invalid or
missing reference binary was executed. The manually expanded control preserves
argument evaluation and compares against the same explicit values/bytes.

All launched corpus, helper, private-pointer, query-control and matrix-layout
runners have reached terminal results; no live check handle remains. Final
project/harness hash still matches the value above. `git diff --check` and
`git -C Sia.Graphics diff --check` passed, with existing root CRLF normalization
warnings. The C#/project trailing-space search returned no matches. No dependency,
service, worktree, subagent, branch or commit was added. Other workspace changes
were preserved. Not run: broader devices, AOT/browser, full Rust-consumer
migration, Mesh/Task/cooperative GPU execution or specialization-valued matrix
initializers. Active task fixtures, oracle artifacts and failure reproductions
remain required for continuing the full goal; completion cleanup is not yet due.

## Pointer addressability and selection continuation (2026-10-08)

Objective: enforce WGSL addressability without forbidding native scalar vector
places in the shared IR, then lower native pointer selection to selected memory
operations. Acceptance: maintained failing addressability regressions first;
reference-accepted valid controls; native-valid selection fixtures; independent
native/reverse-WGSL GPU outputs and complete buffer comparisons; no newly lost
memory requirements or unselected reads/writes. Keep the full translation goal.

Plan: reject vector-component address-of in the WGSL frontend and avoid creating
such addresses when the existing helper inliner captures a store destination.
Keep ordinary vector component assignment legal. Use private reader lowering for
pointer Select expressions, after helper expansion and before layout projection;
capture native conditions/indices at their instruction evaluation point and
rewrite loads/stores/builtins to control flow, preserving alias and memory
attributes. No public IR variant or new dependency is needed. Pointer phi/load/
return provenance remains subsequent work rather than an accepted limitation of
the overall objective. Primary WGSL evidence is the pinned frontend's AddrOf
branch and [address-of rule](https://gpuweb.github.io/gpuweb/wgsl/#address-of-expr).

Implemented WGSL vector-component address rejection with source spans, retained
valid scalar/composite addresses and component assignment, and corrected helper
store-target capture to keep the vector pointer and pre-call index separately.
The shared validator still accepts native vector component places. The reverse
writer diagnoses a remaining explicit component address requiring projection.
The first 11 maintained negative address cases failed before the frontend fix;
the helper capture control now changes the index inside the called helper and
checks that the assignment uses the original component.

Native pointer Select now retains instruction-time condition/index snapshots and
expands into selected loads, stores and builtin calls after helper expansion.
Storage, Workgroup, nested choices, aliasing helper arguments, aggregate copies,
vector components, memory operands and atomic operations have maintained cases.
Mixed ordinary/atomic scalar and array cases first produced four IR-validation
failures. Candidate atomic-member closure repairs corresponding pointees without
changing buffer offsets or copying atomic objects; additional structure controls
cover a partially atomic aggregate. Missing capability, restricted Workgroup and
cross-buffer selection have explicit maintained rejection checks. Full-capability
cross-buffer controls remain accepted. The distinction follows the primary
[variable pointer extension](https://github.khronos.org/SPIRV-Registry/extensions/KHR/SPV_KHR_variable_pointers.html).

Before the final capability/vector/structure additions, the 26-case independent
selection run reached terminal exit 0: 1838 successful checks and two failed
external reference Workgroup native validations (1840 recorded checks). The
reference failures remain VUID-StandaloneSpirv-None-10684; invalid outputs were
not executed. Preserve this baseline separately from final-version evidence.
The first qualified atomic fixture omitted the Vulkan memory model required for
Volatile atomic semantics. Its two invalid native inputs were corrected before
using them as positive requirements; the initial failure report remains in
`pointer-selection/first-atomic-memory-model-failure.json`. This was a fixture
setup defect, not evidence of translator failure. The maintained matrix-helper
Select case changed from an unsupported-producer rejection to a positive
roundtrip because this producer is now implemented; phi/load/return negative
checks remain intact.

Final commands from the workspace root reached terminal results:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`:
  **993 passed, zero failed/skipped**. The harness Release build also passed.
- `.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore --nologo`
  exported 38 native fixtures. `python .work/naga-csharp/check-pointer-selection.py`
  recorded **2702 successful checks and two failed external checks**, total2704.
  All 38 original native inputs and both managed native paths passed native
  validation. **2400 GPU readbacks passed**, comprising1200 output comparisons
  and1200 complete input-buffer comparisons across original native, managed
  roundtrip and reverse-WGSL compiled by both implementations. Every unselected
  field and sentinel is checked. The two known reference Workgroup outputs still
  fail VUID-StandaloneSpirv-None-10684 and were excluded from GPU execution; both
  WGSL sources are reference-accepted. All original-native GPU observations pass.
- `python .work/naga-csharp/check-pointer-address.py`: **73/73 expected outcomes**,
  including 11 invalid/6 valid address sources checked by both frontends and
  **eight GPU readbacks** checking helper store-target evaluation. The device
  lacks ray-query support: these controls remove only proven-unused opaque
  declarations after expansion, reject surviving handle use and compare with a
  separately compiled scalar-handle control. No traversal result is claimed.
- `python .work/naga-csharp/check-atomic-memory.py`: **231/231 expected outcomes**,
  including33 GPU readbacks. `python .work/naga-csharp/check-query-helpers.py`:
  **34/34 checks**, including eight GPU controls.
- `python .work/naga-csharp/scan-corpus.py`: terminal exit0, **197 reference
  inputs**, all per-input step codes equal
  `full-corpus/pre-pointer-selection-baseline.json`. Managed WGSL remains191
  pass/six fail; managed native189 pass/eight fail; native validation184 pass/five
  fail; reference native import162 pass/27 fail; reverse WGSL185 pass/four fail.
  All191/185 WGSL outputs remain reference-accepted. These pre-existing failures
  are retained, not reclassified as successful translations.
- `python .work/naga-csharp/summarize-pointer-continuation.py` confirms the corpus
  comparison and equal project/harness assembly SHA256:
  `A8ECDB646DDFDCEAE02A341765AD78C4FA65153A0FE3A2DBE3075D7206D4BEB2`.
  The selection runner independently verifies its assembly hash at both ends;
  this final run did not reuse earlier case results. Reports and raw failure
  reproductions stay under `.work/naga-csharp`.

`git diff --check`, `git -C Sia.Graphics diff --check` and the C#/project
trailing-space scan passed (the scan returned no matches). Root diff checking
retains existing CRLF normalization warnings. No live check handles remain.
No service, worktree, dependency, subagent, branch or commit was added. Existing
workspace changes were preserved. Not run: other devices, AOT/browser,
ray-traversal GPU execution, complete Rust-consumer migration or exhaustive
extension equivalence. Mixed read-only/descriptor-array pointer provenance and
pointer phi/load/return remain subsequent work. The full goal is active; its
oracle, fixtures and failure reproductions remain required, so completion cleanup
is not yet due.

## Mixed pointer memory continuation (2026-10-08)

Previous goal turn was concrete progress: native Select lowering, addressability
regressions,993 maintained tests and independently verified GPU paths. This
continuation keeps the full port objective. Scope: replace atomic candidate-type
closure with per-selected-leaf memory lowering so a read-only ordinary resource
can participate without becoming an atomic resource. Preserve native ordinary
memory operands, actual atomic scopes/semantics and aggregate snapshot order.
Reuse the reader's existing memory/copy routines; no public IR or dependency is
planned. Establish native-valid scalar/array/structure reproductions with both
arm orders before changing production code. Acceptance: maintained regressions,
independent native/reverse-WGSL validation and GPU values/full input bytes, the
existing38 selection controls, atomic checks and unchanged corpus outcomes.
Inspect phi handling alongside this work; full phi/load/return provenance remains
required subsequent work if it cannot be completed in the same continuation.

The six maintained scalar/array/structure regressions first failed because the
read-only candidate had acquired atomic members. All12 exported combinations
(both arm orders and both memory-operand settings) independently passed native
validation before production changes. Native names are not source names; tests
identify the resource by its binding rather than assuming OpName preservation.

Removed candidate atomic-type closure. Pointer projection now derives each
branch's member/index type and access rights from its actual parent. Selected
ordinary loads/stores reuse the reader's existing memory and atomic-aggregate
copy routines after dispatch. This handles both an atomic first arm and an
ordinary first arm, including partially atomic structures. Only actual native
atomic instructions upgrade their reachable memory. Ordinary accesses to an
atomic place always carry MemoryAccess, using an explicit zero mask when the
original instruction omitted operands; actual atomic operations retain their
separate scope/semantics. Native writing therefore preserves ordinary OpLoad/
OpStore instead of silently adding atomic operations. Maintained checks assert
the read-only resource has no atomic leaves, that unrelated writable candidates
remain ordinary, and that native output contains its one original atomic add
with no added atomic load/store.

One intermediate projection change lost the Function pointer type of an SSA
store target captured by the helper inliner. The two existing helper-selection
regressions exposed it. Retaining the original address type for value-typed
function places corrected the issue; no assertion was removed or weakened.

Final commands from the workspace root reached terminal results:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`:
  **999 passed, zero failures/skips**, including the stronger native-opcode
  assertions. The harness Release build passed with zero warnings/errors.
- `.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore --nologo`
  exported the new12 and existing38 fixtures.
  `python .work/naga-csharp/validate-readonly-selection-inputs.py`: **12/12
  original native inputs valid**.
- `python .work/naga-csharp/check-readonly-pointer-selection.py`: **864/864
  checks**, including **768 GPU readbacks** (384 outputs and384 complete input
  buffers) across original native, managed native roundtrip and reverse-WGSL
  compiled by both implementations. The input bytes remain unchanged; output
  values, the atomic target and all sentinels are compared explicitly.
- `python .work/naga-csharp/check-pointer-selection.py`: **2702 successful checks
  and two failed external checks**, total2704; **2400 GPU readbacks pass** for
  all38 prior fixtures. The same two reference Workgroup outputs fail native
  VUID-StandaloneSpirv-None-10684. Both WGSL sources remain accepted; invalid
  reference binaries were not executed. All original and managed native
  validation/GPU paths pass.
- `python .work/naga-csharp/check-atomic-memory.py`: **231/231 expected outcomes**,
  including33 GPU readbacks. No memory requirement or expected rejection was
  loosened.
- `python .work/naga-csharp/scan-corpus.py`: terminal exit0; **197 reference inputs**.
  All per-input step codes match `full-corpus/pre-mixed-pointer-memory-baseline.json`.
  Counts remain managed WGSL191 pass/six fail, managed native189 pass/eight fail,
  native validation184 pass/five fail, reference native import162 pass/27 fail,
  reverse WGSL185 pass/four fail; all191/185 WGSL outputs remain reference-accepted.
- `python .work/naga-csharp/summarize-mixed-pointer-memory.py` verifies corpus
  equality, exact external failures and matching final project/harness/runner
  assembly SHA256:
  `87FD69716BF0DFB02A8F1BD485B125B193D1EEE46958F9A00CEE7B7EB2FA89F5`.
  Both selection runners verify their start/end assembly hash and reran every
  case rather than reusing old pass records. Earlier selection evidence remains
  in `pointer-selection/pre-leaf-memory-checks.json`.

The source inspection confirms that merely removing the pointer-Phi guard would
not implement it: ResolveFunctions.Edge currently makes simultaneous ordinary
value snapshots/stores, while pointer-result declarations are deliberately
absent. A complete implementation must carry both selected provenance and
instruction-time index values across edges, including loop backedges and
simultaneous pointer phis. Keep this work distinct from treating pointer values
as WGSL mutable variables. Pointer load/return and descriptor-array provenance
also remain required; no reduced success criterion is adopted.

Root/Graphics `git diff --check` passed; root retains existing CRLF normalization
warnings. The C#/project trailing-space scan found no matches. All launched
check handles are terminal. No dependency, public IR variant, service, worktree,
subagent, branch or commit was added; unrelated changes remain preserved. Not
rerun: the historical full matrix/member suites, address/query GPU controls
whose paths this conditional native selection pass does not affect, other
devices, browser/AOT, ray traversal or Rust-consumer migration. The full goal is
active and its fixtures/oracle/failure evidence remain required; completion
cleanup is not yet due.

## Pointer Phi continuation (2026-10-08)

Previous goal turn was progress: per-selected-leaf ordinary/atomic memory
lowering,999 maintained tests and native/GPU evidence. This continuation targets
pointer Phi across branches and loops, including simultaneous cyclic phis and
branch-local indices. Private native normalization will represent finite pointer
provenance using scalar selector/index SSA phis, then reconstruct pointer choices
after the phi group for the existing reader lowering. Preserve original pointer
result IDs, decorations, instruction-time indices and memory operations; do not
introduce a public IR concept or dependency. Acceptance: native-valid failing
fixtures first, independently valid normalized intermediates, maintained
roundtrips, GPU values and full-buffer comparisons for branches/loops, unchanged
existing selection/atomic/corpus checks. Pointer load/return and descriptor-array
coverage remain part of the full goal rather than exclusions.

Implemented a private native pointer-Phi normalization pass before the existing
reader. It traces finite Variable/FunctionParameter, AccessChain, CopyObject,
Select and Phi provenance, computes reachable address shapes, and carries the
selector plus each dynamic index in ordinary scalar SSA phis. Pointer choices
are reconstructed after the complete phi group. Original pointer result IDs
and decorations remain intact. The existing simultaneous edge snapshots handle
self and mutually dependent loop phis. Same-root/different-index paths share
one shape while retaining the selected index. Original header bounds and
variable-pointer capabilities are checked before allocating generated IDs.
No public IR variant or dependency was added. Loaded, returned and null pointer
provenance, pointer arithmetic and broader descriptor-array coverage remain
unimplemented work rather than accepted exclusions.

The initial maintained regressions exposed duplicate local names because several
generated instructions share their original Phi word offset. A private capture
counter makes index/condition names unique while retaining diagnostic offsets.
The first exported loop fixtures accidentally removed the native entry wrapper;
independent validation rejected those fixtures before GPU execution. Corrected
fixture construction preserves the wrapper, and the failed fixture evidence is
retained separately. The previous unsupported-Phi assertion now tests successful
lowering; unsupported pointer load/return assertions remain. The new17 maintained
cases cover branch-local addresses/indices, nested choices, self/mutual loop phis,
same-array index selection and capability/bound/predecessor rejection.

Final commands from the workspace root reached terminal results:

- `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`:
  **1016 passed, zero failures/skips**.
- `.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore --nologo`
  exported38 original/normalized Phi pairs alongside the existing selection
  fixtures. All38 originals and all38 normalized binaries independently pass
  native validation for Vulkan1.2.
- `python .work/naga-csharp/check-pointer-phi.py`, followed by
  `python .work/naga-csharp/check-pointer-phi.py --resume` after adding five
  same-array cases: **3331 successful checks and three failed external checks**,
  total3334. All **2992 GPU readbacks** pass:1496 outputs and1496 complete input
  buffers across original native, normalized native, managed native roundtrip
  and reverse WGSL compiled by reference/managed implementations. Eight distinct
  inputs per fixture exercise choices, indices and untouched sentinels. The
  resume path verifies the saved assembly hash before reusing completed cases;
  both runs used the same assembly. Three reference Workgroup outputs fail native
  VUID-StandaloneSpirv-None-10684 and were not executed. All original, normalized
  and managed native validation/GPU paths pass.
- `python .work/naga-csharp/check-pointer-selection.py`: **2702 successful checks
  and two failed external checks**, total2704, with **2400 GPU readbacks passing**.
  The same two reference Workgroup outputs fail the native VUID above. No existing
  original/managed path regressed.
- `python .work/naga-csharp/check-atomic-memory.py`: **231/231 expected outcomes**,
  including33 GPU readbacks.
- `python .work/naga-csharp/scan-corpus.py`: terminal exit0; all per-input step
  codes for **197 reference inputs** match
  `full-corpus/pre-pointer-phi-baseline.json`. Counts remain managed WGSL191
  pass/six fail, managed native189 pass/eight fail, native validation184 pass/five
  fail, reference native import162 pass/27 fail, reverse WGSL185 pass/four fail;
  all191/185 WGSL outputs remain reference-accepted.
- `python .work/naga-csharp/summarize-pointer-phi.py` checks corpus equality,
  all76 input/intermediate validation results, the exact external failures and
  matching project/harness/selection-runner assembly SHA256:
  `12C5EE842AE5AF6A4B010BBDA875AD68B4ADA264ABCF8CA8BD9D99FDC28F1A33`.

Root/Graphics `git diff --check` and the C#/project trailing-space scan pass.
All launched check handles are terminal. No service, worktree, subagent, branch
or commit was added; unrelated changes remain preserved. Not rerun: historical
matrix/member/address/query/read-only GPU controls, other devices, browser/AOT,
ray traversal and Rust-consumer migration. The no-Phi fast path keeps those
native inputs out of normalization, and the full maintained suite passes; this
does not establish additional GPU/device coverage. The full goal remains active.
Its oracle, fixtures, summaries and failure evidence are still needed, so task
completion cleanup is not yet due.

## Pointer memory continuation (2026-10-08)

Previous goal turn made progress: finite pointer Phi normalization,1016 tests
and2992 new GPU readbacks. Both minimal pointer-load and pointer-return native
inputs independently pass Vulkan1.2 validation. This continuation first targets
Function/Private pointer slots: represent their stored finite provenance as
ordinary mutable selector/index state, snapshot it at each native pointer load,
then reuse existing pointer-choice memory lowering. Keep dynamic index evaluation,
slot alias identity, branch/loop writes and ordinary/atomic memory behavior.
No public IR variant or dependency is planned. Acceptance: initially failing
maintained regressions, independently native-valid original/normalized inputs,
GPU output/full-input comparisons and unchanged Phi/selection/atomic/corpus
controls. Pointer-return specialization and broader pointer-slot aggregates
remain required subsequent work, not scope exclusions.

The first eight maintained slot regressions failed at the existing pointer-load
guard, after all eight original inputs independently passed native validation.
Extended the private Phi normalization with direct Function/Private slots and
CopyObject/empty-AccessChain aliases. Stored finite provenance is mutable
selector/index memory; every load creates fresh scalar SSA snapshots and
reconstructs its pointer from them. Load sources participate in the same finite
fixed point as Phi/Select/access chains, including cycles across loop slot writes
and mixed load/Phi graphs. Original slot IDs remain selector-variable IDs, and
original load result IDs remain reconstructed pointer IDs. Slot load/store
memory operands are retained for each scalar state access. Cross-function
parameter roots, slot memory copies/helper arguments and unknown aliases retain
explicit diagnostics. Undefined stored pointer IDs now produce a parse diagnostic
even in a module with no Phi instructions.

An intermediate version declared generated Private globals before their new
pointer types. Two maintained tests exposed it; deferring those globals until
after the generated module types/constants corrected ordering. Explicit volatile
slot masks exposed the existing WGSL Function/Private volatile limitation, also
covered by MemberMemoryTests. Normal aligned/nontemporal slot cases continue
full roundtrips; separate volatile cases assert retained native load/store flags,
native roundtrips and the existing explicit WGSL diagnostic. This does not claim
volatile WGSL support or drop those requirements. No existing rejection assertion
was weakened: the minimal supported load case moved from a rejection theory to
the successful roundtrip theory; pointer-return rejection remains.

The first46-case GPU run finished with3954 successful checks and four reference
Workgroup native-validation failures, including3552 successful GPU readbacks.
Further version coverage exposed missing SPIR-V1.4+ entry interfaces for the new
Private index variables. An independently valid1.4 input and its rejected
intermediate are recorded in `pointer-memory/private-interface-first-failure.json`.
The pass now lists all generated Private state in modern entry interfaces.
Added1.4/1.5 maintained interface regressions and six independent version cases.
Earlier check/assembly records are retained under `pre-interface-*`; final checks
rerun against the corrected assembly rather than reusing those pass records.

Maintained suite command
`.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`
passed **1033 tests, zero failures/skips**. The Release harness build passed with
zero warnings/errors. The fixture exporter produced52 new cases:50 full-roundtrip
cases and two explicit volatile native-only cases. All52 originals and all52
normalized intermediates passed independent Vulkan1.2 native validation before
GPU execution.

Final commands from the workspace root reached terminal results after the
interface correction:

- `.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore --nologo`:
  zero warnings/errors. The maintained suite command above passed1033 tests.
- `.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore --nologo -- memory`
  exported52 original/normalized pairs. `pointer-memory/native-validation.json`
  records all104 independent native validations passing, including1.4/1.5.
- `python .work/naga-csharp/check-pointer-memory.py`: **4454 successful checks
  and six failed external checks**, total4460. All **4000 GPU readbacks** pass:
  2000 outputs and2000 complete input buffers on Intel UHD Graphics. The50 ordinary
  cases exercise original native, normalized native, managed native roundtrip,
  reference reverse WGSL and managed reverse WGSL paths; six invalid reference
  Workgroup outputs were not executed. Those outputs fail the previously recorded
  VUID-StandaloneSpirv-None-10684. The two explicit volatile slot cases pass their
  three native validation/GPU paths and retain the expected WGSL rejection.
  Every original, normalized and managed native input validates. Each path uses
  eight inputs; full buffers check unselected values, indices and sentinels.
- `python .work/naga-csharp/check-pointer-phi.py`: **3331 successful checks and
  three failed external checks**, total3334; all2992 GPU readbacks pass. The same
  reference Workgroup native VUID remains the only failure class.
- `python .work/naga-csharp/check-pointer-selection.py`: **2702 successful checks
  and two failed external checks**, total2704; all2400 GPU readbacks pass. The
  same two reference Workgroup native VUID failures remain.
- `python .work/naga-csharp/check-atomic-memory.py`: **231/231 expected outcomes**,
  including33 successful GPU readbacks.
- `python .work/naga-csharp/scan-corpus.py`: terminal exit0; all per-input step
  codes for **197 reference inputs** match
  `full-corpus/pre-pointer-memory-baseline.json`. Counts remain managed WGSL191
  pass/six fail, managed native189 pass/eight fail, native validation184 pass/five
  fail, reference native import162 pass/27 fail, reverse WGSL185 pass/four fail;
  all191/185 WGSL outputs remain reference-accepted.
- `python .work/naga-csharp/summarize-pointer-memory.py` verifies corpus equality,
  all52 original/intermediate/back native results, both expected volatile WGSL
  rejections, the exact external failures and matching project/test/harness/
  memory/Phi/selection-runner assembly SHA256:
  `EF6EC88939B096ADE50A43C7E3668E6639FBF480795B54ED314C968A122A62DB`.
  All three pointer runners verify their assembly hash at start/end. Final runs
  executed all cases rather than resuming evidence from an earlier assembly.

Root/Graphics `git diff --check` passed; the C#/project trailing-space scan found
no matches. All launched check handles are terminal. No dependency, public IR
variant, service, worktree, subagent, branch or commit was added; unrelated
changes remain preserved. Not rerun: historical matrix/member/address/query/
read-only GPU controls, other devices, browser/AOT, ray traversal and Rust-consumer
migration. Input normalization has a no-Phi/no-pointer-load fast path; the full
maintained suite passes, but that is not additional device/GPU coverage. Slot
memory copies/helper arguments, pointer-return/null, aggregate slots and
cross-function parameter roots remain incomplete. Native volatile Function/Private
memory still requires equivalent WGSL lowering. The full goal is active; oracle,
fixtures, summaries and failure evidence remain needed, so completion cleanup
is not yet due.

## Pointer-slot transfer continuation (2026-10-08)

Previous goal turn made progress: direct pointer-slot loads,1033 maintained
tests and4000 new GPU readbacks. Continue with native slot CopyMemory and
slot/helper parameter provenance, preserving aliases, scalar index snapshots,
memory masks and modern entry interfaces. Establish independently native-valid
failing copy/cyclic-copy/helper fixtures first. Reuse private scalar state and
the existing helper inliner; no public IR variant or dependency is planned.
Acceptance: maintained/native/reverse-WGSL validation, GPU output/full-buffer
comparisons, existing slot/Phi/selection/atomic controls and unchanged corpus
outcomes. Pointer returns/null, aggregate slots and other recorded gaps remain
required work rather than exclusions.

Implemented finite slot CopyMemory, cyclic/self/cross-address-space copies,
slot-reference helper parameters, stored-pointer value parameters and nested
forwarding. Connected slot copies/aliases share a shape domain. Call arguments
establish parameter provenance; expanded signatures pass scalar values for
pointer values and scalar-cell addresses for slot references. The existing
inliner expands affected helpers, including Function-space state, so two helper
parameters naming one slot retain their alias. Separate source/target memory
masks and modern Private entry interfaces remain intact. Write-only slots also
normalize when no pointer load/Phi is present. No public IR variant or dependency
was added.

Ten independently native-valid initial fixtures reproduced the prior copy/helper
rejections. The first implementation passed those cases but an overloaded private
Run method broke two existing reflection-based normalization tests. Renaming the
helper-reporting method fixed that ambiguity without weakening assertions.
Nineteen maintained tests now cover copies/helpers, aliased helper edits, dual
native masks, write-only slots and mismatched helper types/address spaces. Final
`.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`
passed **1052 tests**, zero failures/skips. Harness Release build passed with zero
warnings/errors. Production assemblies remain frozen during independent checks.

`.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore --nologo -- transfer normalized`
exported76 cases. All **152 original/normalized native validations** pass.
`python .work/naga-csharp/check-pointer-slot-transfer.py` reached terminal exit0:
**6560 successful checks and12 failed external checks**, total6572. All **5888 GPU
readbacks** pass on Intel UHD Graphics:2944 outputs and2944 complete input buffers.
Cases cover scalar/array/atomic/Workgroup/mixed aggregates, loop swaps, aliases,
nested helper writes,1.4/1.5 interfaces and write-only slots. Original, normalized,
managed native roundtrip and both valid reverse WGSL paths use eight seeds.
The12 failed reference Workgroup outputs have the known
VUID-StandaloneSpirv-None-10684; invalid reference outputs were not executed.
Every managed native output validates.

Existing controls reached terminal exit0 against the same frozen assembly:

- `python .work/naga-csharp/check-pointer-memory.py`:4454/4460 successful checks,
  six known external Workgroup failures; all4000 GPU readbacks pass.
- `python .work/naga-csharp/check-pointer-phi.py`:3331/3334 successful checks,
  three known external Workgroup failures; all2992 GPU readbacks pass.
- `python .work/naga-csharp/check-pointer-selection.py`:2702/2704 successful checks,
  two known external Workgroup failures; all2400 GPU readbacks pass.
- `python .work/naga-csharp/check-atomic-memory.py`:231/231 expected outcomes,
  including33 GPU readbacks.
- `python .work/naga-csharp/scan-corpus.py`:197 inputs; all per-input step codes
  match `full-corpus/pre-pointer-slot-transfer-baseline.json`. Counts remain
  managed WGSL191 pass/six fail, managed native189 pass/eight fail, native
  validation184 pass/five fail, reference native import162 pass/27 fail, reverse
  WGSL185 pass/four fail; all191/185 WGSL outputs remain reference-accepted.
- `python .work/naga-csharp/summarize-pointer-slot-transfer.py`:exit0. Verifies
  corpus equality,76 new and52 previous original/intermediate/back native cases,
  independent native validation records, expected volatile WGSL rejections,
  exact external failures and matching project/test/harness/four pointer-runner
  assembly SHA256:
  `9EDEC1ADDBA2FE1B60D611A9AAAAE11FA060D6CC5AE6F93798D879109991EBFE`.
  Each pointer runner checks its assembly at start/end; runs did not resume
  prior-assembly results. Total GPU coverage this continuation is15313 successful
  readbacks, including the5888 new transfer readbacks.

Read-only follow-up investigation confirmed a separate remaining gap.
`pointer-slot-transfer/cross-buffer-slot-limited.spv` uses
VariablePointersStorageBuffer and branch stores of addresses from two buffers
into one slot. Independent Vulkan1.2 validation passes; managed import fails
because the synthesized pointer Select is subject to the single-buffer limit.
`cross-buffer-slot-first-failure.json` retains exact results. Preserve rejection
of genuinely invalid original limited-capability Select/Phi inputs when fixing
this; do not broadly enable the full capability to bypass validation.

Correction from the next normative audit: that input is invalid despite the
native validator's success. The same-structure restriction also applies to
loaded variable pointers. The investigation and withdrawal below supersede this
paragraph's validity inference; the existing rejection was correct.

Root and Graphics `git diff --check` passed; the C#/project trailing-space scan
found no matches. All launched checks are terminal. No service, worktree, subagent,
branch or commit was created; unrelated source/policy changes remain preserved.
Not rerun: historical matrix/member/address/query/read-only GPU controls, other
devices, browser/AOT, ray traversal and Rust-consumer migration. Returned/null
pointers, aggregate slot addresses, unknown parameter roots and native volatile
Function/Private WGSL equivalence remain incomplete. The full goal remains active;
task fixtures, oracle and failure evidence are still needed for the next stage,
so task-completion cleanup is not yet due.

## Cross-buffer capability audit (2026-10-08)

Previous turn made progress: finite slot transfers are implemented and verified;
the native-validator-accepted cross-buffer reproduction changed this turn's next
action, but validator acceptance did not establish normative validity.
Initial objective: investigate distinct-buffer slot provenance while preserving
source capability checks.
Scope: private pointer normalization/reader provenance and maintained/independent
regressions. Acceptance: original/intermediate/back native checks, reverse WGSL,
GPU output and complete input buffers, explicit invalid original Select/Phi
rejections, maintained suite and unchanged corpus. Sync/setup were already
successful earlier in this same task/worktree and are not repeated.

An experimental normalizer identified reconstructed Select IDs and promoted the
internal native capability while retaining the original declaration in reader
checks. GPU investigation prompted a normative audit before accepting that
change. The [current SPIR-V validation rules](https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html)
and [variable-pointer extension](https://github.khronos.org/SPIRV-Registry/extensions/KHR/SPV_KHR_variable_pointers.html)
require every limited-capability variable pointer, including a loaded pointer,
to remain in one structure. The experiment therefore accepted invalid inputs
and was completely withdrawn from production. No capability promotion or origin
whitelist remains. Native validator success alone was insufficient evidence.

Focused command
`.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo --filter FullyQualifiedName~PointerCrossBufferSlotTests`
initially passed16 tests against the withdrawn experiment. Initial loop fixture
generation incorrectly retained scalar Phi
predecessors when splitting entry blocks, then put new Function locals into a
helper rather than the owning caller. Correcting these test-input bugs fixed the
three predecessor failures and remaining unknown-reference failure. No assertion
was removed to hide a failure. Once the normative requirement was established,
the new positive cases were corrected to declare full VariablePointers; six
negative cases retain original Select/Phi and loaded-slot rejection under the
limited capability. These20 cross-buffer maintained tests pass with capability
validation preserved. Invalid earlier inputs/results remain separately classified
under `pointer-cross-buffer/withdrawn-limited-inputs/` and are not valid GPU
equivalence evidence. The expanded34 full-capability cases are independently
native-valid; original loop execution on Intel UHD Graphics still differs from
the scalar expectation, while managed native output matched the examined seed.
This is an unresolved native execution difference, not yet a proven driver bug
or an implementation fix. Keep the original failed gate and investigate further.

## Finite pointer-return continuation (2026-10-08)

Objective: implement native finite pointer returns, including conditional early
returns, nested calls and slot-loaded values, without a public IR pointer-return
variant or new dependency. Acceptance: maintained regressions, independent native
original/intermediate/back validation, reverse WGSL and GPU output/full-buffer
equivalence, previous pointer controls and unchanged corpus outcomes.

Function return sources now join the existing finite provenance graph. Internal
return types are scalar selector/index structures; ReturnValue constructs that
state, calls extract fresh SSA snapshots and reconstruct the original result ID.
Expanded native signatures and the existing helper inliner handle nested return
helpers, including helpers with no pointer arguments. Original capabilities and
type/bound validation remain in effect. The former minimal pointer-return rejection
test now asserts successful roundtrips because the requested behavior is implemented;
the new regressions retain capability and malformed-return diagnostics.

Sixteen new return regressions cover scalar/array/mixed/atomic/Workgroup/cross-buffer
values, early returns, nested calls, Function/Private slot loads, loop snapshots,
missing capabilities and malformed returns. Together with20 capability-audit
regressions, the maintained suite passes **1088 tests**, zero failures/skips:
`.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`.
One test-source spread-argument syntax error was corrected before execution.
Harness Release build passes with zero warnings/errors. Frozen assembly SHA256:
`04B3C750C587C1CC34F439B2C1C87513172098DBC84FC636E2F7F0D8F8D7CEF1`.

`.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore --nologo -- returns`
exports66 cases. All **132 original/intermediate native validations** pass.
`pointer-return/first-failure.json` records the previous experimental assembly's
explicit pointer-return rejection on an independently valid input; that experiment
had not implemented returns. The earlier minimal native return fixture also
reproduced the original rejection.

All final verification commands reached terminal state against the frozen assembly:

- `python .work/naga-csharp/check-pointer-return.py`:5670/5682 successful checks;
 12 known external reference Workgroup validation failures. All **5088 GPU
 readbacks** pass:2544 outputs and2544 complete input buffers on Intel UHD Graphics.
 Original, normalized, managed native roundtrip and both valid reverse WGSL paths
 use eight seeds; invalid reference Workgroup outputs were not executed. Every
 managed native result validates.
- `python .work/naga-csharp/check-pointer-slot-transfer.py`:6560/6572,12 known
 external failures; all5888 GPU readbacks pass.
- `python .work/naga-csharp/check-pointer-memory.py`:4454/4460,six known external
 failures; all4000 GPU readbacks pass, including both expected volatile WGSL
 rejections in their native-only cases.
- `python .work/naga-csharp/check-pointer-phi.py`:3331/3334,three known external
 failures; all2992 GPU readbacks pass.
- `python .work/naga-csharp/check-pointer-selection.py`:2702/2704,two known external
 failures; all2400 GPU readbacks pass.
- `python .work/naga-csharp/check-atomic-memory.py`:231/231,including33 GPU readbacks.
- `python .work/naga-csharp/scan-corpus.py`:197 inputs,unchanged per-input step
 codes versus `full-corpus/pre-pointer-cross-buffer-baseline.json`. Counts remain
 managed WGSL191 pass/six fail, managed native189 pass/eight fail, native
 validation184 pass/five fail, reference native import162 pass/27 fail, reverse
 WGSL185 pass/four fail; all191/185 WGSL outputs remain reference-accepted.
- `python .work/naga-csharp/summarize-pointer-return.py`:exit0; verifies corpus
 equality,66/76/52 original/intermediate/back native cases, independent native
 validations, exact external failure classes and matching project/test/harness/
 five-pointer-runner assembly hashes. Pointer runners check hashes at start/end;
 none resumed older-assembly results. Total return plus prior-control GPU coverage
 is **20401 successful readbacks**. Task-only older pointer runners now checkpoint
 once per completed case and immediately on failure; all comparisons and failure
 assertions remain intact, avoiding quadratic report rewriting.

Separate investigation command
`python .work/naga-csharp/capture-cross-loop-difference.py` records two input seeds
across original/normalized/managed/reference native paths, with all four native
validations passing. `pointer-cross-buffer/full-capability-loop-difference.json`
retains **two failed output comparisons** (original and normalized seed0), six
passing outputs and eight passing full input-buffer comparisons. Managed and
reference paths match both expected outputs and buffers. Removing nontemporal
hints did not resolve the observed original-native difference. This probe's
exit0 asserts only managed/reference agreement; it does not certify all native
paths. The full cross-buffer GPU runner's earlier failed gate remains retained.
Do not count these16 probe comparisons as part of the20401 successful controls.
Full34-case cross-buffer GPU verification has not completed; investigate the
native loop difference and confirm on another device before claiming equivalence.

Root/Graphics `git diff --check` pass; the C#/project trailing-space scan finds
no matches. All launched verification handles are terminal. No dependency,
public IR variant, service, worktree, subagent, branch or commit was added.
Unrelated changes remain preserved. Not rerun: historical matrix/member/address/
query/read-only GPU controls, other devices, browser/AOT, ray traversal and Rust
consumer migration. Null pointers, aggregate slot addresses, unknown/uncalled
parameter contexts and native volatile Function/Private WGSL equivalence remain
incomplete. The goal remains active; oracle, active fixtures and failure evidence
are needed for subsequent work, so task-completion cleanup is not yet due.

## Null-pointer continuation (2026-10-08)

Objective: preserve finite null provenance through native pointer choices, Phis,
Function/Private slots, helper parameters and returns, with defined null guards
before memory access. Scope remains SPIR-V/WGSL in the managed module. Acceptance
uses maintained tests, independent native validation, reference WGSL acceptance,
GPU output/full-input comparison and the existing197-input corpus.

The private provenance pass reserves selector0 for null and reconstructs typed
`OpConstantNull` values without changing public IR or adding dependencies. Scalar
state retains null through loops, slot snapshots, nested calls and early returns.
`OpPtrEqual`/`OpPtrNotEqual` lower when an operand's entire finite provenance is
null; versions before1.4, wrong pointer types and missing capabilities retain
parse diagnostics. Non-null/non-null comparisons still report an address-equivalence
diagnostic; do not infer different buffers cannot alias. Selected memory lowering
omits undefined null dereferences, including atomic calls and projected array
reads, while keeping the selected non-null arm. Atomic root discovery ignores
the null alternative and upgrades only real memory locations. An unused null
constant does not force unrelated uncalled pointer parameters into provenance
specialization.

Normative basis: the [SPIR-V specification](https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html),
sections2.16.1 and2.18.3, allows null as an alternative with the limited storage
pointer capability, defines null as unequal to declared-object addresses, and
leaves executed null loads/stores undefined. Pointer comparison opcodes require
version1.4 and the appropriate storage/full-Workgroup capability. Null-based access
chains and general address comparisons remain explicitly unsupported.

Maintained suite: **1112 passed**, zero failures/skips, including24 new tests.
Command: `.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo`.
The temporary fixture initially included annotations in its result-definition map;
that fixture bug was corrected. Subsequent array-null projection and atomic-null
discovery failures drove the production fixes above. Harness Release build passes
with zero warnings/errors. Frozen final assembly SHA256:
`BD4D477D4AA7CED5378216EB583C63A4ED8920B5A1FF1BE5D99E232FBDC51EF7`.

The final fixture exporter produces80 cases across scalar/array/vector/atomic/
Workgroup memory, select/Phi/slot/loop/return/nested-call modes, all-null alternatives,
both comparison operators and Function/Private slots. Final commands reached
terminal state against the frozen assembly:

- `.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore --nologo -- nulls`
  exports80 original/intermediate pairs. All160 independent native validations pass.
- `python .work/naga-csharp/check-pointer-null.py`:6752/6864 checks pass, with112
  explicitly failed external rows:16 known reference Workgroup layout VUID10684
  failures and96 original-native Workgroup output differences. Of6144 GPU readbacks,
  **6048 pass** (2976 outputs and3072 complete input buffers). All normalized,
  managed-native and WGSL-to-managed comparisons pass; all64 valid reference-native
  paths pass. The16 invalid reference-native paths are not executed. The original
  Workgroup failures remain in `pointer-null/checks.json`; they are not certified
  by the runner's exit0, which permits only this separately classified output
  difference. Any normalized/managed output or full-input mismatch still fails.
- `.dotnet/dotnet.exe .work/naga-csharp/pointer-selection-fixtures/bin/Release/net10.0/pointer-selection-fixtures.dll cross-slot`
  exports the34 full-capability cross-buffer cases against the final pass.
  `python .work/naga-csharp/check-cross-buffer-noopt-control.py` passes **3026/3026**
  checks, including **2720 GPU readbacks** (1360 outputs and1360 complete inputs).
  Only original/normalized pipelines request disabled driver optimization; all
  three translated paths use default optimization. This separate report does
  not replace the original default-optimization failed gate.
- `python .work/naga-csharp/scan-corpus.py`:197 inputs, unchanged per-input step
  codes versus `full-corpus/pre-pointer-null-baseline.json`. Managed WGSL191 pass/
  six fail, managed native189 pass/eight fail, native validation184 pass/five fail,
  reference native import162 pass/27 fail, reverse WGSL185 pass/four fail. All191/
  185 WGSL results remain reference-accepted.
- `python .work/naga-csharp/summarize-pointer-null.py`:exit0; verifies counts,
  exact external failure classes, corpus equality, native validations, diagnostic
  pipeline flags, probe results and matching project/test/harness/exporter/runner
  assembly hashes. No result from a previous assembly is counted as a new pass.

Root/Graphics `git diff --check` pass, and the C#/project trailing-space scan finds
no matches. All verification handles are terminal. Not rerun against this assembly:
the prior20401-readback controls, historical matrix/member/address/query/read-only
GPU controls, other devices, browser/AOT, ray traversal or Rust consumer migration.
Finite null support does not complete aggregate slot addresses, null-based access
chains, general address comparison/difference, unknown parameter contexts or the
remaining corpus/extension gaps.

Separate native investigations preserve failures rather than changing the port's
expected behavior. `python .work/naga-csharp/investigate-cross-loop-alias.py`
compares original/normalized/managed/reference shaders, Aliased-root and volatile
controls, and a per-iteration instrumented shader. All seven variants validate.
Of28 output comparisons, five default-optimization comparisons fail; all14
comparisons requesting disabled driver optimization pass, and both managed and
reference variants pass at either setting. The instrumented default shader exposes
a stale selected-pointer read of output[2] even when its final output hides the
difference. The same shader reads the intervening919 write correctly when
optimization is disabled. This is evidence pointing to the local Intel UHD
Graphics optimization path, not proof about other devices or a general driver fix.
The [Vulkan pipeline flag documentation](https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineCreateFlagBits.html)
defines `VK_PIPELINE_CREATE_DISABLE_OPTIMIZATION_BIT` (1); it is used only in the
separate diagnostic, never in the managed library or ordinary failure gate.

The first null GPU gate retained an original-native Workgroup failure: seed0
reports non-null for a selected null pointer and executes its memory arm.
`python .work/naga-csharp/probe-null-workgroup.py` confirms that original output
fails with both optimization settings, while normalized, managed-native and
WGSL-to-managed paths pass both seeds/settings. Reference Workgroup native output
retains the known explicit-layout VUID10684 and is not executed. The probe and
first failure retain their assembly hashes. Final comprehensive checks retain96
original Workgroup output differences as explicitly failed external rows; all
normalized/managed output and every full-input comparison still fail the gate
on mismatch. Another device and the native pointer-comparison root cause remain
unverified. No claim of universal native equivalence is warranted.

All previous-stage GPU controls belong to their recorded older assembly. They
must not be represented as rerun against the new hash. No new services/worktrees,
subagents, branch/commit operations or dependencies were introduced. Sync/setup
were already successful earlier in this task and were not repeated. Active task
fixtures and oracle remain required for subsequent goal work; cleanup is due on
goal completion.

## Non-null address continuation (2026-10-08)

Previous goal turn: progress, with authoritative null-provenance code,24 maintained
regressions and independent verification. No prior verification handle remains live.
Current objective is non-null finite pointer equality/inequality and same-array
element differences, using captured root/path state through the existing pointer
graphs. Acceptance: maintained positive/negative cases, independent native original/
intermediate validation, reference WGSL acceptance, GPU output/full-input agreement
and the197-input corpus. Preserve potential cross-binding aliasing; different
StorageBuffer roots do not prove different addresses. Workgroup objects and paths
within one object have identities independent of bound-resource aliases. No public
IR variant or dependency was added.

`SpirvReader.PointerComparison.cs` now lowers finite same-root path equality and
same-array element difference. Selector pairs and captured dynamic indices retain
the address value through Phi/slot/return state. Pointer copies compare reflexively,
including cross-buffer choices. Potentially aliased distinct storage roots still
diagnose; separate Workgroup globals compare unequal. Storage differences require
the pointer's native ArrayStride decoration. Undefined differences outside one
array do not define a new address contract. The architecture account is updated.

Independent validation first caught an invalid unsigned width conversion with a
signed result in generated index handling; an unsigned intermediate and bitcast
correct it. Negative16-bit differences also exposed ordinary integer conversion
semantics: native UConvert/SConvert interpret source bits according to their
opcode, while numeric IR casts extend according to their source type. The reader
now expresses the native reinterpret/width/reinterpret sequence, and the writer
uses the source signedness for cross-width integer casts. Specialization conversion
behavior is unchanged. Twelve maintained conversion theories check scalar/vector
16/32/64-bit width pairs, signed and unsigned sources/results, native roundtrips,
WGSL roundtrips and high-bit inputs. Seventeen pointer comparison cases and the
24 previous null cases pass. The former non-null self-comparison rejection test
now asserts the newly supported reflexive behavior.

Current frozen assembly: `C909BE36DEA2850E3B7C805DD6EBAC63212664352B9F6BEA78B9D5B8435E5274`.
Checks completed so far:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo --logger "trx;LogFileName=pointer-comparison.trx" --results-directory .work/naga-csharp/test-results`:
  1141/1141 pass; focused comparison/null/conversion run53/53 pass.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore --nologo`:
  exit0, no warnings/errors. The fixture exporter was refreshed to the same hash.
- `python .work/naga-csharp/check-runtime-integer-conversion.py`:1044/1044 pass,
  including36 assembled original modules, native original/managed/reference
  validation and720 GPU output comparisons across five high-bit/boundary inputs
  and four translation paths. No reference rejection or GPU mismatch.
- `python .work/naga-csharp/scan-corpus.py`:197 inputs; all per-step exit codes
  unchanged from the retained pre-null baseline. Managed WGSL191/197, managed
  SPIR-V189/197; native184/189, reference SPIR-V162/189, reverse WGSL185/189.
  All191 emitted forward WGSL and185 reverse WGSL outputs accepted by reference.
-87 comparison originals and87 normalized modules pass independent Vulkan1.2
  validation. The valid original first-failure reproduction rejects on the prior
  assembly with its non-null address-equivalence diagnostic.

The default comparison verification stops at the cross-buffer loop; its first79
cases completed. Its6609 recorded rows have6525 passes,56 failed original Workgroup
outputs,27 failed reference Workgroup native validations and the final original
cross-buffer loop output mismatch. All completed normalized/managed outputs and
all input-memory readbacks pass. A separate tail control covers the final8 cases:
`python .work/naga-csharp/check-pointer-comparison-tail-control.py`,712/712 pass,
including640 GPU output/input-memory comparisons. Only the original/normalized
cross-buffer loop disables optimization; every translated path and every other
tail case keeps default flags. Negative16/32/64-bit differences and mixed index/
result widths pass all five paths, including reference WGSL generation. The
default failure report is preserved and remains failed; tail control does not
certify default native equivalence. Refreshed null controls completed:
`python .work/naga-csharp/check-pointer-null.py`,6752/6864 pass, with6144 GPU
readbacks. The same96 original Workgroup output mismatches and16 reference
Workgroup native-layout failures remain failed external rows. All normalized,
managed and valid-reference outputs and all3072 input-memory comparisons pass.
The first
Workgroup original output difference is retained in `pointer-comparison/first-gpu-failure.json`:
seed1 returns element difference8 instead of1. The32-output optimization probe
has three mismatches in each of the two original optimization modes; all16
normalized outputs pass. Adding pointer ArrayStride1/4/8 keeps native validation
passing and changes the original difference to32/8/4; this suggests physical
Workgroup layout participates in native pointer subtraction, but does not establish
the root cause. Original failures remain failed external rows; normalized and
managed output/full-input mismatches still stop the verification runner. Another
device is unverified. Native reproduction and probe artifacts are retained.

`python .work/naga-csharp/probe-comparison-cross-loop.py`:80 GPU outputs,
eight failed default-optimization original/normalized outputs (four each), all16
original/normalized no-optimization outputs and all48 managed/reference/back
outputs in both optimization modes pass. This reproduces the earlier native
cross-buffer loop issue with pointer self-comparison; equality itself remains
correct and the loaded value becomes stale across aliasing writes.

`python .work/naga-csharp/summarize-pointer-comparison.py` exits0 and checks the
1141-test TRX, four matching assembly copies, unchanged197-input corpus codes,
all report counts, exact allowed external failure classes, selective optimization
flags, and both native difference probes. `git diff --check` at workspace and
Graphics roots exits0; Git reports existing CRLF normalization warnings in root
docs. No verification sessions remain live. No service/worktree or new dependency
was introduced; active fixtures/oracles/reports remain required by this unfinished
goal, so final cleanup is not due yet. Full Naga parity remains unachieved.

Next audit comparison operand type identity/decorations against native validation,
then extend finite pointer/descriptor-array provenance and remaining unsupported
core corpus cases. Retain the raw native failure gates and controls; do not turn
the optimization control into a universal default-equivalence claim.

## Descriptor buffer identity continuation (2026-10-08)

Previous goal turn is progress: non-null comparison/conversion implementation,
29 new maintained tests and independent GPU/native evidence. Verification handles
are terminal. Sync and setup were successful earlier in this task and are not
repeated. Working trees retain unrelated changes; no nested instruction files
were found. Objective remains full managed SPIR-V/WGSL Naga alignment.

Current scope: enforce comparison operand type identity, preserve descriptor
buffer aliasing in address operations, and admit proven single-descriptor storage
structures under the limited pointer capability. Acceptance: maintained positive/
negative regressions, independently validated original/normalized/emitted SPIR-V,
reference WGSL compilation, GPU output and both input-buffer readbacks with
separate/aliased descriptors, full maintained tests and unchanged corpus gates.
No public IR or dependency change is needed.

`audit-pointer-comparison-types.py` saved six native-vs-managed discrepancies
against the prior C909 assembly: duplicate pointer declarations, with stride4/8,
reject in native validation but incorrectly translate in C#. Pointer comparisons
now require the same native pointer type ID. Six maintained tests cover all three
comparison/difference opcodes and both stride variants.

Descriptor regressions initially exposed five failures: direct limited-capability
selection/comparison within descriptor0 falsely rejected, and comparison across
potentially aliased descriptor elements silently accepted in select/loop/nested
return modes. `PointerSelectionLowering` now identifies a storage structure by
its global plus proven constant descriptor index. Unknown descriptor identities
still diagnose under the limited capability. Private native comparison lowering
checks descriptor identity before comparing element paths; differing potentially
aliased storage roots also diagnose for differences. Copies of one pointer remain
reflexive. Twenty-four descriptor tests now pass, including ordinary/atomic
selection, Phi, slots, loops and returned pointers. Full-capability memory choices
between descriptor elements already used the existing projection; new independent
tests exercise their actual execution and aliases.

Independent verification completed on40 fixed-descriptor fixtures (24 memory,16
comparisons) and16 dynamic-descriptor memory fixtures, eight seeds and distinct/
aliased descriptor buffer bindings. Dynamic ordinary/atomic cases overwrite the
descriptor index after address creation and exchange captured pointers in loops.
Nine additional maintained theories exercise those cases. A separate difference
test checks the diagnostic for unknown cross-binding address equivalence, bringing
this continuation to40 new maintained cases. The task-only Vulkan adapter reuses
the existing resource owner and cleanup and changes only descriptor counts/buffer
bindings; it performs no translation or expected-value calculation. Dynamic cases
query/enable the supported descriptor-indexing features using the local Vulkan
header ABI. Production code adds no dependency or public IR metadata.

Frozen assembly: `197D5F521328BECCACB2E838377234C8654E914FEF7CE286AC26FE412276935F`.
Executed checks:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo --logger "trx;LogFileName=pointer-descriptor-array.trx" --results-directory .work/naga-csharp/test-results`:
  1181/1181 pass, zero skipped. Earlier intermediate1171/1180 runs are superseded
  by this final TRX, after the cross-binding difference test was added.
- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore --nologo`:
  exit0, no warnings/errors. Fixture exporter refreshes completed in both modes.
- `python .work/naga-csharp/check-pointer-descriptor-array.py`:9960/9960 pass,
  including40 original/normalized/back/managed/reference native validation sets,
  reference WGSL compilation and9600 GPU readbacks across3200 dispatches.
- `python .work/naga-csharp/check-dynamic-pointer-descriptor-array.py`:3984/3984
  pass, including16 analogous validation sets and3840 GPU readbacks across1280
  dispatches. The initial invocation preceded exporter completion and reported a
  missing manifest; the same live exporter was waited to exit0, then verification
  completed. No exporter was restarted on an observation timeout.
- `python .work/naga-csharp/verify-descriptor-diagnostics.py`:14/14 expected
  results, including six corrected native type-identity rejections and four
  independently valid address cases with explicit managed alias diagnostics.
- `python .work/naga-csharp/scan-corpus.py`:197 inputs, exact per-step exit codes
  unchanged from `full-corpus/pre-pointer-descriptor-baseline.json`; WGSL191/197,
  SPIR-V189/197, native184/189, reference SPIR-V162/189 and reverse WGSL185/189.
  All191 forward and185 reverse emitted WGSL outputs accepted by reference.
- `python .work/naga-csharp/summarize-pointer-descriptors.py`:exit0; validates the
  final TRX, four assembly copies, every expected GPU path/alias/seed/readback key,
  all native stages, diagnostics, feature availability and corpus exit codes.
- Workspace and Graphics `git diff --check`:exit0, existing root-doc CRLF warnings.

All new GPU/native/reference checks pass on Intel UHD Graphics, using default
pipeline optimization. Other devices and browser WebGPU are not verified. Prior
null/comparison/native-difference reports belong to C909 and were not rerun against
this hash; their unresolved external failures remain historical evidence. The
current corpus failures remain failed, so full Naga parity is still unachieved.
No verification sessions, task services or worktrees remain live. Active fixtures,
oracles and reports stay in `.work/naga-csharp` because the goal is unfinished;
completion cleanup is not due yet. No branch/commit/PR or subagent operation.

The final corpus triage reconfirmed that `operators.wgsl` fails at1620:2 on its
explicitly invalid mixed concrete short-circuit operand (reference issue8440),
already documented in the earlier workgroup/matrix continuation. Its strict
diagnostic is correct; no type rule was relaxed. Simple-model SubgroupMemory
barriers also retain their existing diagnostic pending model/equivalence work.
The following continuation adds finite pointer arithmetic (`OpPtrAccessChain`); next broaden descriptor identity proofs for
dynamic/specialization indices, slot aggregates and unknown parameter roots.
Preserve the native difference controls and alias diagnostics while extending
the equivalent WGSL lowering.

## Finite pointer arithmetic continuation — 2026-10-08

Objective: translate finite logical `OpPtrAccessChain` addresses to equivalent
WGSL memory operations, including captured signed offsets, aggregate trailing
indices and one-past comparisons. Scope remains SPIR-V/WGSL only. Acceptance for
this continuation is native validation of original/normalized/generated modules,
independent GPU output and complete input-buffer comparisons, maintained
regressions, and unchanged existing corpus step results. Full Naga parity remains
unfinished.

The private scalar provenance pass now recognizes pointer arithmetic without
requiring a separate comparison/Phi trigger. Arithmetic steps replace the captured
innermost-array index, then append trailing access indices. Offsets sign-extend
before addition even when their declared integer type is unsigned. Multiple
constant source paths can collapse to one dynamic path without duplicate SSA
definitions. The same state crosses pointer Phi, Function/Private slots and
specialized helper returns; arithmetic within a loop can update a stored address
on each iteration.

Native materialization anchors a nonzero final index at its preceding valid
element and advances by one with `OpPtrAccessChain`; index zero uses a zero step.
This preserves legal one-past addresses without creating an out-of-bounds
`OpAccessChain`. Unselected final indices use zero. Storage pointer strides are
checked against their arrays, with required generated result-pointer decorations
placed in the annotation section. Reader lowering consumes the reconstructed
array index; atomic root discovery follows the pointer step without adding an
extra aggregate path component. No public IR/API or dependencies were added.

Maintained `PointerArithmeticTests` adds21 cases: 16/32/64-bit positive and
negative offsets, unsigned negative encodings, chained steps, selected/merged
addresses, private pointer slots, loop snapshots, nested returns, atomics,
Workgroup, one-past comparisons, array-of-array trailing indices, operation without
comparisons, collapsed constant sources, missing strides and missing capability.
`docs/architecture.md` records the lowering and its limits. Non-array nonzero
steps, null arithmetic bases, physical addresses and broader pointer provenance
remain unresolved.

Frozen production assembly SHA-256:
`0B3ED0CAF77777B87B80CC424C5A676B0631031F3B03377CF9BB8903B593CC4D`.
All final reports below refer to this hash; the baseline harness retains197D.

Actual final checks, from the workspace root:

- `./.dotnet/dotnet.exe build .work/naga-csharp/harness/harness.csproj -c Release --no-restore --nologo`:
  exit0, zero warnings/errors. The exporter subsequently rebuilt only changed
  maintained fixture code; four production DLL copies retain the frozen hash.
- `./.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore -- arithmetic`:
  exit0, exports91 original/normalized pairs:85 scalar/atomic/width combinations,
  four aggregate combinations, one direct operation and one collapsed-constant
  source case. Arithmetic slots/loops use Private slots; other cases cover
  selections, Phi and early/nested returns.
- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-build --no-restore --nologo --logger "trx;LogFileName=pointer-arithmetic-full.trx" --results-directory .work/naga-csharp/test-results`:
  exit0, **1202/1202 passed**, zero skipped.
- `python .work/naga-csharp/check-pointer-arithmetic-native.py`: exit0,
  **273/273 expected checks**:91 originals and91 normalized binaries pass native
  Vulkan1.2 validation, and all91 originals are rejected by the197D managed baseline.
- `python .work/naga-csharp/check-pointer-arithmetic.py --resume`: exit0,
  **8147/8235 checks pass;88 external original-native readback failures remain**.
  The runner records failures; exit0 does not mean an all-green result. It tests
  original, normalized, managed SPIR-V roundtrip, reference WGSL-to-SPIR-V and
  managed WGSL-to-SPIR-V paths, eight input seeds each. GPU output and complete
  68-byte input data are read back in separate fresh dispatches: **7408 readbacks /
  7408 dispatches**, including128 widened-element control readbacks. All463 native
  validations and all reference WGSL compilations pass. All normalized/translated
  GPU output and input bytes pass with default pipeline optimization.
- `python .work/naga-csharp/scan-corpus.py`: exit0,197 inputs. Forward managed
  WGSL191pass/6fail; managed SPIR-V189pass/8fail; native184pass/5fail;
  reference SPIR-V162pass/27fail; reverse managed WGSL185pass/4fail. All191 forward
  and185 reverse WGSL outputs remain reference-accepted. Every per-input step
  exit code matches `full-corpus/pre-pointer-arithmetic-baseline.json`.
- `python .work/naga-csharp/summarize-pointer-arithmetic.py`: exit0, validates the
  final TRX, four DLL hashes, all91 fixture/path/seed/readback identities,463 native
  checks, all273 baseline checks, all128 widened control readbacks, every external
  failure classification and the complete corpus step-code comparison. Summary:
  `.work/naga-csharp/pointer-arithmetic-summary.json`.
- Workspace and Graphics `git diff --check`, plus workspace staged diff check:
  exit0; existing root-document CRLF warnings only. No maintained files or checks
  were removed/disabled to make results green.

External execution difference: eight16-bit negative-element fixtures retain
**44 original-native output failures and44 input-byte failures** on Intel UHD
Graphics. Both signed and unsigned16-bit `-1` encodings behave like65535 in the
original GPU path. Failed address-difference results differ from expected by65536
elements, or131072 scalar elements when the pointer steps over two-scalar arrays.
For each affected fixture, an independently generated control inserts only
`OpSConvert` to signed32 before `OpPtrAccessChain`; its original pointer/slot/call
graph is otherwise preserved. All128 control readbacks pass, as do every
normalized and translated readback. This supports a native narrow-element
execution issue; other drivers and optimization modes were not tested, so its
underlying driver/compiler cause remains unconfirmed. The
[SPIR-V specification](https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html)
defines signed Element interpretation independently of its declared signedness.

Fixture/runner repairs are explicit and their initial reports remain in the task
area: sign-extend signed16 negative literal words; place aggregate decorations
before every type declaration; reuse an existing signed32 type; define inner-array
length constants before their types. The first selected-loop runner incorrectly
assumed addresses merely swapped; the actual loop stores its decremented pointer
back, so inputs and expectations now account for cumulative decrement, including
the final loop-header evaluation. Aggregate `PtrDiff` comparisons were corrected
to stay within one innermost array instead of assuming a defined difference
between distinct inner arrays. These were test-input/expectation defects; the
production hash stayed frozen during final validation. On resuming after fixture
repairs, all174 non-aggregate original/normalized binaries were hash-checked as
unchanged; completed case evidence was reused only for those unchanged binaries.

Workgroup arithmetic has maintained checks but no new GPU coverage in this
continuation. Browser WebGPU, other GPUs, consumer migration and full extension
parity remain unverified. Earlier descriptor/null/comparison reports use older
hashes and are historical, including their unresolved native differences. No
verification sessions, services or task worktrees remain live. Active oracle,
fixture and report data stays in `.work/naga-csharp` for the unfinished goal;
completion cleanup is not due yet. No branch, commit, PR or subagent operation.
Next broaden dynamic/specialization descriptor identity proofs, pointer-slot
aggregates and unknown parameter roots, then address the remaining core/extension
corpus gaps without weakening valid diagnostics.

## 2026-10-08 continuation: descriptor snapshot identity (verification interrupted)

Objective: admit provably identical dynamic/specialized descriptor captures under
the limited storage-pointer capability and in address comparisons, retaining
native capture time. Scope: private SPIR-V pointer normalization/selection and
maintained regressions; no public API, dependency or consumer migration changes.
Acceptance: shared captures work across finite Select/Phi/slot/helper graphs;
independent reads and repeated loop/helper activations cannot establish identity;
native validation and GPU output plus both complete input buffers agree, including
aliased descriptor bindings and pipeline overrides.

Implemented a finite descriptor identity domain before native reconstruction.
Scalar copies and specialization addition by zero retain identity. Phi/parameter/
return/slot boundaries retain only proven invariant captures in their owning
activation scope. Private slot loads also require a dominating overwrite in that
activation. Loop execution and escaped callee-local captures acquire distinct
identities. Direct finite Select graphs now enter normalization even without a
comparison. Keep the direct-root guard: normalizing every Select broke the
existing unused-null/uncalled-parameter regression; the guard restored it. Reader
capture names carry this private proof into limited-capability selection checks.
Unproven descriptor aliasing still diagnoses. No new public IR metadata.

Current assembly SHA-256:
`C22EF62A55CBC86718CA8FB3114A1C33EECF871801DEFE3F4C5A46BF6137BA4A`.
Preserve the frozen assembly during external checks. Passed:

- `./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Naga.Tests/Sia.Spirv.Naga.Tests.csproj -c Release --no-restore --nologo --logger "trx;LogFileName=descriptor-identity-full.trx" --results-directory .work/naga-csharp/test-results`:
  exit0, **1240/1240**, zero skipped, 38 additional maintained cases since1202.
  Includes independent-read, repeated-loop and repeated-callee rejection and eight
  WGSL-roundtrip pipeline-resolution cases with default/overridden descriptor0/1.
- `./.dotnet/dotnet.exe run --project .work/naga-csharp/pointer-selection-fixtures -c Release --no-restore -- identities`:
  exit0,108 positive original/normalized pairs and seven negative native inputs.
- `python .work/naga-csharp/check-descriptor-identity-native.py`: exit0,
  **223/223** native validations. The frozen0B3 managed baseline accepts42/108
  positive originals and rejects66; do not describe all108 as newly accepted.
- `python .work/naga-csharp/scan-corpus.py`: exit0,197 inputs, every step exit
  code unchanged from `full-corpus/pre-descriptor-identity-baseline.json`.
  Forward WGSL191pass/6fail; SPIR-V189pass/8fail; native184pass/5fail;
  reference SPIR-V162pass/27fail; reverse WGSL185pass/4fail. All191 forward
  and185 reverse WGSL outputs are reference-accepted. Existing failures remain.

Failed initial external attempt: `python .work/naga-csharp/check-descriptor-identity.py`
stopped after six cases at managed WGSL-to-SPIR-V for a derived specialization
default. Preserved in
`pointer-descriptor-identity/checks-unresolved-derived-before.json`. Native
SPIR-V-to-SPIR-V retains the original SpecId191 and derived operations. WGSL
emission represents derived dependencies as dependent overrides; writing those
back requires `PipelineConstants` resolution. The reference oracle also calls
`process_overrides` before SPIR-V writing. The runner now resolves both WGSL paths
with the same191=0/1 values; original, normalized and direct managed native paths
retain SpecId191 and receive Vulkan pipeline specialization. General unresolvable
dependent-override emission remains a documented limitation; no diagnostic was
disabled and production code was not changed to fold away specialization.

Interrupted check: the corrected GPU runner was explicitly stopped after the
goal state returned **paused**. Its durable checkpoint records **13530/13530**
successful checks across54/108 complete positive cases, **4320 dispatches /
12960 readbacks**,297 native validations including the seven negative inputs,
and all seven expected managed alias diagnostics. The completed cases are the
ordinary descriptor cases; atomic descriptor cases remain unrun in this runner.
Each dispatch reads output and both68-byte input buffers, tests aliased and
distinct bindings, and changes the source descriptor index after address capture.
Device: Intel UHD Graphics. This partial report is not full GPU acceptance.
The process exit1 reflects intentional termination, not a completed failing suite.
Process4240 and tool session3792 were confirmed terminal; no services/worktrees
or subagents were started. No new branch/commit/PR operations.

Resume only when the user resumes the goal. Reinspect source, assembly hashes and
goal state, then rerun the corrected108-case GPU runner to completion and add a
strict summary verifier. Keep the partial/initial failure evidence separate.
Broader full Naga parity, other GPUs/browser, consumer migration and earlier
external native differences remain unfinished. Active temporary inputs/oracles/
reports stay in `.work/naga-csharp` for this paused, unfinished goal; task-completion
cleanup is not due. Do not rerun the historical pointer-arithmetic summary against
this newer assembly.

## Recovery and handoff

Resume by inspecting Graphics status and this plan, then the managed project and
tests. Storage member requirements and Vulkan coherent buffers are now retained.
Direct Uniform per-access layout lowering now passes independent checks.
Initialization-aware Workgroup member lowering and selected TaskPayload volatility
have maintained regressions. Native explicit matrix layouts now lower in the
reader without public metadata and its final independent checks have passed.
Projected helper pointers, finite module initializer reconstruction and independent
member layouts of a shared matrix value type now have maintained and GPU checks.
Next cover unresolved whole-array copies, specialized constant initialization,
variable pointer provenance operations, helper diagnostic/continuing scopes and
the raw native device/reference Workgroup differences recorded above.
WGSL vector-component addressability now has maintained/reference/GPU checks.
Mixed read-only/atomic candidate memory now has maintained/native/GPU coverage.
Finite pointer-Phi branch/loop provenance now has maintained/native/GPU coverage.
Direct Function/Private pointer-slot loads now have maintained/native/GPU coverage,
including mixed load/Phi graphs and1.4+ Private entry interfaces. Finite slot copies,
helper arguments and aliased helper writes now also have those checks. The claimed
limited-capability cross-buffer gap was disproved by normative rules; keep its
correct rejection. Covered finite pointer returns now pass maintained/native/GPU
checks. The full-capability cross-buffer difference now has an optimization-control
reproduction. Finite null provenance and guards have maintained/native/GPU
verification; finite non-null address equality and same-array differences also
have maintained/native/GPU coverage with the documented raw native differences.
Exact comparison operand type identity now has native/maintained rejection tests.
Finite innermost-array pointer arithmetic, including one-past addresses and
aggregate trailing indices, now has maintained/native/GPU checks; keep the
original-native16-bit negative-element execution differences and passing widened
controls separate from translated behavior.
Fixed and dynamic descriptor address snapshots have native/reference/GPU coverage,
including aliased bindings; limited capability admits proven constant single-
descriptor structures. Investigate original Workgroup null/comparison execution,
broaden cross-root address equivalence and dynamic descriptor identity proofs,
then broaden slot
aggregates/unknown parameter roots, and investigate
the reference private-pointer emission panic without weakening input validation.
Resolve remaining TaskPayload coherence equivalence. Then inspect
the full-corpus manifest and repair common core failures before
extension coverage. Prioritize provably equivalent ray-query committed lowering,
helper diagnostic scope/continuing loop lowering, cooperative per-access memory
and native operation lowering, broader specialization builtins/default emission,
unresolved WGSL array snapshot writing, remaining diagnostic
reporting and uniformity analysis, member/per-access memory attributes, remaining scalar/IO features,
descriptor layout gaps, the mesh/task native lowering limits above and broader
GPU equivalence. Preserve/promote the independent
GPU fixtures into maintained regressions before final cleanup. Temporary work
belongs in `.work/naga-csharp`; references remain read-only.
No task-owned services or worktrees. Keep active task inputs and oracle artifacts
until final acceptance; perform documented completion cleanup once finished.
