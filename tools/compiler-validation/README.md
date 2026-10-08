# Integrated compiler checks

Run from the workspace root with its local .NET SDK. Compiler tests need no host
tools for direct IL compilation or translation; set `SIA_SPIRV_TOOLCHAIN` to a
pinned LLVM/SPIRV-Tools directory to include the offline compilation tests.

```powershell
./.dotnet/dotnet.exe test Sia.Graphics/Sia.Spirv.Compiler.Tests/Sia.Spirv.Compiler.Tests.csproj -c Release
./.dotnet/dotnet.exe run --project Sia.Graphics/tools/compiler-validation/CompilerValidation.csproj -c Release -- Sia.Graphics/Sia.Spirv.Compiler.Tests/bin/Release/net11.0/Sia.Spirv.Compiler.Tests.dll Sia.Graphics/Sia.Spirv.Core/bin/Release/net10.0/Sia.Spirv.Core.dll .work/compiler-check/direct
./.dotnet/dotnet.exe run --project Sia.Graphics/tools/compiler-validation/CompilerValidation.csproj -c Release -- Sia.Graphics/Sia.Spirv.Compiler.Tests/bin/Release/net11.0/Sia.Spirv.Compiler.Tests.dll Sia.Graphics/Sia.Spirv.Core/bin/Release/net10.0/Sia.Spirv.Core.dll .work/compiler-check/static static
./.dotnet/dotnet.exe publish Sia.Graphics/Sia.Spirv.Compiler.Browser/Sia.Spirv.Compiler.Browser.csproj -c Release
node Sia.Graphics/tools/compiler-validation/wasm-smoke.mjs Sia.Graphics/Sia.Spirv.Compiler.Browser/sia-spirv-polyfill.js Sia.Graphics/Sia.Spirv.Compiler.Browser/bin/Release/net11.0/publish/wwwroot/_framework/dotnet.js Sia.Graphics/Sia.Spirv.Compiler.Tests/bin/Release/net11.0/Sia.Spirv.Compiler.Tests.dll Sia.Graphics/Sia.Spirv.Core/bin/Release/net10.0/Sia.Spirv.Core.dll .work/compiler-check/direct
```

Install the matching SDK's `wasm-tools` workload before publishing. The smoke
check executes Wasm in Node, compares both output formats byte-for-byte with
native direct compilation, and exercises both translation directions. It can
also target an extracted SDK package's loader and runtime.

`browser-smoke.html` runs the same assertions in a real browser. Serve the test
inputs locally and pass URL parameters `loader`, `runtime`, `assembly`,
`intrinsics`, and `artifacts` (the exported directory). Optional `hostRuntime`
creates a separate .NET runtime first to check coexistence with a managed app.
Use URLs relative to that page or absolute localhost URLs. Keep the server bound
to loopback and stop it after verification.

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

Public runtime usage is `new SpirvCompiler().CompileModule(shaderPe, token,
intrinsicPe)`, followed by `WgslWriter.Write(module)` or
`SpirvWriter.Write(module)`. Default runtime ABI is WebGPU. Intrinsic metadata is
the bytes of the matching Sia.Spirv.Core assembly, supplied explicitly. Input
assemblies are parsed as data; they are not loaded into the CLR.

Supported runtime regression coverage includes scalar/vector/matrix math,
same-assembly static helpers, structured stage IO, buffer layouts, workgroup
arrays, atomics, branches/loops, and texture sample/load. Unsupported CIL,
generic or external helpers, and pointer values crossing control-flow stack
edges produce diagnostics. Arbitrary managed programs and complete upstream
Naga compatibility are outside the proven coverage.
