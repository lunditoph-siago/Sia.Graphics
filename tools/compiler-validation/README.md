# Integrated compiler checks

See the [current pipeline/target map](../../docs/compiler-architecture.md) and
[proposed improvement plan](../../docs/compiler-roadmap.md). `evidence.json`
records the original PR baseline, not verification of later source changes;
`renewal-evidence.json` records the cleanup follow-up.

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
var module = new SpirvCompiler().CompileModule(shaderPe, token, intrinsicPe);
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

Supported runtime regression coverage includes scalar/vector/matrix math,
same-assembly static helpers, structured stage IO, buffer layouts, workgroup
arrays, atomics, branches/loops, and texture sample/load. Unsupported CIL,
generic or external helpers, and pointer values crossing control-flow stack
edges produce diagnostics. Arbitrary managed programs and full language/extension
coverage remain outside the proven surface. Semantics are defined by the input
language and selected target, independently of another compiler's behavior.
