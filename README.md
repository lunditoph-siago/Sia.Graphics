# Sia.Graphics

`Sia.Graphics` contains the backend-neutral render-graph IR, WebGPU lowering,
and Sia.NET reactive integration.

## Getting started

Install the optional SPIR-V workload:

```bash
dotnet tool install --global Sia.Spirv.Bootstrap
dotnet spirv install
```

Enable C# kernel compilation in the project:

```xml
<PropertyGroup>
  <EnableSpirvCompilation>true</EnableSpirvCompilation>
</PropertyGroup>
```

Compute kernels may use scalar or `Sia.Math` vector storage buffers, sequential
unmanaged structs, workgroup memory, barriers, and integer atomics:

```csharp
[SpirvKernel(64)]
static void Accumulate(
    StorageBuffer<uint> totals,
    WorkgroupMemory<uint> shared)
{
    var local = Gpu.LocalInvocationId.X;
    shared[local] = 1u;
    Gpu.Barrier();
    totals.AtomicAdd(0u, shared.AtomicAdd(0u, 1u));
}
```

`WorkgroupMemory<T>` contains one element per local invocation and does not
consume a descriptor binding. Atomics currently support `int` and `uint`
`AtomicAdd`/`AtomicExchange`. Storage-buffer structs must use sequential layout,
contain only supported scalar/vector fields, and are described in the artifact
manifest with explicit field offsets, alignment, size, and array stride.

Sampled 2D and 2D-array textures support mip-level `Load` and `SampleLevel`
operations. Texture operations return one selected component so shaders can
avoid materializing unused channels.

## Reactive render graphs

```csharp
using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;

var output = new RenderGraphTextureKey("output");
var draw = new RenderGraphPassKey("draw");

ReactiveComponent<DrawProps> component =
    static (in DrawProps props, ref Hooks hooks) => {
        hooks.UseRenderGraphTexture(
            props.Registry,
            props.Output,
            new RenderGraphTextureDescriptor(
                "output",
                RenderGraphTextureFormat.BGRA8Unorm,
                props.Width,
                props.Height));
        hooks.UseRenderGraphPass(
            props.Registry,
            props.Draw,
            "draw",
            static pass => pass.Write(
                new RenderGraphTextureKey("output"),
                RenderGraphTextureUsage.RenderAttachment));
        hooks.UseWgpuRenderGraphPassHandler(
            props.Registry,
            props.Draw,
            static context => {
                var encoder = context.CommandEncoder;
                var target = context.GetTextureView(
                    new RenderGraphTextureKey("output"));
                // Encode WebGPU commands with encoder and target.
            });
        return Reactive.None;
    };
```

Configure the borrowed WebGPU device and queue once, then execute directly:

```csharp
var registry = world.ConfigureWgpuRenderGraph(device, queue);
var mount = world.Mount(
    component,
    new DrawProps(registry, output, draw, width, height));

world.ExecuteWgpuRenderGraph();
```
