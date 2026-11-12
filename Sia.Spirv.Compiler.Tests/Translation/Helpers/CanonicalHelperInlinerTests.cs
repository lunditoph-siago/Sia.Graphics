using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Tests.Translation.Helpers;

public sealed class CanonicalHelperInlinerTests
{
    [Fact]
    public void UsesOwnedCalleeGraphAndLeavesBothInputsUntouched()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse("""
            fn increment(p: ptr<function, u32>) { *p = *p + 1u; }
            @compute @workgroup_size(1) fn main() {
                var value = 7u;
                increment(&value);
            }
            """));
        var caller = canonical.Functions["main"];
        var callee = canonical.Functions["increment"];
        var callerBefore = ControlFlowPrinter.Write(caller);
        var calleeBefore = ControlFlowPrinter.Write(callee);
        var originalStore = Assert.Single(Operations(caller).OfType<ValueOperation.Store>());
        var originalCall = Assert.Single(Operations(caller).OfType<ValueOperation.Call>());

        var expanded = CanonicalHelperInliner.Run(caller, canonical.Declarations,
            (ShaderFunction _, Module _, out ControlFlowFunction? _, out string? _,
                IReadOnlyDictionary<string, ShaderEffects> _) => throw new InvalidOperationException("Owned graph was ignored."),
            canonical.Functions, expandFunctions: new HashSet<string> { "increment" });

        Assert.Equal(callerBefore, ControlFlowPrinter.Write(caller));
        Assert.Equal(calleeBefore, ControlFlowPrinter.Write(callee));
        Assert.Empty(Operations(expanded).OfType<ValueOperation.Call>());
        var inlinedStore = Assert.Single(Operations(expanded).OfType<ValueOperation.Store>(), store => store != originalStore);
        Assert.Equal(originalCall.Arguments[0], inlinedStore.Pointer);
    }

    [Fact]
    public void RejectsReturningCalleeLocalStorage()
    {
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function);
        var function = new ShaderFunction("dangling") { ReturnType = pointer };
        var module = new Module();
        module.Functions.Add(function);
        var graph = new ControlFlowFunction(function);
        var entry = graph.Block();
        graph.Entry = entry.Id;
        var local = graph.Value(pointer);
        entry.Instructions.Add(new(local, new ValueOperation.Local("value", true)));
        entry.Terminator = new ControlFlowTerminator.Return(local);

        var error = Assert.Throws<ShaderException>(() =>
            CanonicalHelperInliner.Run(graph, module, StructuredControlFlowReader.ReadHelperGraph));

        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Equal("Canonical helper in dangling: callee-local returned address requires lifetime legalization",
            error.Diagnostic.Message);
    }

    private static IEnumerable<ValueOperation> Operations(ControlFlowFunction graph)
        => graph.Blocks.SelectMany(block => block.Instructions).Select(instruction => instruction.Operation);
}
