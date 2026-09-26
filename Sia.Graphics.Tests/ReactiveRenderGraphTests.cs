using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Tests;

public class ReactiveRenderGraphTests
{
    [Fact]
    public void ReplacingSurfaceRetainsPreparedBindingsAndPlan()
    {
        using var registry = new WgpuRenderGraphRegistry();
        var surface = new RenderGraphTextureKey("surface");
        registry.RegisterImportedTexture(surface, new("surface", RenderGraphTextureFormat.RGBA8Unorm, 64, 64,
            usage: RenderGraphTextureUsage.RenderAttachment));
        registry.RegisterPass(new("draw"), "draw", p => p.Write(surface, RenderGraphTextureUsage.RenderAttachment));
        var binding = registry.BindImportedTexture(surface, new WgpuHandle<WGPUTexture>(1));
        var first = registry.PrepareBindings();
        binding.Dispose();
        using var next = registry.BindImportedTexture(surface, new WgpuHandle<WGPUTexture>(2));
        var second = registry.PrepareBindings();
        Assert.Same(first, second);
        Assert.Equal(1, registry.CompilationCount);
    }

    [Fact]
    public void RemovingLiveBindingFailsUntilRebound()
    {
        using var registry = new WgpuRenderGraphRegistry();
        var buffer = new RenderGraphBufferKey("buffer");
        registry.RegisterImportedBuffer(buffer, new("buffer", 16, RenderGraphBufferUsage.CopyDestination));
        registry.RegisterPass(new("copy"), "copy", p => p.Write(buffer, RenderGraphBufferUsage.CopyDestination));
        var binding = registry.BindImportedBuffer(buffer, new WgpuHandle<WGPUBuffer>(1));
        registry.PrepareBindings();
        binding.Dispose();
        Assert.Throws<InvalidOperationException>(() => registry.PrepareBindings());
        using var replacement = registry.BindImportedBuffer(buffer, new WgpuHandle<WGPUBuffer>(2));
        registry.PrepareBindings();
        binding.Dispose();
        registry.PrepareBindings();
        Assert.Equal(1, registry.CompilationCount);
    }

    [Fact]
    public void ExplicitDependenciesRecompileOnlyWhenAccessChangesAndUnmountCleanly()
    {
        using var world = new World();
        var registry = world.AcquireAddon<WgpuRenderGraphRegistry>();
        var first = new RenderGraphBufferKey("first");
        var second = new RenderGraphBufferKey("second");
        var mount = world.Mount(Declare, new Props(registry, first));
        var original = world.PrepareWgpuRenderGraph();
        for (var i = 0; i < 5; i++) {
            mount.Update(new Props(registry, first));
            Assert.Same(original, world.PrepareWgpuRenderGraph());
        }
        Assert.Equal(1, registry.CompilationCount);
        mount.Update(new Props(registry, second));
        var changed = world.PrepareWgpuRenderGraph();
        Assert.NotSame(original, changed);
        Assert.Equal(registry.GetBufferHandle(second), Assert.Single(Assert.Single(changed.Graph.Passes).Buffers).Buffer);
        Assert.Equal(2, registry.CompilationCount);
        mount.Unmount();
        Assert.Empty(world.PrepareWgpuRenderGraph().Graph.Passes);

        static ReactiveNode Declare(in Props props, ref Hooks hooks)
        {
            hooks.UseImportedRenderGraphBuffer(props.Registry, new("first"), new("first", 16, RenderGraphBufferUsage.CopyDestination));
            hooks.UseImportedRenderGraphBuffer(props.Registry, new("second"), new("second", 16, RenderGraphBufferUsage.CopyDestination));
            hooks.UseComputeRenderGraphPass(props.Registry, new("copy"), "copy", new Dependencies(props.Target),
                static (in Dependencies deps, RenderGraphPassDeclarationBuilder pass) =>
                    pass.Write(deps.Target, RenderGraphBufferUsage.CopyDestination));
            return global::Sia.Reactive.Reactive.None;
        }
    }

    [Fact]
    public void BindingErrorsCanBeRemovedWithoutPoisoningRetainedState()
    {
        using var registry = new WgpuRenderGraphRegistry();
        var initial = registry.PrepareBindings();
        var invalid = registry.BindImportedBuffer(new("missing"), new WgpuHandle<WGPUBuffer>(1));
        Assert.Throws<InvalidOperationException>(() => registry.PrepareBindings());
        invalid.Dispose();
        Assert.Same(initial, registry.PrepareBindings());
        var handler = registry.BindPassHandler(new("missing"), static _ => {});
        Assert.Throws<InvalidOperationException>(() => registry.PrepareBindings());
        handler.Dispose();
        registry.PrepareBindings();
    }

    [Fact]
    public void ResourceDescriptorChangeRebuildsPlanAndBindings()
    {
        using var registry = new WgpuRenderGraphRegistry();
        var key = new RenderGraphBufferKey("buffer");
        var declaration = registry.RegisterImportedBuffer(key, new("buffer", 16, RenderGraphBufferUsage.CopyDestination));
        registry.RegisterPass(new("copy"), "copy", p => p.Write(key, RenderGraphBufferUsage.CopyDestination));
        registry.BindImportedBuffer(key, new WgpuHandle<WGPUBuffer>(1));
        var initial = registry.PrepareBindings();
        declaration.Dispose();
        registry.RegisterImportedBuffer(key, new("buffer", 32, RenderGraphBufferUsage.CopyDestination));
        Assert.NotSame(initial, registry.PrepareBindings());
        Assert.Equal(32ul, registry.PreparePlan().Graph.GetBuffer(registry.GetBufferHandle(key)).Descriptor.Size);
    }

    private readonly record struct Props(WgpuRenderGraphRegistry Registry, RenderGraphBufferKey Target);
    private readonly record struct Dependencies(RenderGraphBufferKey Target);
}
