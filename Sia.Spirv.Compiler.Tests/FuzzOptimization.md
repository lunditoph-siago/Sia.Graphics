# Nonzero divisor optimization

Follow-up to [the native compiler comparison](UpstreamComparison.md), 2026-11-06.
The [generated evidence](FuzzOptimization.json) records per-source hashes,
before/after binary hashes and metrics, tool fingerprints and reproduction code.

## Problem and change

`x / (y | 1u)` and `x % (y | 1u)` cannot divide by zero, but the compiler still
emitted a checked integer helper. For these operations it now proves nonzero
divisors through immutable SSA aliases, integer OR, vector construction,
width-preserving integer conversions and selects with two nonzero alternatives.
The structured fallback applies the same conservative rules. This changes no
public API and adds no production dependency.

The proof does not inspect mutable memory, trust override defaults, propagate
through unknown block parameters or assume wrapping arithmetic preserves
nonzero values. AND can clear every set bit; narrowing can discard the only set
bit. Every vector lane must be nonzero. Signed division/remainder retains its
overflow handling, including the most-negative integer divided by minus one.
The pass reuses already evaluated operands and does not execute, duplicate or
move calls/loads. A regression checks two atomic operands and their final memory.

## Reproduction and verification

Baseline source is PR #119 commit
`35da7d4f728fc9fc71290d4e04d580019466036c`. The first focused run reproduced the
missed optimization: 8 failed, 7 passed. After the change those 15 pass. Further
regressions cover narrowing, a zero incoming branch, unresolved overrides and
independently calculated unsigned/signed boundary results.

The full Release solution run passed **194 tests, zero failures and zero skips**.
Native Tint/Naga paths were supplied for the optional upstream-tool tests.

```text
dotnet test Sia.Graphics.slnx -c Release --blame-hang --blame-hang-timeout 5m -m:1 -p:UseSharedCompilation=false
```

The differential corpus contains the exact 160 typed u32 programs from two fixed
seeds (`0x5eed`, 96 programs; `0x123400`, 64), each with two equivalent source
variants. They cover arithmetic, vectors, initialized arrays, loop/switch/
continue edges, helper effects and pointer arguments. These programs were
deliberately designed with masked shift counts and nonzero runtime divisors.
They therefore stress this proof and are not an unbiased shader workload.

Before and after, all **320** sources compile through Sia to SPIR-V 1.3, translate
back to WGSL, pass `spirv-val --target-env vulkan1.1`, and match the independent
Python source evaluator in a strict integer SPIR-V interpreter. Each binary is
checked on four boundary input vectors: **1,280 executions per revision**.

After the change, both native tools recompile every Sia-generated WGSL file:
**640** additional binaries validate and match the same expected values in
**2,560 executions**. The pointer-family cases which panic in the pinned Naga
when given original WGSL succeed after Sia's existing pointer lowering. This
uses compiler output as an interchange check; it is not a fix to Naga itself.

The native executables are the preceding pinned Dawn/wgpu builds. Naga uses
`--spirv-version 1.3 --index-bounds-check-policy Unchecked
--buffer-bounds-check-policy Unchecked`; Tint uses `--spirv-version 1.3
--disable-robustness`. Native validation remains enabled. No binary version
header rewriting is used. The evidence embeds the C# batch bridge, original
typed generator, strict interpreter, check scripts and generator; project/source
paths must be remapped to a local checkout when reproducing outside the workspace.

## Static output results

All before/after metrics use the same 320 original WGSL inputs, target and options.
172 binaries shrink; 148 stay the same size; none grows.

| Measure | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Binary bytes | 655,096 | 598,408 | 8.65% |
| Instructions excluding debug | 35,798 | 33,186 | 7.30% |
| Function call instructions | 1,536 | 1,168 | 23.96% |
| Loads / stores | 1,592 / 1,544 | 1,592 / 1,544 | unchanged |
| Phi instructions | 324 | 324 | unchanged |
| Function variables | 106 | 106 | unchanged |

On the 268 original sources where all three writers produce valid, independently
checked binaries, the raw native-writer totals are:

| Measure | Sia after | Tint | Naga |
| --- | ---: | ---: | ---: |
| Binary bytes | 491,000 | 595,632 | 524,376 |
| Instructions excluding debug | 27,208 | 30,651 | 33,363 |
| Loads / stores | 1,176 / 1,232 | 3,332 / 2,174 | 3,386 / 2,174 |
| Function variables | 54 | 672 | 726 |

The 52 original-source Naga panics are excluded from that three-writer table,
not counted as zero-sized outputs. Counts include types/layout/constants and
reflect different SSA/local-storage choices. They are not optimized GPU machine
code, GPU timings, register pressure or compilation-time benchmarks.

## Limits

The integer oracle covers one invocation and defined u32 operations, not GPU
execution, floating point, synchronization or all SPIR-V semantics. The existing
`point_size` and deep structuring issues tracked in #120 remain. Coherent-memory
annotations are preserved; this patch does not strip semantics to make another
WGSL dialect accept them. Existing broad comparison failures remain recorded in
the earlier report rather than being relabeled as passes.
