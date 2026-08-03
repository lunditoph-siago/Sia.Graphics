using Sia.Reactive;

namespace Sia.Graphics.Reactive;

public static partial class RenderGraphHooks
{
    public static void UseRenderGraphPass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphPassKey key,
        string name,
        RenderGraphPassDeclaration declaration)
    {
        hooks.UseEffect(
            new PassDependencies(registry, key, name, declaration),
            static (in PassDependencies dependencies) =>
                dependencies.Registry.RegisterPass(
                    dependencies.Key,
                    dependencies.Name,
                    dependencies.Declaration),
            DisposeRegistration);
    }

    public static void UseWgpuRenderGraphPassHandler(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphPassKey key,
        WgpuReactiveRenderGraphPassHandler handler)
    {
        hooks.UseEffect(
            new PassHandlerDependencies(registry, key, handler),
            static (in PassHandlerDependencies dependencies) =>
                dependencies.Registry.BindPassHandler(
                    dependencies.Key,
                    dependencies.Handler),
            DisposeRegistration);
    }

    private readonly record struct PassDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphPassKey Key,
        string Name,
        RenderGraphPassDeclaration Declaration);

    private readonly record struct PassHandlerDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphPassKey Key,
        WgpuReactiveRenderGraphPassHandler Handler);
}
