using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalPointerMergeTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;@compute @workgroup_size(1) fn main(){}";
    private static readonly ShaderType.Pointer FunctionPointer = new(ShaderType.U32, AddressSpace.Function);
    private static readonly ShaderType.Pointer StoragePointer = new(ShaderType.U32, AddressSpace.Storage);
    private static SsaValue Add(ControlFlowFunction graph, ControlFlowBlock block, ShaderType type, ValueOperation operation)
    {
        var result = graph.Value(type); block.Instructions.Add(new(result, operation)); return result;
    }
    private static SsaValue Root(ControlFlowFunction graph, ControlFlowBlock block, GlobalVariable global)
        => Add(graph, block, new ShaderType.Pointer(global.Type, global.Space, global.Access), new ValueOperation.Symbol(global.Name));
    private static Module Lower(Module module, ControlFlowFunction graph)
    {
        ControlFlowVerifier.Validate(graph, module); string before = ControlFlowPrinter.Write(graph);
        var lowered = StructuredControlFlowLowering.Run(graph, module);
        module.Functions[module.Functions.FindIndex(f => f.Name == graph.Signature.Name)] = lowered;
        var output = new PointerSelectionLowering(HelperInliner.RunPointers(module)).Run(); ModuleValidator.Validate(output);
        Assert.Equal(before, ControlFlowPrinter.Write(graph)); return output;
    }
    internal static Module Selection(bool function)
    {
        var module = WgslReader.Parse(Resources); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id; var left = graph.Block(); var right = graph.Block(); var merge = graph.Block();
        var input = Root(graph, entry, module.Globals[0]); var output = Root(graph, entry, module.Globals[1]);
        var zero = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(0u)); var one = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(1u));
        var source = Add(graph, entry, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read), new ValueOperation.Access(input, zero));
        var sample = Add(graph, entry, ShaderType.U32, new ValueOperation.Load(source));
        var index = Add(graph, entry, ShaderType.U32, new ValueOperation.Binary("&", sample, one));
        var condition = Add(graph, entry, ShaderType.Bool, new ValueOperation.Binary("==", index, zero));
        var out0 = Add(graph, entry, StoragePointer, new ValueOperation.Access(output, zero)); var out1 = Add(graph, entry, StoragePointer, new ValueOperation.Access(output, one));
        var a = function ? Add(graph, entry, FunctionPointer, new ValueOperation.Local("a")) : out0;
        var b = function ? Add(graph, entry, FunctionPointer, new ValueOperation.Local("b")) : out1;
        var ten = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(10u)); var seven = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(7u));
        entry.Instructions.Add(new(null, new ValueOperation.Store(a, sample))); entry.Instructions.Add(new(null, new ValueOperation.Store(b, ten)));
        var pointer = graph.Value(function ? FunctionPointer : StoragePointer); merge.Parameters.Add(pointer);
        entry.Terminator = new ControlFlowTerminator.Conditional(condition, new(left.Id), new(right.Id));
        left.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [a])); right.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [b]));
        var old = Add(graph, merge, ShaderType.U32, new ValueOperation.Load(pointer)); var changed = Add(graph, merge, ShaderType.U32, new ValueOperation.Binary("+", old, seven));
        merge.Instructions.Add(new(null, new ValueOperation.Store(pointer, changed)));
        if (function) {
            var result0 = Add(graph, merge, ShaderType.U32, new ValueOperation.Load(a)); var result1 = Add(graph, merge, ShaderType.U32, new ValueOperation.Load(b));
            merge.Instructions.Add(new(null, new ValueOperation.Store(out0, result0))); merge.Instructions.Add(new(null, new ValueOperation.Store(out1, result1)));
        }
        merge.Terminator = new ControlFlowTerminator.Return(); graph.SelectionMerges.Add(entry.Id, merge.Id);
        return Lower(module, graph);
    }
    internal static Module SwapLoop(bool reverse = false)
    {
        var module = WgslReader.Parse(Resources); var graph = new ControlFlowFunction(module.Functions.Single());
        var entry = graph.Block(); graph.Entry = entry.Id; var header = graph.Block(); var body = graph.Block(); var continuing = graph.Block(); var merge = graph.Block();
        var input = Root(graph, entry, module.Globals[0]); var output = Root(graph, entry, module.Globals[1]);
        var zero = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(0u)); var one = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(1u));
        var ten = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(10u));
        var source = Add(graph, entry, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read), new ValueOperation.Access(input, zero));
        var sample = Add(graph, entry, ShaderType.U32, new ValueOperation.Load(source)); var count = Add(graph, entry, ShaderType.U32, new ValueOperation.Binary("+", sample, one));
        var a = Add(graph, entry, FunctionPointer, new ValueOperation.Local("a")); var b = Add(graph, entry, FunctionPointer, new ValueOperation.Local("b"));
        entry.Instructions.Add(new(null, new ValueOperation.Store(a, one))); entry.Instructions.Add(new(null, new ValueOperation.Store(b, ten)));
        var p = graph.Value(FunctionPointer); var q = graph.Value(FunctionPointer); var step = graph.Value(ShaderType.U32); header.Parameters.AddRange([p, q, step]);
        entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [a, b, zero]));
        var condition = Add(graph, header, ShaderType.Bool, new ValueOperation.Binary("<", step, count));
        if (reverse) condition = Add(graph, header, ShaderType.Bool, new ValueOperation.Unary("!", condition));
        header.Terminator = new ControlFlowTerminator.Conditional(condition, new(reverse ? merge.Id : body.Id), new(reverse ? body.Id : merge.Id));
        var old = Add(graph, body, ShaderType.U32, new ValueOperation.Load(p)); var value = Add(graph, body, ShaderType.U32, new ValueOperation.Binary("+", old, one));
        body.Instructions.Add(new(null, new ValueOperation.Store(p, value))); body.Terminator = new ControlFlowTerminator.Branch(new(continuing.Id));
        var next = Add(graph, continuing, ShaderType.U32, new ValueOperation.Binary("+", step, one)); continuing.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [q, p, next]));
        var out0 = Add(graph, merge, StoragePointer, new ValueOperation.Access(output, zero)); var out1 = Add(graph, merge, StoragePointer, new ValueOperation.Access(output, one));
        var result0 = Add(graph, merge, ShaderType.U32, new ValueOperation.Load(a)); var result1 = Add(graph, merge, ShaderType.U32, new ValueOperation.Load(b));
        merge.Instructions.Add(new(null, new ValueOperation.Store(out0, result0))); merge.Instructions.Add(new(null, new ValueOperation.Store(out1, result1)));
        merge.Terminator = new ControlFlowTerminator.Return(); graph.Loops.Add(header.Id, new(continuing.Id, merge.Id));
        return Lower(module, graph);
    }
    [Theory]
    [InlineData(false, 0u, 7u, 10u)] [InlineData(false, 5u, 5u, 17u)]
    [InlineData(true, 0u, 7u, 10u)] [InlineData(true, 5u, 5u, 17u)]
    public void SelectedAddressesHaveOneOrderedLoadAndStore(bool function, uint input, uint first, uint second)
        => Verify(Selection(function), input, [first, second]);
    [Theory]
    [InlineData(0u, 2u, 10u)] [InlineData(1u, 2u, 11u)] [InlineData(2u, 3u, 11u)] [InlineData(5u, 4u, 13u)]
    public void LoopPointerSwapsUseSimultaneousEdgeSnapshots(uint input, uint first, uint second)
        => Verify(SwapLoop(), input, [first, second]);
    [Theory]
    [InlineData(0u, 2u, 10u)] [InlineData(5u, 4u, 13u)]
    public void LoopHeaderAcceptExitIsEquivalentToRejectExit(uint input, uint first, uint second)
        => Verify(SwapLoop(reverse: true), input, [first, second]);
    private static void Verify(Module module, uint input, uint[] expected)
    {
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default); var binary = SpirvWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(wgsl), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(binary), [input]).Run().Output);
    }
    [Fact]
    public void PointerChoiceDoesNotSplitAConvergentHelpersUnconditionalBarrier()
    {
        var module = WgslReader.Parse("fn touch(p:ptr<function,u32>){workgroupBarrier();*p=7u;}@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32){}");
        var graph = new ControlFlowFunction(module.Functions.Single(f => f.Name == "main"));
        var entry = graph.Block(); graph.Entry = entry.Id; var left = graph.Block(); var right = graph.Block(); var merge = graph.Block();
        var lane = Add(graph, entry, ShaderType.U32, new ValueOperation.Symbol("lane")); var zero = Add(graph, entry, ShaderType.U32, new ValueOperation.Literal(0u));
        var condition = Add(graph, entry, ShaderType.Bool, new ValueOperation.Binary("==", lane, zero));
        var a = Add(graph, entry, FunctionPointer, new ValueOperation.Local("a")); var b = Add(graph, entry, FunctionPointer, new ValueOperation.Local("b"));
        var pointer = graph.Value(FunctionPointer); merge.Parameters.Add(pointer);
        entry.Terminator = new ControlFlowTerminator.Conditional(condition, new(left.Id), new(right.Id));
        left.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [a])); right.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [b]));
        merge.Instructions.Add(new(null, new ValueOperation.Call("touch", [pointer], new ShaderType.Void(), CalleeEffects: ShaderEffectAnalysis.Compute(module)["touch"])));
        merge.Terminator = new ControlFlowTerminator.Return(); graph.SelectionMerges.Add(entry.Id, merge.Id);
        var output = Lower(module, graph); Assert.Empty(UniformityAnalysis.Validate(output)); Assert.NotEmpty(WgslWriter.Write(output, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void SharedPointerDispatchPreservesBorrowedInputAndLexicalDiagnostics()
    {
        var input = WgslReader.Parse("@fragment fn main(@builtin(position) p:vec4f)->@location(0) f32{@diagnostic(off,derivative_uniformity){if p.x>0.0f{return dpdx(p.y);}}return 0.0f;}");
        string before = WgslWriter.Emit(input); var output = new PointerSelectionLowering(input).Run();
        Assert.Equal(before, WgslWriter.Emit(input)); Assert.NotSame(input.Functions[0], output.Functions[0]);
        Assert.Empty(UniformityAnalysis.Validate(output)); ModuleValidator.Validate(output);
        Assert.Equal(before, WgslWriter.Emit(output));
    }
}
