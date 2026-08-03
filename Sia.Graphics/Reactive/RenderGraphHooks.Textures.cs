using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public static partial class RenderGraphHooks
{
    public static void UseRenderGraphTexture(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphTextureKey key,
        in RenderGraphTextureDescriptor descriptor)
    {
        hooks.UseEffect(
            new TextureDependencies(registry, key, descriptor),
            static (in TextureDependencies dependencies) =>
                dependencies.Registry.RegisterTexture(
                    dependencies.Key,
                    dependencies.Descriptor),
            DisposeRegistration);
    }

    public static void UseImportedRenderGraphTexture(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphTextureKey key,
        in RenderGraphTextureDescriptor descriptor)
    {
        hooks.UseEffect(
            new TextureDependencies(registry, key, descriptor),
            static (in TextureDependencies dependencies) =>
                dependencies.Registry.RegisterImportedTexture(
                    dependencies.Key,
                    dependencies.Descriptor),
            DisposeRegistration);
    }

    public static void UseRenderGraphTextureExport(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphTextureKey key,
        RenderGraphTextureUsage usage = RenderGraphTextureUsage.None)
    {
        hooks.UseEffect(
            new TextureExportDependencies(registry, key, usage),
            static (in TextureExportDependencies dependencies) =>
                dependencies.Registry.RegisterTextureExport(
                    dependencies.Key,
                    dependencies.Usage),
            DisposeRegistration);
    }

    public static void UseImportedRenderGraphTextureBinding(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphTextureKey key,
        WgpuHandle<WGPUTexture> texture)
    {
        hooks.UseEffect(
            new TextureBindingDependencies(registry, key, texture),
            static (in TextureBindingDependencies dependencies) =>
                dependencies.Registry.BindImportedTexture(
                    dependencies.Key,
                    dependencies.Texture),
            DisposeRegistration);
    }

    private readonly record struct TextureDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphTextureKey Key,
        RenderGraphTextureDescriptor Descriptor);

    private readonly record struct TextureExportDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphTextureKey Key,
        RenderGraphTextureUsage Usage);

    private readonly record struct TextureBindingDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphTextureKey Key,
        WgpuHandle<WGPUTexture> Texture);
}
