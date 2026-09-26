using Sia.Reactive;
using Sia.RenderGraph;

namespace Sia.Graphics.Reactive;

public static partial class RenderGraphHooks
{
    public static void UseRenderGraphPass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphPassKey key,
        string name,
        RenderGraphPassDeclaration declaration,
        RenderGraphPassKind kind = RenderGraphPassKind.Render)
    {
        var order = UsePassOrder(ref hooks, registry);
        hooks.UseEffect(
            new PassDependencies(registry, key, name, declaration, kind, order),
            static (in PassDependencies dependencies) =>
                dependencies.Registry.RegisterPass(
                    dependencies.Key,
                    dependencies.Name,
                    dependencies.Declaration,
                    dependencies.Kind,
                    dependencies.Order),
            DisposeRegistration);
    }

    public static void UseRenderGraphPass<TDependencies>(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphPassKey key,
        string name,
        in TDependencies dependencies,
        RenderGraphPassDeclaration<TDependencies> declaration,
        RenderGraphPassKind kind = RenderGraphPassKind.Render)
        where TDependencies : struct, IEquatable<TDependencies>
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var order = UsePassOrder(ref hooks, registry);
        hooks.UseEffect(
            new PassDependencies<TDependencies>(registry, key, name, dependencies, declaration, kind, order),
            static (in PassDependencies<TDependencies> value) => {
                var captured = value;
                return captured.Registry.RegisterPass(captured.Key, captured.Name,
                    pass => captured.Declaration(captured.Dependencies, pass), captured.Kind, captured.Order);
            },
            DisposeRegistration);
    }

    private static long UsePassOrder(ref Hooks hooks, WgpuRenderGraphRegistry registry)
    {
        var order = hooks.UseRef(static () => new PassOrder()).Value;
        if (!ReferenceEquals(order.Registry, registry)) {
            order.Registry = registry;
            order.Value = registry.ReservePassOrder();
        }
        return order.Value;
    }

    public static void UseComputeRenderGraphPass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphPassKey key,
        string name,
        RenderGraphPassDeclaration declaration) =>
        hooks.UseRenderGraphPass(registry, key, name, declaration, RenderGraphPassKind.Compute);

    public static void UseComputeRenderGraphPass<TDependencies>(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        RenderGraphPassKey key,
        string name,
        in TDependencies dependencies,
        RenderGraphPassDeclaration<TDependencies> declaration)
        where TDependencies : struct, IEquatable<TDependencies> =>
        hooks.UseRenderGraphPass(registry, key, name, in dependencies, declaration, RenderGraphPassKind.Compute);

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
        RenderGraphPassDeclaration Declaration,
        RenderGraphPassKind Kind,
        long Order);

    private readonly record struct PassDependencies<TDependencies>(
        WgpuRenderGraphRegistry Registry,
        RenderGraphPassKey Key,
        string Name,
        TDependencies Dependencies,
        RenderGraphPassDeclaration<TDependencies> Declaration,
        RenderGraphPassKind Kind,
        long Order)
        where TDependencies : struct, IEquatable<TDependencies>;

    private sealed class PassOrder
    {
        public WgpuRenderGraphRegistry? Registry { get; set; }

        public long Value { get; set; }
    }

    private readonly record struct PassHandlerDependencies(
        WgpuRenderGraphRegistry Registry,
        RenderGraphPassKey Key,
        WgpuReactiveRenderGraphPassHandler Handler);
}
