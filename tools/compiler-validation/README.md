# Integrated compiler checks

See the [current pipeline/target map](../../docs/compiler-architecture.md) and
[proposed improvement plan](../../docs/compiler-roadmap.md). `evidence.json`
records the original PR baseline, not verification of later source changes;
`renewal-evidence.json` records the cleanup follow-up.

Latest synchronization migration (7BCB90E0…) records twelve contracts, maintenance
2023/2023, independent format 217/217, frozen native input/output format 82/82,
160 reverse PASS and 657 deterministic files. All 218 prior SPIR-V artifacts and
41 replay outputs match. Targeted GPU results are 24 PASS/3 ERROR; the three
Vulkan memory-model imports have no execution/readback. Evidence is separately
recorded under canonical-synchronization-* in the workspace task area. The first
full run's two compatibility-body assertions and their correction to prepared
target graphs are retained. Full architecture and research acceptance remain open.

Previous target memory qualification (F7382786…) consumes SSA address provenance
and keeps qualified access/atomic operands in target graphs. Graph call closure
inherits those memory effects; structured qualification handles explicit deferrals
only. Six new contracts, related 143/143 checks and maintenance 2011/2011 pass;
format checks are 217/217 and frozen native input/output checks 82/82. Reverse
routes pass 160/160, 657 repeated files agree, all 218 prior SPIR-V artifacts and
41 frozen replay outputs match. Current-source GPU and full consumers are not run.
Synchronization expansion, layout/pointers, frontend/LLVM and full research gates
remain open. Evidence is separately recorded under canonical-memory-* in the
workspace task area; original baseline and historical evidence remain unchanged.

Previous canonical-module validation (73488227…) uses owned graph instructions
and terminators for stage/call/payload closure while retaining declaration,
signature, entry and explicit deferred-body gates. Mixed effect summaries also
resolve owned graphs. The native frontend no longer reconstructs function bodies
before shared passes; its public Module remains an explicit output adapter.
Seven new contracts and related 345/345 checks pass; maintenance is 2005/2005,
format 217/217, reverse 160 PASS, determinism 657 files, and all 218 prior
SPIR-V artifacts match. Frozen native replay is recorded separately. Current-source
GPU and full consumers have not been rerun. Full direct frontend/target/LLVM
convergence and research acceptance remain unfinished.

Previous R3 (2FC5BD44…) handles native conditional exits to a known enclosing
selection merge in the structured adapter, keeping phi edge copies and ordered
effects. The exact optimized failing input also fails in frozen compiler 4347B236…;
the R2 failure attribution to llc was incorrect and its freeze produced no snapshot.
Related checks pass 134/134, complete maintenance 1998/1998, captured/reduced
input/output validation 6/6 and maintained format validation 217/217. GPU and
full consumer checks have not been rerun for this source. Direct graph consumers,
LLVM convergence and full research acceptance remain unfinished.

Previous canonical module evidence (F9289B01…) records shared owned graphs,
borrowed-native-graph isolation, predecessor/dominance preservation, canonical
call-effect closure and explicit target/legacy adapters. Focused checks are
371/371 including twelve new contracts; maintenance 1990/1992 (two missing-selection-merge diagnostics; the earlier llc attribution was incorrect),
independent validation 217/217, descriptor regression 4/4, original input/output
replay 82/82, reverse routes 160 PASS, determinism 657 files and all 218 prior
SPIR-V artifacts unchanged. Final-source GPU checks are 110 PASS/9 ERROR in 119 isolated attempts.
The three VMM, three loop-return WGSL and three specialization/LocalSizeId
imports remain unresolved without execution/readback; the latter match frozen
old-compiler bytes. Initial build/trace
assertion failures remain separate; exact pass-count assertions were updated
for the added call-effect phase while semantic assertions were retained.
Target layout/pointer and public frontend adapters, further shared passes,
LLVM and the full research/consumer matrix remain unfinished.


R2 evidence correction: freeze stopped before creating the snapshot/evidence files.
The exact failing optimized input also fails in frozen compiler 4347B236…
(input SHA-256 3DB8FEB375D9909C46B511CF93DC396A360FFDDE80D4BE7C0A68C478DA481FA4).
This is a newly exposed existing enclosing-selection-exit defect, not proof
of a regression introduced by canonical module preparation. Reproduction and
baseline records are in `.work/compiler-architecture-first/canonical-module-offline-*`.

Previous entry metadata evidence (DF169F36…) records target-owned modes,
workgroup specialization, interface requirements, override defaults/IDs and a
typed specialization DAG. Internal emission takes prepared data only. The raw
ordered fixture retains its explicit SPIR-V 1.5 target in preparation; 214 prior
SPIR-V artifacts remain byte-identical. Focused checks pass 13/13, maintenance
checks are 1978/1980 (two llc failures), format checks 217/217, descriptor
regressions 4/4, frozen native input/output replay 82/82, reverse routes 160 PASS
and determinism 657 files. Final-source GPU is 110 PASS/9 ERROR in 119
isolated attempts. Nine new WGSL entry routes pass; their three SPIR-V imports
fail on SpecConstantOp/ExecutionModeId with bytes identical to the frozen old
compiler, alongside the six prior VMM/loop-return imports. Errors have no
execution/readback. Source/DLL/options/tool
identities and the frozen-compiler comparison are kept in task evidence; those
bounded checks do not close the full architecture or consumer matrix.

Previous entry-ABI evidence (C626BFA0…) records canonical wrapper graphs with
explicit interface effects and prepared initializer/conversion/publication calls.
Checks are 1966/1968 maintenance tests (two llc failures), 213/213 independent
validation, descriptor regression input/output 4/4, frozen original native
input/output replay 82/82, 156 reverse PASS and 640 deterministic files. Final
source GPU checks are 101 PASS/6 ERROR of 107, with all 27 new entry/raster cases
passing. Three VMM and three loop-return WGSL import errors remain without
execution/readback; previous-source results remain separate. A native
variable numbered 12 independently reproduces the descriptor identity regression;
the fixed producer table excludes type declarations. Writer execution-mode and
capability policy, frontend/research and consumer gates remain open.

Previous target-CFG evidence (E9696BE8…) records target-owned block order,
merge/continue and structural backedges, direct canonical function serialization,
1956/1958 tests (two llc failures), 210/210 independent SPIR-V checks, 153 reverse
routes and 628 deterministic files. The frozen original 41 native inputs replay
separately, with 82/82 input/output validation. GPU attempts are 74 PASS/6 ERROR
of 80: three VMM import errors and three loop-return WGSL import errors, including
authored source; the corresponding SPIR-V route passes. New fixtures keep fixed
expected words and untouched-buffer sentinels. Record error reports and actual
readbacks; successful parsing does not imply a GPU consumer accepts the shader.
Entry-wrapper/ray-query/native-pointer control lowering, frontend convergence
and the original full research/consumer matrix remain unfinished.

Historical collective-read recovery proves isolated native default workgroup
barrier/single constructible read/barrier regions on verified CFG. Actual helper
requirements retain candidate SSA origins; failed pointers/control and dependent
candidates fall back. Source WGSL rules, native qualifications and target gates
remain strict. CPU/export source B14FA918… and GPU source E950ABB0… differ only
in two GPU registration/suffix files; compiler/test source and DLL hashes match.
Focused 421/421; maintenance 1944/1946 (two existing llc crashes); independent
SPIR-V 202/202; 595 deterministic files. All 145 reverse routes now pass, resolving
two previous rejections for identical SPIR-V. Prior 159 binaries/144 direct WGSL
remain identical; 140/141 prior reverse outputs match. The changed aggregate
reverse is GPU checked. Thirty-six native attempts: 33 PASS, three existing VMM
import ERRORs without execution. Four-invocation uniform and divergent fixtures
pass all four formats; ordered reverse output and marker=31456 also pass.
Collective-recovery-final preserves partial evidence and original failures. Other
memory/atomic regions, target control flow, frontend convergence and the complete
research/descriptor/corpus/consumer scope still require completion.

Historical synchronization legalization materializes builtin barrier/default atomic
operands and ordered collective-load captures/reads before serialization. It keeps
native memory operands, resolved user-function identity, scoped continuing aliases
and effect order. Source 94C7B3C8…: focused 447/447; maintenance 1929/1931 (two
existing llc crashes); independent SPIR-V 200/200; 586 deterministic files, including
explicit failures. Prior 156 SPIR-V, 141 WGSL and 140 reverse WGSL remain identical.
Twenty-five GPU attempts yielded 22 PASS and three existing VMM import ERRORs.
All seven new cases verify output/marker ordering or [9,9,14] atomic results.
Ordered and RawOrdered reverse WGSL are rejected by the uniformity gate; both
reproduce with the immutable same-input 1DEA compiler. The exporter preserves
diagnostics and still exits 1. No unvalidated inspection output was executed.
Collective read uniform-result preservation, target control flow, frontend
convergence and the original full research/descriptor/corpus/consumer scope remain
open. Synchronization-final preserves partial evidence, not full acceptance.

Historical memory-access legalization captures coherent/volatile and inherited address
requirements in ordered access/atomic metadata before serialization. The writer
no longer derives pointer memory qualifications or captures alias requirements.
Function snapshots retain native operands without inheriting logical member flags;
task-payload atomics retain Workgroup scope. Source 1DEA0CF5…: focused 323/323,
maintenance 1916/1918 (two existing llc crashes), independent 197/197, and 572
deterministic files. Existing 155 SPIR-V/140 WGSL outputs remain identical; the
new qualified alias matches the immutable prior compiler on identical input and
target flags. Its WGSL rejection remains explicit; its sidecar is an unqualified
control, with only SPIR-V GPU execution selected. Eighteen GPU attempts yielded
15 PASS and three VMM shader-import ERRORs, with no execution/readback for errors.
They have identical prior/baseline shader hashes; external validity does not prove
GPU execution. Memory-legalization-final and BEA7 history preserve evidence.
Builtin synchronization/default memory semantics, frontend convergence, complete
research/descriptor validation and consumer gates remain open under original scope.

Historical workgroup access prepares physical globals/addresses and explicit
ordered conversion calls before serialization; writer WorkgroupLayout is removed.
Initializer mappings, legacy implicit swizzles and function/builtin identity retain
their contracts. Source 3ED2AE79…: focused 276/276, maintenance 1907/1909 (two
existing llc crashes), independent 196/196, 568 deterministic files and 15 native
GPU cases PASS. Existing 152 SPIR-V/137 WGSL outputs remain identical; new alias
and aggregate uniform-load readbacks retain fields, ABI and effectful counter=1.
VMM GPU, mesh execution, remaining GPU/direct/target, full research/descriptor and
consumer gates remain open. See workgroup-access-final and the architecture plan.
Remaining memory/builtin/control-flow and frontend convergence retain full scope.

Historical mesh-publication evidence uses source E143B33A…: focused 267/267,
maintenance 1899/1901 (two existing llc version-query failures), independent
193/193 (152 outputs and 41 raw inputs), and 555 identical files across independent
exports. Five existing mesh/task SPIR-V outputs change; all 131 existing WGSL and
six same-input frozen compiler WGSL outputs remain identical. Six existing compute
GPU controls PASS; these do not execute mesh/task shaders. vulkaninfo identifies
Intel HD Graphics 620 with no EXT/NV mesh extension, so native mesh/task execution
is blocked. The shared uniformity gate's four negative cases failed before the
fix and pass afterwards. mesh-publication-final-2 preserves source/options/commands,
tool and consumer hashes, independent validation, readbacks, baseline and failures.
Remaining memory/control-flow/frontend convergence, complete research/descriptor
corpus and browser/SDK/Linux/AOT gates are open.

Historical uniform-legalization evidence uses source 8D734301…: focused 154/154,
maintenance 1879/1881 (two llc version-query failures), independent 187/187 (146
outputs and 41 raw inputs), 530 identical files across independent exports. Of 25
selected native GPU attempts, 21 PASS, two original continuing WGSL variants ERROR
on native Naga redefinition and two original shared WGSL controls panic fatally.
Generated continuing WGSL/SPIR-V readbacks pass, as do nested uniform ABI/calls=1
and explicit SPIR-V zero fallback. Original failed inputs and immutable previous
compiler rejection are retained. uniform-legalization-final preserves source,
consumer hashes, options, tools, eight commands, readbacks and failures. Remaining
historical GPU/direct/target, mesh, full research/descriptor and browser/SDK/Linux/
AOT checks are not run for this identity. This is not full architecture acceptance.


Historical workgroup-conversion evidence uses source BB7698C5…: focused 181/181,
maintenance 1871/1873 (two llc version-query failures), independent 185/185 (144
outputs plus 41 raw inputs), two exporter processes agree on 522 files. Ten of
twelve selected native GPU checks PASS; the two original Shared WGSL inputs still
trigger Naga no entry found for key fatal panics with no final capture. New nested
array/structure checks read ten defined ABI fields and counter=1 in all three
formats. Full raw buffers retain padding without requiring defined padding values.
Frozen previous compiler WGSL matches all three probes; the changed Shared and
nested SPIR-V outputs are checked by independent validation and execution.
workgroup-conversion-final preserves exact source/consumer/options, complete
command arguments, baseline, actual readbacks and failures. Remaining historical
GPU, direct/target exports, mesh execution, full research/descriptor corpus and
browser/SDK/Linux/AOT are not run for this identity. Uniform/alias and mesh control
extraction remain pending; this is not full architecture or parity acceptance.

Previous physical-layout preparation evidence uses source B2ECEC53…: focused 243/243,
maintenance 1867/1869 (two llc version-query crashes), independent 184/184 (143
outputs plus 41 raw native inputs). Seven of nine selected native GPU checks PASS:
all six alias variants and shared uniform/workgroup/storage SPIR-V. Shared source
and generated WGSL both cause Naga 29.0.3 no entry found for key fatal panics,
exit -1073740791 with no final capture. ABI field words are compared; padding is
not a defined output value. Full raw buffers and field-index expectations are
retained. The previous 141 binaries are identical, and the immutable prior compiler
generates identical SPIR-V/WGSL for both new inputs. physical-layout-final evidence/
snapshot preserves source/assembly/options/assets, baseline and failure logs.
Other historical GPU, direct/target exports, mesh GPU, full research/descriptor
corpus and browser/SDK/Linux/AOT were not run for this source. This is partial
physical preparation; dynamic uniform reads and workgroup conversion remain to
be extracted. Commands and actual result history are in the workspace plan.

Previous workgroup-initialization evidence uses source F18CB9F8… and preserves the
current source manifest, compiled consumer hashes, emitted assets/options and
native reports in workgroup-init-final evidence/snapshot. Focused 145/145,
maintenance 1860/1862 (two llc failures), independent 182/182 (141 outputs and
41 raw native inputs). Selected GPU 33 attempts: 30 PASS, pending-array module
ERROR and two AtomicSerial fatal panics. New cases check all 64 initial zeros,
neighbor reads across barriers and default/resolved specialization-array values;
output buffers begin with a sentinel so missing shader writes cannot pass.
An isolated prior frozen compiler baseline reproduces the unresolved-array
consumer rejection. Resolved output is not acceptance of unresolved input.
Remaining historical GPU, direct/target exports, full research/descriptor corpus,
mesh GPU and browser/SDK/Linux/AOT were not rerun for this source. Commands and
failure history are in the workspace architecture plan; no tests were disabled.

Run from the workspace root with its local .NET SDK. Compiler tests need no host
tools for direct IL compilation or translation; set `SIA_SPIRV_TOOLCHAIN` to a
pinned LLVM/SPIRV-Tools directory to include the offline compilation tests.

```powershell
./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Compiler.Tests/Sia.Spirv.Compiler.Tests.csproj -c Release
./.dotnet/dotnet.exe run --project Sia.Graphics/tools/compiler-validation/CompilerValidation.csproj -c Release -- Sia.Graphics/Sia.Spirv.Compiler.Tests/bin/Release/net11.0/Sia.Spirv.Compiler.Tests.dll Sia.Graphics/Sia.Spirv.Core/bin/Release/net10.0/Sia.Spirv.Core.dll .work/compiler-check/direct
./.dotnet/dotnet.exe run --project Sia.Graphics/tools/compiler-validation/CompilerValidation.csproj -c Release -- Sia.Graphics/Sia.Spirv.Compiler.Tests/bin/Release/net11.0/Sia.Spirv.Compiler.Tests.dll Sia.Graphics/Sia.Spirv.Core/bin/Release/net10.0/Sia.Spirv.Core.dll .work/compiler-check/static static
./.dotnet/dotnet.exe publish Sia.Graphics/tools/compiler-validation/browser/CompilerHost.csproj -c Release
node Sia.Graphics/tools/compiler-validation/wasm-smoke.mjs Sia.Graphics/tools/compiler-validation/browser/bin/Release/net11.0/publish/wwwroot/_framework/dotnet.js Sia.Graphics/Sia.Spirv.Compiler.Tests/bin/Release/net11.0/Sia.Spirv.Compiler.Tests.dll Sia.Graphics/Sia.Spirv.Core/bin/Release/net10.0/Sia.Spirv.Core.dll .work/compiler-check/direct
```

Install the matching SDK's `wasm-tools` workload before publishing. The smoke
check executes an ordinary managed consumer application in Node. Its C# code
compiles each fixture once, writes both formats, compares with native direct
compilation, and exercises both translation directions. Compiler and consumer
share the application's one .NET runtime. The SDK contains managed assemblies;
it does not ship a browser runtime or JavaScript compiler loader.

To verify the SDK's runtime references, publish the same test application with
`-p:CompilerSdkDirectory=<absolute-extracted-SDK-directory>` and run the smoke
script against that application's `wwwroot/_framework/dotnet.js`. The consumer
opts into SDK runtime references with `EnableSpirvRuntimeCompilation=true` and
disables offline compilation with `EnableSpirvCompilation=false`.

`browser-smoke.html` runs the same assertions in a real browser. Serve the test
inputs locally and pass URL parameters `runtime` (the consumer's dotnet.js),
`assembly`, `intrinsics`, and `artifacts` (the exported directory). It also asserts
that exactly one native .NET Wasm runtime was downloaded. Use URLs relative to
that page or absolute localhost URLs. Keep the server bound to loopback and stop
it after verification.

The source-linked GPU runner uses the existing `sia-gpu-diagnostics` skill's
report/assertion host. Pass its directory and the absolute static export path:

```powershell
./.dotnet/dotnet.exe run --project Sia.Graphics/tools/compiler-validation/gpu/NativeRunner.csproj -c Release -p:GpuDiagnosticsRoot=<skill-directory> -p:SpirvValidationArtifactsDirectory=<absolute-static-directory> -p:ShaderSourceId=<source-identifier> -- --mode native --output .work/compiler-check/gpu.json
```

Each compute fixture executes direct/static WGSL/SPIR-V against independently
specified buffers and expectations. The synchronization fixture checks pipeline
creation and zero-count dispatch; it does not establish concurrent memory
ordering. Graphics stages, textures and matrix shaders currently have independent
format validation and Wasm equivalence coverage, not pixel comparison coverage.

Reference `Sia.Spirv.Compiler` from the C# host, through a normal package/project
reference or the SDK opt-in above. Native and browser hosts call the same API:

```csharp
var request = new SpirvModuleCompilationRequest(shaderPe, token, intrinsicPe);
var module = new SpirvCompiler().CompileModule(request);
var wgsl = WgslWriter.Write(module);
var spirv = SpirvWriter.Write(module);
```

Use `ShaderTranslator.SpirvToWgsl(spirv)` or `WgslToSpirv(wgsl)` for existing
shader data. Browser Dawn bindings call the managed translator directly when
creating a SPIR-V shader module; no JS imports, compiler runtime startup or
inter-runtime buffer copies are required. Native/Wgpu backends accept SPIR-V
directly. Default runtime ABI is WebGPU. Intrinsic metadata is the bytes of the
matching Sia.Spirv.Core assembly, supplied explicitly. Keep original, untrimmed
shader PE/IL bytes and their metadata token as data (for example an embedded
resource or an application-provided byte buffer), including in AOT hosts.
Reading a trimmed/AOT host's methods back through reflection does not guarantee
that the shader IL remains available. Input assemblies are parsed as data; they
are not loaded into the CLR. Compiler execution uses the host's normal managed
publish pipeline; it does not require a second Wasm module/runtime.

New memory/file requests share `SpirvCompilationTarget.Default`: WebGPU ABI,
Vulkan 1.2, SPIR-V 1.5 and default resource limits. Maintained direct exporters,
browser inputs and direct native GPU cases pass the request target to both writers.
File/tool options stay on `SpirvFileCompilationRequest`; writer-specific policies
stay on `SpirvWriteOptions`. Legacy memory/file/writer overloads preserve their
previous defaults through adapters. `TargetContractTests` cover early invalid
target rejection, explicit 1.3-1.6 headers/resource interfaces, feature/stage denial,
used-resource limits after override resolution, helper/global use and target identity.
These are partial target contract checks, not full environment/device validation.
The target pipeline validates each whole-module pass and preserves the input
module, so callers may emit both formats in either order. Maintained
`TargetPipelineTests` cover native buffer strides, query helper lowering,
override-resolution failure and byte/word consistency.

`SpirvIntegerLegalizationTests` check integer safety in prepared target IR before
serialization. They cover signed zero/overflow cases, unsigned zero divisors,
operand effects/order, widths/broadcasts, guard opt-out, floating exclusion,
generated-name collisions and borrowed-input preservation. Signed remainder
still expands to quotient/product/subtraction. IntegerSignedRuntime,
IntegerUnsignedRuntime, IntegerOrderedRuntime and IntegerVectorRuntime have
fixed GPU expectations for both outputs and source WGSL; wide-type exports have
independent format validation and require separate device capability acceptance.

`SpirvOutputPolicyTests` cover ordinary/structured position and fragment-depth
outputs, mesh output selection, independent option switches, one entry-body call,
post-barrier mesh publication, name collisions, borrowed bodies and repeatability.
The pure prepared conversions are also exported as compute probes, preventing
fixed-function depth clamping from masking shader arithmetic errors. The optional
`output-policy/` native GPU module checks those exact functions in both formats
and 2x2 color/depth pixels with default and opt-out policies. The native consumer
uses the supplied positions: explicit target Y negation flips the triangle; the
source and opt-out remain in the original half. Mesh format validation is distinct
from actual mesh execution, which is not provided by this GPU module.

`SpirvOutputBuiltinIdentityTests` exercise source functions shadowing builtins,
resolved CFG Call/Builtin reconstruction, effect closure, constant-fold exclusion,
independent builtin signatures and native roundtrip. WGSL target tests cover
function/local disambiguation, entry-name diagnostics and stable numeric override
IDs; name-based override collisions diagnose. Ordinary user names also cannot
select ray-query/atomic lowering or builtin classification emission. The
BuiltinIdentityOrderedProbe runs the exact generated depth conversion after an
effectful user `clamp`, checking three independent float results and a one-call
storage counter in both GPU output formats.

`NativeCanonicalControlFlowTests` exercise ordinary native CFG import,
including raw scalar phi swaps, predecessor/type diagnostics at original binary
spans, captured reads and helper side-effect order. These inputs no longer need
pointer return/phi/load instructions to enter shared CFG/SSA. Pending
specialization-sized arrays keep an explicit migration deferral and unresolved
identity. NativeScalarPhiLoop's raw input and both legalized outputs have four
specified GPU results; use current source/artifact hashes rather than older
batch results. This does not complete native type migration or the full matrix.

`CanonicalControlFlowTests` exercise the internal integer compute migration:
loop-carried simultaneous merges, short-circuit memory reads, switch/loop exits,
index capture, signed shifts, deterministic dumps, explicit deferrals and invalid
SSA/access/call-signature/structure rejection. Natural target control reconstruction
is checked for expected loop/switch counts, nested continue/break-if behavior,
loop/continuing lexical scopes and condition capture before loop-carried copies.
Missing structure diagnoses instead of falling back to a state machine.
Compute entry wrappers and scalar helpers
are checked for actual canonical migration, including the native reader route.
A test-only scalar interpreter evaluates the original,
canonical-adapted and reparsed WGSL/SPIR-V modules against fixed expected buffers
and read traces. It does not call production constant folding and is not a CPU
shader backend. These checks establish regression semantics for that slice; they
do not replace independent SPIR-V validation, GPU execution or full migration
evidence. `CanonicalDataTests` add actual migration checks for concrete numeric
data, vector/matrix/structure/finite-array operations, typed data helpers, maintained
direct CIL math kernels and vertex/fragment IO. Invalid constructor/builtin/swizzle/
conversion signatures are rejected using the shared validator rules. Ordered
effectful calls remain before a pure select, including its SSA let aliases.
`CanonicalEffectTests` add actual atomic, workgroup synchronization, explicit
texture and maintained CIL migration checks, exact native barrier/atomic metadata
and helper convergence propagation. `CanonicalPointerTests` add actual
WGSL/native-reader helper-pointer migration with scalar/composite places,
private memory, nested forwarding, index capture and helper loops. Escaping
slots remain memory, and native same-address arguments stay identical. Pointer
call address-space/access mismatches diagnose. `PointerAliasTests` cover WGSL
write-alias rejection, readonly aliases, unused arguments, projected roots and
transitive global effects. Native matrix alias fixtures retain their original
preservation assertions, with explicit IR alias construction after valid distinct
roots are parsed. `HelperLegalizationTests` exercise native same-address arguments,
raw serialization address identity and both target routes. Both targets now expand
pointer calls without copying pointees; GPU `NativePointerAlias` checks zero/five
inputs against independent expectations for WGSL and SPIR-V. This native IR fixture
has no legal authored WGSL alias-call variant. Block diagnostic filters survive
canonical migration and inlining, including callee defaults and continuing break-if.
`CanonicalAliasTests` mutate CFG calls independently of the old source body,
exercise selection/loop pointer edge roots, transitive global access, retained
unreachable-source rejection and explicit analysis deferrals. Target alias checks
now use canonical instructions for migrated functions. `CanonicalPointerMergeTests`
build genuine pointer block arguments and check storage/function choices, parallel
loop swaps, both loop-exit orientations, borrowed input and lexical filters.
Independent scalar expectations are checked after both output formats are parsed.
`CanonicalSlotTests` begin with explicit memory IR for pointer slots, checking
loaded-address snapshots after overwrites, branch definite assignment, parallel
loop swaps at zero/odd/even iteration counts and allocation-only data promotion.
Uninitialized, partially assigned, escaped and qualified slot memory remains
explicit; implicit zero pointer allocation is rejected by the common verifier.
Three maintained `CanonicalSlot*` GPU fixtures supply seven independent input/
expected-output samples in both formats. They establish common slot promotion,
separately from native pointer-slot frontend migration.
A convergence regression keeps a helper's unconditional barrier outside divergent
address dispatch. `CanonicalPointerReturnTests` cover actual pointer returns/calls
in CFG, nested forwarding, ordinary and early-exit loops, repeated caller-loop
invocations, callee variable shadowing, borrowed graphs, pass traces and alias roots.
Regressions retain addresses used by terminators/edges and initialize escaping
callee slots for each dynamic invocation. Allocation-only facts preserve native
volatile initialization counts and distinguish unknown contents from proven zero.
Diagnostic regressions cover nested callee rules, module inheritance and caller
block overrides. Equal default scopes produce no redundant compound attributes;
required lexical overrides remain even when a consumer lacks attribute support.
`NativeCanonicalSlotHelperTests` add direct, nested and repeated void-helper slot
writes before shared promotion, borrowed inputs, explicit qualified-memory
deferrals and independent numeric expectations. Four `NativeSlotHelper*` fixtures
run original SPIR-V controls alongside both legalized formats. The scalar helper
slot migration regression now requires canonical import/expansion; the atomic
family still requires its explicit deferral.
`NativeUnreachableTests` cover native pointer/ordinary scalar helpers, nested and
repeated calls, continuing, malformed operands, retained source spans, writer
order and uniformity exits. The native terminator has no return operand or edge.
WGSL makes its source-undefined-path choice in legalization; the independent test
interpreter rejects undefined execution rather than assigning it a numeric value.
Five `NativeUnreachable*` GPU fixtures compare original SPIR-V controls and both
legalized outputs using only inputs that follow defined source paths.
`NativeInvocationKillTests` additionally cover native kill import/effects/spans,
callee and caller side effects, typed WGSL helper returns and non-returning call
uniformity. These fragment inputs require independent structural validation and
fragment GPU execution; compute-only readbacks do not certify invocation kill.
The external native runner registers `fragment-kill/` with seven original native input
families and killed/alive samples. It compares original SPIR-V and both legalized
formats in a 1x1 draw, checking storage readbacks; the color fixture additionally
checks R32Uint clear retention (37) on kill and output 109 on the live path.
Earlier storage writes [43,23] must survive kill while later caller writes are
suppressed. Void fragment entries use a depth-only pass and make no color claim.
Rejected raw variable-pointer inputs remain separate failed consumer evidence.
Two continuing families additionally cover nested switch continue, early body
break, body locals captured by continuing and repeated dynamic loop entry. Their
three variants check fixed storage/color results. Shared target legalization
relocates native kill exposed by helper expansion out of continue constructs.
These cases do not certify dynamic helper queries, derivative quads or complete
structural verification, which remain architecture work.
`HelperInvocationQueryTests` add native dynamic helper-state reads with snapshot
and unused-result ordering, explicit boolean/effect/stage verification, initial
helper CPU controls, helper-return uniformity, target capability/extension checks
and SPIR-V 1.6 output. These native-only inputs deliberately have no generated
WGSL sidecar: target preparation records the unsupported query diagnostic.
The external `helper-query/` GPU module attempts original and generated SPIR-V for
live and demoted paths, with independent storage/color oracles. A backend rejecting
the declared capability remains failed consumer evidence, rather than an execution
pass. CPU initial-helper simulation does not establish raster quad equivalence.
Four additional invocation fixtures cover WGSL demotion through a function-pointer
helper, native pointer-return demotion, explicit pointer termination and explicit
color termination. `DemotionFunction` also executes the original WGSL control;
its live result is storage [19,99] and color 128, while its demoted result retains
storage [43,23] and color clear 37. Native pointer controls retain their original
capabilities even when a consumer rejects them. `InvocationDemotionTests` checks
local computation after discard, normal pointer returns, effect traces, derivative
uniformity, fragment-only stage legality, declaration requirements, early target
policy rejection and SPIR-V 1.6 core output. Expectations follow
[WGSL discard](https://www.w3.org/TR/WGSL/#discard-statement),
[SPIR-V demotion](https://github.khronos.org/SPIRV-Registry/extensions/EXT/SPV_EXT_demote_to_helper_invocation.html)
and [explicit termination](https://github.khronos.org/SPIRV-Registry/extensions/KHR/SPV_KHR_terminate_invocation.html).
The continuing fixture calls a non-returning helper, retaining a structurally
valid native continue construct. The internal continuing-marker legalization
check is separate and does not certify that marker as valid native SPIR-V.
Returning a callee-local address still
requires lifetime legalization and diagnoses rather than extending storage lifetime.
Finite pointer merges and called helper returns now have target legalization;
complete frontend convergence remains pending. `NativeCanonicalPointerReturnTests`
add original binary pointer returns, nested forwarding, pointer select, repeated
call-result uses, unused calls, repeated caller-loop invocations, source offsets,
native capability/root validation, qualified memory and explicit legacy deferrals.
`NativeCanonicalPointerPhiTests` add direct native block/phi import, selection and
parallel loop swaps, odd/even iteration counts, nested returns and repeated
invocations, exact volatile access counts, missing/extra/type-invalid phi inputs,
cross-buffer capability rejection before tag lowering and the unchanged public
WGSL gate. The existing return-only effect-count tests also require the native CFG
import trace, ensuring they use the same importer as pointer-phi modules. Entry-call
closure and helper expansion read native frontend graphs
instead of reconstructed metadata bodies. Eligible families enter the same verified
CFG expansion. `NativeCanonicalSlotTests` import original Function slot load/store
memory, CopyObject aliases, branch/loop assignment and load snapshots across later
writes. Shared promotion runs before metadata/helper reconstruction; load-only
modules do not depend on a pointer-return/phi trigger. Malformed slot operands,
types and undefined references diagnose without fallback, and storage-buffer roots
remain checked before dispatch. Qualified/Private slots and escaped helper-parameter
transfers retain explicit migration
deferrals and their original native flags/diagnostics. An uninitialized-load probe
records the unchanged normalizer's finite-provenance diagnostic as a baseline;
it does not establish language-complete undefined-pointer handling.
Six `NativeSlot*` fixtures preserve original `.input.spv` controls and legalized
WGSL/SPIR-V pairs. Remaining slot/null/undefined, arithmetic/comparison,
atomic/opaque/physical-matrix, descriptor arrays, kill/task terminators and native
pointer libraries retain binary normalization before migration.
Internal native validation does not loosen the public WGSL gate. Parse errors do
not silently switch to the old route. Five `NativePointerReturn*` and seven
`NativePointerPhi*` GPU fixtures
retain original `.input.spv` controls as well as legalized WGSL/SPIR-V outputs;
their WGSL sidecar is not the executed source for `source-spirv`. Consumer rejection
must remain separate from independent binary validity and actual GPU readback;
the scalar test interpreter does not establish floating-point execution semantics.
`UniformityTests` cover control/value dependencies, mutable merges and loop
backedges, helper return/argument and pointer-content requirements, input builtin
uniformity, readonly/writable resources and lexical diagnostic filters. They also
check the WGSL target gate against mutated structured IR and the narrow proof
conditions for entry/private-IO zero folding. The original conditional native
memory-fence assertions remain intact. `DiagnosticFilterTests` now reject the
four unfiltered uniformity violations previously accepted with reference checks
disabled; standard WGSL legality, rather than disabled oracle validation, defines
this requirement. `CanonicalUniformityTests` mutate SSA predicates, stores,
helper arguments and instruction filters while the old source body stays valid.
They cover value and pointer-content dependencies through selection/loop incoming
edges, iteration-crossing continue paths, infinite-loop reconvergence, early
returns, native memory fences and explicit deferrals. Migrated functions now use
direct CFG analysis; unmigrated families retain the structured fallback. Public
warnings, subgroup scope extensions and complete canonical integration remain pending.

The optional `canonical/` native GPU catalog consumes exported `.source.wgsl`,
`.wgsl` and `.spv` files named for the maintained constant fixtures in
`CanonicalControlFlowTests`: `SwapLoop`, `EarlyExit`, `CapturedIndex`, `SignedShift`,
`ShortCircuitOr`, `ShortCircuitAnd`, `ScalarHelpers`, `NestedLoops`,
`ContinuingBreakIf` and `BodyContinuingScope`; `CanonicalDataTests` also supplies
`NumericVectorLoop`, `MatrixAggregate`, `OrderedSelect`, `FloatBuiltin` and
`FiniteArrayLoop`. `CanonicalEffectTests` supplies `AtomicSerial`,
`AtomicIndexCapture`, `WorkgroupReduction` and `UniformWorkgroupLoad`, covering
compare/exchange, captured pointer indices, four-invocation accumulation and
uniform workgroup loads. `UniformityTests` supplies `UniformHelper`,
`UniformLoopOverwrite` and `ReconvergentBranches`, checking readonly input
requirements, loop-carried overwrites and barriers after branch reconvergence.
Select `--filter canonical/`
with their directory as `SpirvValidationArtifactsDirectory`. `CanonicalPointerTests`
also supplies `PointerScalar`, `PointerNested`, `PointerIndexCapture`,
`PointerPrivate`, `PointerDistinct` and `PointerLoop`. Fifty-eight fixed input
samples run in all three representations; each case records artifact hashes,
bindings, dispatch and exact expected/actual output words. Export source constants
from the matching test assembly; do not mix these inputs with older compiler DLLs.
Numeric cases use independent expected floating-point bit patterns and integer
words, with exactly representable vector/matrix arithmetic, bounded builtins and
effectful select inputs. They do not establish all floating-point edge cases or
raster pixel equivalence.
Pipeline creation progress identifies a case even if a native consumer aborts.
Keep its process exit/log and report that failure separately; missing reports
or readbacks cannot establish a pass. These checks cover a single native
device/profile, not browser or full parity. Three additional canonical-IR fixtures,
`CanonicalPointerMergeStorage`, `CanonicalPointerMergeFunction` and
`CanonicalPointerMergeSwapLoop`, supply eight fixed inputs in both target formats.
They have no authored WGSL source control; they specifically exercise canonical
pointer block-argument lowering rather than native-reader pre-normalization.
Five `CanonicalPointerReturn*` fixtures supply ten more fixed input samples in
both target formats. Their typed IR helper returns are not authored WGSL pointer-
return functions; independent GPU expectations cover branches, nested/early-exit
loops, repeated calls and per-call escaping-local initialization.

Supported runtime regression coverage includes scalar/vector/matrix math,
same-assembly static helpers, structured stage IO, buffer layouts, workgroup
arrays, atomics, branches/loops, and texture sample/load. Unsupported CIL,
generic or external helpers, and pointer values crossing control-flow stack
edges produce diagnostics. Arbitrary managed programs and full language/extension
coverage remain outside the proven surface. Semantics are defined by the input
language and selected target, independently of another compiler's behavior.
