using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Front;

internal interface ICanonicalModuleSource
{
    Module Declarations { get; }
    bool UsesNativeValidation { get; }

    IReadOnlyDictionary<string, ControlFlowFunction>? NativeGraphs { get; }

    void ValidateDeclarations();

    CanonicalGraphImport ReadGraph(ShaderFunction function);

    void ValidateGraph(ControlFlowFunction graph);
}

internal readonly record struct CanonicalGraphImport(
    ControlFlowFunction? Graph,
    string? DeferredFeature = null,
    bool IsNative = false);

internal sealed class StructuredModuleSource(Module declarations) : ICanonicalModuleSource
{
    public Module Declarations { get; } = declarations;
    public bool UsesNativeValidation => false;
    public IReadOnlyDictionary<string, ControlFlowFunction>? NativeGraphs => null;

    public void ValidateDeclarations() => ModuleValidator.Validate(Declarations);

    public CanonicalGraphImport ReadGraph(ShaderFunction function) => ReadGraph(function, Declarations);

    public void ValidateGraph(ControlFlowFunction graph) { }

    internal static CanonicalGraphImport ReadGraph(ShaderFunction function, Module declarations)
        => StructuredControlFlowReader.TryRead(function, declarations, out var graph, out var deferred)
            ? new(graph)
            : new(null, deferred);
}

internal sealed class NativeModuleSource(
    Module declarations,
    IReadOnlyDictionary<string, ControlFlowFunction>? graphs,
    Action<ControlFlowFunction, Module> validateGraph) : ICanonicalModuleSource
{
    public Module Declarations { get; } = declarations;
    public bool UsesNativeValidation => true;
    public IReadOnlyDictionary<string, ControlFlowFunction>? NativeGraphs { get; } = graphs;

    public void ValidateDeclarations()
    {
        if (NativeGraphs is { } graphs) {
            ModuleValidator.ValidateNative(Declarations, graphs);
        }
        else {
            ModuleValidator.ValidateNative(Declarations);
        }
    }

    public CanonicalGraphImport ReadGraph(ShaderFunction function)
        => NativeGraphs?.TryGetValue(function.Name, out var graph) == true
            ? new(graph.Copy(), IsNative: true)
            : StructuredModuleSource.ReadGraph(function, Declarations);

    public void ValidateGraph(ControlFlowFunction graph) => validateGraph(graph, Declarations);
}
