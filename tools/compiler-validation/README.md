# Integrated compiler checks

See the [current pipeline/target map](../../docs/compiler-architecture.md) and
[proposed improvement plan](../../docs/compiler-roadmap.md). `evidence.json`
records the original PR baseline, not verification of later source changes;
`renewal-evidence.json` records the cleanup follow-up.

Latest writer/translator migration: source F53E737A…, Compiler EEBD8F39….
All public writer and translator calls require an explicit target; writer options
contain only emission policies. Maintained consumers choose their target directly,
including explicit versions for mesh and Vulkan 1.3 LocalSizeId fixtures. Seven
new contracts cover required API shape, null-target ordering and translator policy.
Initial regression found 14 caller omissions (LocalSizeId environment and implicit
version assumptions); callers now select the same target for preparation/output,
and original behavior assertions remain. No tests were removed or disabled.

Passed: maintenance 2266/2266, formats 233/233 plus 22/22 query/handle,
41/41 frozen native replays plus 82/82 independent input/output formats, four
direct CIL WGSL/SPIR-V GPU cases, CLI build and translation/format validation,
Dawn WebGPU browser-library build. Evidence is in
`.work/compiler-architecture-first/writer-api-*`. The browser executable build
still fails NU1102 for exact rc.2 packages; actual browser, SDK/Linux/AOT and
full research parity are unverified. Previous batch records below are historical.

Latest public compiler API migration: source B4DEEE64…, Compiler 18E1514D….
Public PE/token/options and file/path/options adapters, the options type and
memory forwarding properties are removed. CLI and maintained tests use requests;
variants replace named resource limits within the request's target. CLI defaults
to WebGPU ABI. Writer no-target adapters remain pending.

Passed: maintenance 2259/2259 (focused 60/60), CLI build, default and variants
compilation with 22/44 manifests and verified SPIR-V hashes, four native GPU cases
(IntegerControlFlow and SpeculativeSelection, direct WGSL/SPIR-V) on Intel HD 620
Vulkan. Integer words match exactly; the float branch matches its fixed formula
within 0.00001. Four Compiler DLL copies match. Evidence and actual scripts/logs
are in `.work/compiler-architecture-first/public-compiler-api-*`.
Blocked: local browser build reports NU1102 for exact .NET 11 rc.2 ILLink,
WebAssembly.Pack and Mono.browser-wasm packages. Not run: actual browser,
SDK/Linux/AOT, new format exports/frozen replay and full research parity.
The ray-query records below describe the preceding source, not this API batch.

Latest SPIR-V query guard migration: source 2F3A9B12…, Compiler BED9439A….
High-level query state/descriptor/traversal/candidate/range/getter guards are
canonical target control flow. The writer query-state/guard implementation is
removed; unlegalized high-level calls diagnose instead of synthesizing semantics.
Native raw-query graphs are unchanged. Forty-one new contracts cover malformed
descriptors, exhausted/uninitialized queries, candidate/committed kinds, distance
bounds, allocation resets, origins/filters and poisoned owned Bodies. The query
stub observes guard decisions; it does not simulate or verify GPU traversal.

Passed: maintenance 2253/2253, formats 233/233 maintained plus 22/22 query/handle
outputs and native replays, frozen original replay 41/41 plus independent
input/output formats 82/82. Five Compiler DLL copies match. Actual scripts,
manifests, TRX, raw format reports and hashes are in
`.work/compiler-architecture-first/canonical-ray-query-*`.
Failures retained: 22 test-stub failures before select support, a test diagnostic
severity compile error, and the temporary replay-validator directory suffix.
Not run: current-source GPU, repeated-export determinism, WASM/real browser,
SDK/Linux/AOT and full research consumers. Other target adapters remain open.
The user authorized breaking public legacy API removal on 2026-10-10; that
consumer migration is the next architecture batch and is not yet complete.

Latest WGSL termination migration: source 91017049…, Compiler E9538038….
Owned graphs retain continuing relocation, demotion and typed returns; uniformity
checks original non-returning control first. Structured relocation is retired.
Return origins survive source/native import, SSA mapping and target reconstruction.
Borrowed graphs are unchanged; only explicit deferrals retain the Body adapter.

Passed: maintenance 2212/2212, independent formats 233/233, GPU 39/39. Fifteen
WGSL attempts execute five exports with poisoned owned Bodies; 24 input/output
SPIR-V controls cover kill, nested/repeated continuing, break paths and defined
unreachable execution. Raw storage words and R32Uint pixels match fixed oracles.
Three terminate WGSL attempts pass; native terminate-extension controls were not
rerun and their previous consumer rejection remains open. Four DLL copies match.
Failures retained: two compiler/test build errors and a temporary exporter assuming
an authored-source sidecar for native input. No assertions removed. Earlier source
snapshots (101 focused, full 2211 twice, GPU 39) remain distinct from final evidence.
Actual manifests, TRX/scripts/reports are under
`.work/compiler-architecture-first/canonical-wgsl-termination-*`.
Not run: frozen replay, repeat-export determinism, WASM, real browser, SDK/Linux/AOT,
full research/parity consumers. Query/memory/layout, constructors and frontend/LLVM
convergence remain open.

Latest WGSL target graph migration: source 778F73B8…,
Compiler 5578AA90…. Entry builtin/private-slot facts, pointer helpers, collective
recovery and uniformity retain owned CFGs until the explicit structured target
boundary. Recovery retains unchanged deferrals; empty inlined helper diagnostic
scopes survive. No new public interface/dependency.

Passed: maintenance 2183/2183, independent SPIR-V formats 233/233; 21 isolated
GPU attempts on scalar helpers, captured indices and loop-carried swaps. Seven
canonical-WGSL samples use exports from deliberately poisoned owned Bodies;
source WGSL and SPIR-V provide controls. All raw words match fixed expectations.
The report records device, source/options, shader/DLL hashes and raw readback.
Failures retained: helper rename assumption/nullable warning, temporary exporter
compile errors, full 2171 PASS/10 FAIL and 2181 PASS/1 FAIL, then 21 suite errors
from missing canonical/ filter prefix with zero selected tests. Current checks
pass; existing assertions are preserved. Logs/scripts/manifests are under
`.work/compiler-architecture-first/canonical-wgsl-target-*`.
Not run: frozen replay, repeat-export determinism, WASM, real browser, SDK/Linux/AOT
and full research consumers. These scalar GPU results do not prove concurrent
workgroup or query traversal behavior. Remaining WGSL query/memory/layout and
termination adapters, constructors and frontend/LLVM convergence remain open.

Latest deferred-helper migration: source
`9CC19A9B35995EC4218C31B5D121B2EB43499E348519B453375449032507C2EE`,
Compiler `9C8FE148D9CA731675DAC1A3B29A33A2223333FFA4A68B6B6B4463D52FF744F3`.
PrepareSpirv no longer uses a whole-module structured adapter. Readable explicit
deferrals import individually; unreadable helpers adapt only related callers.
Owned graphs supply effects and types across readers, wrappers and target helper
construction. Qualified native pointer-slot memory retains origin/access flags
and declares StorageBuffer or full variable-pointer capabilities as appropriate;
held Function-space pointers fail before emission.

Passed: maintenance 2169/2169 (no skips), maintained independent formats 233/233
and six additional helper/slot outputs 6/6. Export builds have no warnings/errors.
Ten new cases and one updated readable-deferral contract cover ownership, SSA,
effects, unsupported aliases, qualified memory and target capabilities. Retained
failure history includes the three before-change failures, stale effect summaries,
the obsolete deferral expectation, invalid Function-space fixture, missing pointer
capability, one fixture constructor build error and scalar interpreter gaps.
Actual manifests, TRX, scripts and logs are under
`.work/compiler-architecture-first/canonical-deferred-helpers-*`.
Not run for this source: GPU, WASM, real browser, SDK/Linux/AOT or full research
consumers. Frozen replay passes 41/41 with input/output formats 82/82; 41/41 outputs
match the earlier termination batch, using identical frozen inputs.
Query guards, WGSL target, constructors and frontend/LLVM convergence remain open.

Latest resource-handle admission: source
`A01D1F090CEE7312BB35A157BB6FA85A6E2764EB550FBA75C13CC9D873B0FCB9`,
Compiler `7FA706E849B928D31AD626CAFF5363D04E339E990727D4F5BB1D98C65B9F96F4`.
Image/sampler/acceleration helper arguments and opaque query locals now enter
canonical graphs. Query allocation retains identity and vertex-return type;
the structured adapter resets state at the allocation inside each loop iteration.
Query updates/getters have shared effects and remain conservatively nonuniform.
Ten new contracts cover borrowed graph preservation with stale bodies, handles,
allocation position, effects and uniform barrier rejection. Three existing
analysis/trace contracts now expect canonical query admission.

Passed: compiler maintenance 2159/2159; independent spirv-val checks 14/14
(seven source outputs and seven native replays), export build without warnings
or errors. Failure history retains reserved-keyword fixture errors, overly broad
alias assertions, the two uniformity regressions and obsolete deferral
expectations; none is silently skipped. Actual manifests, commands and results
are under `.work/compiler-architecture-first/canonical-resource-handles-*`.
Not run for this source: GPU, full fixture/replay/determinism matrix, WASM,
real browser, SDK/Linux/AOT and remaining research consumers. Deferred helpers,
query guard/state legalization and the complete roadmap remain open.

Previous invocation-termination migration: source
`CCFE45DE6B2F10EF22056ABFE85399FD27610B5496D578DAFE849ADC5DD646D0`,
Compiler `A4B234CB580CF527F7A8FBF1E0D49300AA566DA800EBB179D2D702857E8FF8B4`.
Owned CFG termination no longer forces a whole-module SPIR-V adapter. Distinct
selection joins preserve nested continue exits, former break-if exits and already
evaluated SSA edge arguments; common latches keep the continue construct free of
termination. Zero-backedge loops retain target structural labels and typed Phi
incoming values. Twenty-eight new contracts cover stale declaration bodies,
kill/terminate, repeated/nested loops, skipped merge effects, zero/multiple
backedges, origins, borrowed graph preservation and explicit deferrals.

Passed: maintenance 2149/2149, independent 184 outputs + 49 generated native
inputs = 233/233, frozen 41-input replay and 82/82 input/output validation, 689
repeated artifacts identical. Five Compiler DLL copies match. Compared with the
pointer batch, 212/218 SPIR-V artifacts match and six outputs change; native inputs
in that comparison are unchanged. Export/replay/GPU builds report no warnings or
errors. Toolchain 0.0.22 supplies spirv-val 2026.2; reports retain per-case target
environments, options and hashes.

GPU: 146 isolated attempts, 94 PASS/52 ERROR. Successful storage raw words and
R32Uint pixels match fixed oracles on Intel HD Graphics 620/Vulkan compatibility.
Current wgpu/Naga rejects VariablePointersStorageBuffer (10 attempts),
DemoteToHelperInvocation (6), or SPV_KHR_terminate_invocation (36), before readback.
Keep these as errors: independent validity does not establish consumer support.
Generated WGSL for explicit termination passes, while its SPIR-V extension remains
unsupported by this consumer. Prior pointer source-WGSL native aborts remain open
and were not rerun here. No baseline GPU reproduction classifies new errors as
pre-existing.

Failure history retained: missing test namespace; 44 PASS/22 FAIL from selection
boundaries; 48 PASS/18 FAIL from nested continue exits; then 66/66 and 75/75 pass.
The first independent run was 231/233: two new fixtures incorrectly raised the
SPIR-V version without rebuilding the entry interface. They now preserve the
original version and declare the terminate extension; corrected full tests and
233/233 validation pass. The failed inputs, manifests, TRX and logs remain intact.

Workspace-specific commands, reports and source manifests are recorded under
`.work/compiler-architecture-first/canonical-termination-*`, with the handoff and
`record-canonical-termination-evidence.py` checking current source, raw baseline,
executed artifacts, reports and DLL identities. Previous ab5c4b8 CI passed Ubuntu
compiler 2121/2121, WASM publish and Node managed-host 22 fixtures; those results
belong to that previous source. Current-source real browser GPU, SDK/Linux/AOT,
mesh GPU and remaining historical corpus/descriptor/GPU matrix have not run.
Remaining pointer/query adapters, WGSL target, frontend/LLVM, initialization/mesh
and feature policy work still belongs to the original full roadmap.

Previous pointer-helper graph migration (source AA5E4452…) expands owned
pointer-argument helpers with the existing canonical CFG inliner. Caller SSA IDs,
captured argument addresses, return joins, lexical diagnostics and native memory
operands survive; borrowed caller/callee graphs are copied. Private helpers are
removed only after remaining graph/deferred calls are accounted for. Deferred
pointer/query and invocation-termination families keep the temporary whole-module
adapter. Internal target/constant validation accepts existing native canonical
pointer address spaces; public WGSL source legality remains enforced.
Twenty-one new contracts and maintenance 2121/2121 pass; independent format
217/217 passes. Of 218 SPIR-V artifacts compared with the constant batch, 205 match
and 13 change, including one newly generated native-input artifact. Current-source
frozen-input replay and repeat-export determinism have not run.
Forty pointer GPU attempts are 38 PASS/2 NATIVE_ABORT: PointerIndexCapture source
WGSL for zero/five panics in Naga 29.0.3 (Expression [8] is not cached), exit
-1073740791, with no report/readback evidence. That authored input matches the
preceding batch; baseline GPU reproduction was not run, so these are not labelled
pre-existing. Both generated WGSL/SPIR-V variants pass, as do nested helpers,
private pointers, distinct addresses, loops and native alias probes with fixed raw
word expectations. Four Compiler copies match 3BE9D155…; evidence and failed runs
remain under canonical-pointer-arguments-* in the workspace task area.
WGSL target, initialization/mesh construction, frontend/LLVM convergence, deferred
families and full research/consumer coverage remain open. Remaining historical
GPU/corpus/descriptor, real browser GPU, SDK/Linux/AOT and mesh GPU are not run
for this source. Prior 05e30b1 CI passes Ubuntu compiler 2100/2100, WASM publish
and Node managed-host 22 fixtures; it does not verify this new source.

Previous pipeline-constant graph migration (source 4C1D37AF…) resolves values and
types directly in `CanonicalModule`, preserving SSA identities, captured addresses,
edge arguments, loops, diagnostics and native memory operands. Owned declaration
bodies are not read; explicit deferrals retain their structured adapter. Pipeline
values no longer force a whole-module reconstruction. Target-deferred pointer
merges receive selected memory-arm legalization, and resource/call-closure checks
read the prepared graphs so resolved descriptor-array limits remain enforced.
No new public API, IR or dependency is introduced.
Thirty-two contracts and maintenance 2100/2100 pass. Independent format 217/217
passes; all 218 compared SPIR-V artifacts match the preceding integer batch.
GPU workgroup-initialization probes are 8 PASS/1 ERROR: Pending5 canonical SPIR-V
still fails import with invalid array size %6 and has no readback. Passing probes
retain fixed expected raw words. Four Compiler DLL copies match 7C825AD4…;
export source 7E0DB4F7… differs only in the two test files subsequently repaired.
Original failed runs remain recorded under canonical-constants-* in the workspace
task area. Native specialization composites remain inline; named derived values
remain scalar-only, and opaque WGSL locals still defer as Declare. Pointer/query
helpers, invocation termination, WGSL target, initialization/mesh construction,
frontends/LLVM and full research/consumer coverage remain open. Current-source
frozen replay, repeat-export determinism, remaining GPU, browser/SDK/Linux/AOT
and full corpus/descriptor checks have not run in this batch.
Previous 01422dc CI succeeded: Ubuntu compiler 2068/2068, WASM publish and Node
managed-host 22 fixtures. This is evidence for that commit, not this new source;
unlisted Rust checks do not establish repair of the earlier configuration error.

Previous integer graph migration (compiler/test source 61AFB62C…, GPU source
ACF40E1A…) constructs integer safety/remainder helpers directly as pure typed SSA.
Caller result identities, evaluated operands, edges/loops, spans and filters are
retained. Ordinary SPIR-V target preparation now keeps shared graphs through
integer/entry/layout lowering. Pipeline values, pointer/query helper declarations
and invocation termination retain an explicit temporary whole-module adapter;
WGSL target preparation also retains its structured path.
Twenty-one contracts and maintenance 2068/2068 pass; format 217/217 plus two pointer
formats, frozen input/output format 82/82, 160 reverse routes and 657 deterministic
files pass. Of 218 SPIR-V files, 105 match the previous batch and 113 change;
all 41 frozen inputs are retained, while only 12 replay outputs match bytewise.
GPU attempts are 122 PASS/30 ERROR: the six prior error categories and 24 newly
tested 16/64-bit consumer cases fail before execution. The 39 signed/unsigned,
ordered and vector cases and twelve 32-bit width/broadcast controls pass their
fixed raw-word oracles. Narrow integer WGSL enables/SPIR-V widths and 64-bit
casts/capability requirements remain compatibility work. Source manifests differ
only in the GPU fixture registrations; tested compiler/test files and all five
Compiler DLL copies match. Evidence is under canonical-integer-* in the workspace
task area. Constants/pointers/termination, WGSL target, initialization/mesh
constructors, deferrals, frontend/LLVM and full research/consumer gates remain open.

Previous canonical target entry migration (D27A86F6…) retains owned graphs through
the internal entry/physical-layout entrance, copying borrowed graphs before target
changes. Pure workgroup and output conversion helpers are constructed directly as
typed SSA; initialization retains its first captured graph. Explicit target
deferrals adapt the executable graph rather than an obsolete declaration body.
Ten new contracts and maintenance 2047/2047 pass, with independent format 217/217,
two pointer formats, frozen input/output format 82/82, 160 reverse routes and
657 deterministic files. Of 218 compared SPIR-V files, 211 match; seven workgroup
outputs change. All 41 frozen replay outputs match. GPU attempts are 71 PASS /
6 ERROR: three Vulkan memory-model imports, two original continuing WGSL name
redefinitions and one unresolved specialization-array import fail before execution.
The last artifact matches the preceding batch. Output conversion, raster policy
and resolved initialization readbacks pass. Evidence and original failed test/tool
runs are retained under canonical-target-entry-* in the workspace task area.
The shared ShaderTargetLowering entrance still reconstructs structured bodies
before constant/pointer/termination/integer passes. Initialization/mesh constructors,
deferrals, frontend/LLVM and full research/consumer gates remain open.

Previous uniform graph migration (779A518A…) captures target graphs before physical
type discovery and builds uniform read/selection helpers directly as CFG/SSA.
Captured indices, caller results, loop/edge topology, native memory operands and
diagnostic origins survive. Newly generated helpers propagate memory effects to
callers before mixed validation; structured access handles explicit deferrals.
Seven new contracts, related 91/91, maintenance 2037/2037, independent format
217/217 plus two pointer-return formats, frozen input/output format 82/82,
160 reverse routes and 657 deterministic files pass. Of 218 SPIR-V files, 214
match the previous batch and four uniform-layout outputs change; all 41 frozen
replay outputs match. GPU attempts are 32 PASS/5 ERROR: three existing Vulkan
memory-model imports and two newly observed original WGSL continuing-name
redefinitions fail before dispatch. Canonical continuing variants and nested
uniform selection/zero fallback return the expected values. Source/consumer
identities, failures and readbacks are recorded under uniform-graph-migration-*.
The shared canonical-to-target entrance, pure workgroup helper construction,
deferrals, frontend/LLVM and full research/consumer gates remain open.

Previous workgroup graph migration (11F49A83…) records seven contracts, maintenance
2030/2030, independent format 217/217 plus two raw pointer-return fixtures,
frozen native input/output 82/82, 160 reverse PASS and 657 deterministic files.
Targeted GPU results are 24 PASS/3 ERROR before execution. Of 218 compared SPIR-V
files, 187 match; 31 regenerated native inputs differ only by duplicate capability
declarations. All 41 frozen replay outputs match. Source/DLL/asset identities,
actual GPU readbacks and original failures are recorded separately under
workgroup-graph-migration-* in the workspace task area. Current-source browser,
SDK, Linux, AOT, mesh GPU and full corpus/descriptor checks were not run.
Uniform/deferred adapters and frontend/LLVM migration still remain open.

Previous synchronization migration (7BCB90E0…) records twelve contracts, maintenance
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
var wgsl = WgslWriter.Write(module, request.Target);
var spirv = SpirvWriter.Write(module, request.Target);
```

Use `ShaderTranslator.SpirvToWgsl(spirv, target)` or `WgslToSpirv(wgsl, target)` for existing
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
stay on `SpirvWriteOptions`; its former Target property is removed. Legacy
memory/file/writer/translator adapters are removed. `TargetContractTests` cover early invalid
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
