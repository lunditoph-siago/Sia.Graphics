# Sia.Graphics

`Sia.Graphics` contains the backend-neutral render-graph IR, WebGPU lowering,
and Sia.NET reactive integration.

## WebGPU ownership and data access

`WgpuHandle<T>` is a lightweight native reference. Copies borrow the same
reference; copying does not retain it. `Wgpu.Move(ref handle)` clears the source,
and `Wgpu.Release(ref handle)` clears only the supplied variable. Keep borrowed
handles and mapped spans within their owner's lifetime.

`world.AttachWgpu(entity, ref handle)` validates the target World and existing
component before taking ownership. After validation it clears the input and
either registers the resource or releases it during rollback. Rejected arguments
leave the input intact. The by-value overload uses the same ownership rules;
the caller must stop treating its remaining copy as owned after transfer.
`OwnWgpu` consumes a validated input, including when entity creation fails.
Custom release handlers are attempted once, in reverse acquisition order;
cleanup continues after errors and reports an aggregate exception.

`MapBufferReadAsync` retains the buffer until native completion. Cancellation is
observed after that completion; a successful canceled mapping is unmapped before
the Task becomes canceled. Keep the buffer alive for subsequent access, and
avoid overlapping map/unmap operations on it. `ReadMappedRange` and
`WriteMappedRange` copy to/from caller spans. Synchronous mapping pumps events
with yielding, aborts on timeout, and is unavailable in browsers.

The desktop backend currently uses wgpu-native 29. Its mapped-copy and map-state
entry points are exported but unimplemented. The copy wrappers use mapped
ranges on desktop and dedicated copy functions in browsers; desktop
`GetBufferMapState` throws `PlatformNotSupportedException` safely.
Whole-map requests resolve to the remaining buffer length before native dispatch.
Native zero-length bindings and vertex-buffer unbinding also throw a managed
unsupported exception; browser vertex unbinding remains supported. Index formats
must be Uint16 or Uint32.

Wrapper callbacks adapt Wasm's indirect string-view arguments before decoding
messages. Browser adapters support only one successful device request; acquire
a fresh adapter for another device. For emdawn versions that omit surface usage
flags, capability snapshots provide the guaranteed canvas copy, sampled-texture
and render-attachment usages.

Use the `SetVertexBuffer` / `SetIndexBuffer` overloads accepting an existing
`WgpuBufferInfo` to check ranges without querying native size on each bind.
The metadata must describe that buffer. Existing overloads keep native size
validation. Texture upload/copy overloads accept full layouts, extents, origins
and mip descriptors. Capability queries return managed snapshots and release
their temporary native members internally. `OnSubmittedWorkDoneAsync` waits for
prior queue submissions; cancellation cancels the wait, not GPU work.

Browser `CopyBufferToTexture` encodes padded multi-image 3D uploads one depth
slice at a time, preserving the source image stride without repacking or creating
GPU resources. This avoids an image-stride corruption reproduced on mobile
Imagination/Rogue. Native and ordinary browser transfers use the direct command.

For browser array-texture readback affected by backend corruption, provide a
caller-owned intermediate texture descriptor to the `CopyTextureToBuffer`
overload. It encodes texture-to-texture then texture-to-buffer; the intermediate
must have compatible format/dimension, sufficient extent and CopyDst/CopySrc
usage. The caller owns its lifetime and may reuse it. This path adds one GPU
copy and does not allocate or retain a texture inside the wrapper:

```csharp
Wgpu.CopyTextureToBuffer(encoder, source, readback, size, intermediate);
```

The four-argument overload remains a direct readback command and inherits the
backend's restrictions. The intermediate path is an explicit compatibility choice.

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
