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

For a declaration that depends on component props, pass those dependencies as
values and use a static callback. All values that change resource access must be
included in the dependencies. Equal dependencies retain the registered pass;
changed dependencies rebuild it without changing its declaration order.

```csharp
hooks.UseRenderGraphPass(
    registry, draw, "draw", output,
    static (in RenderGraphTextureKey target, RenderGraphPassDeclarationBuilder pass) =>
        pass.Write(target, RenderGraphTextureUsage.RenderAttachment));
```

The original callback overload remains available for stable declarations.
Capturing a new closure on each expansion changes its identity and re-registers
the pass. Execution handlers should likewise use stable callbacks or methods on
the existing resource owner, reading its current frame data.

Replacing an imported resource binding retains the compiled plan and updates
only changed bindings. Removing a live binding still fails validation; changing
a resource descriptor or pass declaration rebuilds the plan. Prepared bindings
are mutable registry-owned execution state, not a snapshot of an earlier frame.

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
