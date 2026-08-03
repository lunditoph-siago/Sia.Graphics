using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public static partial class RenderGraphHooks
{
    public static void UseRenderGraphBuffer(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphBufferKey key,
        in RenderGraphBufferDescriptor descriptor)
    {
        hooks.UseEffect(
            new BufferDependencies(registry, key, descriptor),
            static (in BufferDependencies dependencies) =>
                dependencies.Registry.RegisterBuffer(
                    dependencies.Key,
                    dependencies.Descriptor),
            DisposeRegistration);
    }

    public static void UseImportedRenderGraphBuffer(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphBufferKey key,
        in RenderGraphBufferDescriptor descriptor)
    {
        hooks.UseEffect(
            new BufferDependencies(registry, key, descriptor),
            static (in BufferDependencies dependencies) =>
                dependencies.Registry.RegisterImportedBuffer(
                    dependencies.Key,
                    dependencies.Descriptor),
            DisposeRegistration);
    }

    public static void UseRenderGraphBufferExport(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphBufferKey key,
        RenderGraphBufferUsage usage = RenderGraphBufferUsage.None)
    {
        hooks.UseEffect(
            new BufferExportDependencies(registry, key, usage),
            static (in BufferExportDependencies dependencies) =>
                dependencies.Registry.RegisterBufferExport(
                    dependencies.Key,
                    dependencies.Usage),
            DisposeRegistration);
    }

    public static void UseImportedRenderGraphBufferBinding(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphBufferKey key,
        WgpuHandle<WGPUBuffer> buffer)
    {
        hooks.UseEffect(
            new BufferBindingDependencies(registry, key, buffer),
            static (in BufferBindingDependencies dependencies) =>
                dependencies.Registry.BindImportedBuffer(
                    dependencies.Key,
                    dependencies.Buffer),
            DisposeRegistration);
    }

    private static void DisposeRegistration(
        in RenderGraphRegistration registration) => registration.Dispose();

    private readonly record struct BufferDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphBufferKey Key,
        RenderGraphBufferDescriptor Descriptor);

    private readonly record struct BufferExportDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphBufferKey Key,
        RenderGraphBufferUsage Usage);

    private readonly record struct BufferBindingDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphBufferKey Key,
        WgpuHandle<WGPUBuffer> Buffer);
}
