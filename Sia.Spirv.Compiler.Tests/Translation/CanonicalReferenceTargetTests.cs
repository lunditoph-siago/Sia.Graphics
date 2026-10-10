using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalReferenceTargetTests
{
    [Theory]
    [InlineData(false, 0u, 7u)] [InlineData(false, 5u, 18u)]
    [InlineData(true, 0u, 7u)] [InlineData(true, 5u, 18u)]
    public void BranchStorageKeepsItsResetAndQualifiedAccesses(bool qualified, uint inputValue, uint expected)
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;@compute @workgroup_size(1) fn main(){}");
        var source = new ControlFlowFunction(module.Functions.Single());
        var entry = source.Block(); source.Entry = entry.Id;
        var left = source.Block(); var right = source.Block(); var merge = source.Block();
        SsaValue Add(ControlFlowBlock block, ShaderType type, ValueOperation operation) {
            var value = source.Value(type); block.Instructions.Add(new(value, operation)); return value;
        }
        SsaValue Root(GlobalVariable global) => Add(entry, new ShaderType.Pointer(global.Type, global.Space, global.Access), new ValueOperation.Symbol(global.Name));
        var input = Root(module.Globals[0]); var output = Root(module.Globals[1]);
        var zero = Add(entry, ShaderType.U32, new ValueOperation.Literal(0u));
        var sample = Add(entry, ShaderType.U32, new ValueOperation.Load(Add(entry,
            new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read), new ValueOperation.Access(input, zero))));
        var condition = Add(entry, ShaderType.Bool, new ValueOperation.Binary("==", sample, zero));
        var pointerType = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function);
        var a = Add(left, pointerType, new ValueOperation.Local("a"));
        var b = Add(right, pointerType, new ValueOperation.Local("b"));
        right.Instructions.Add(new(null, new ValueOperation.Store(b, Add(right, ShaderType.U32, new ValueOperation.Literal(11u)))));
        var pointer = source.Value(pointerType); merge.Parameters.Add(pointer);
        entry.Terminator = new ControlFlowTerminator.Conditional(condition, new(left.Id), new(right.Id));
        left.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [a]));
        right.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [b]));
        source.SelectionMerges.Add(entry.Id, merge.Id);
        SpirvMemoryAccess? memory = qualified ? new(3, Alignment: 4) : null;
        var span = new SourceSpan(10, 4); DiagnosticFilter[] filters = [new(DiagnosticSeverity.Off, "derivative_uniformity")];
        var loaded = source.Value(ShaderType.U32);
        merge.Instructions.Add(new(loaded, new ValueOperation.Load(pointer, memory), span) { DiagnosticFilters = filters });
        var changed = Add(merge, ShaderType.U32, new ValueOperation.Binary("+", loaded,
            Add(merge, ShaderType.U32, new ValueOperation.Literal(7u))));
        merge.Instructions.Add(new(null, new ValueOperation.Store(pointer, changed, memory), span) { DiagnosticFilters = filters });
        var result = Add(merge, ShaderType.U32, new ValueOperation.Load(pointer));
        var destination = Add(merge, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage), new ValueOperation.Access(output, zero));
        merge.Instructions.Add(new(null, new ValueOperation.Store(destination, result)));
        merge.Terminator = new ControlFlowTerminator.Return();
        ControlFlowVerifier.Validate(source, module); string before = ControlFlowPrinter.Write(source);
        Assert.Contains("prior target legalization", Assert.Throws<ShaderException>(() => StructuredControlFlowLowering.Run(source, module)).Message);
        var target = CanonicalReferenceLowering.Run(source);
        ControlFlowVerifier.Validate(target, module);
        Assert.Equal(before, ControlFlowPrinter.Write(source));
        var allocations = target.Blocks.SelectMany(block => block.Instructions.Where(i => i.Operation is ValueOperation.Local).Select(i => (block, i))).ToArray();
        Assert.Equal(2, allocations.Length);
        Assert.All(allocations, p => { Assert.Equal(target.Entry, p.block.Id); Assert.False(((ValueOperation.Local)p.i.Operation).ZeroInitialize); });
        var accesses = target.Blocks.SelectMany(b => b.Instructions).Where(i => i.Span == span).ToArray();
        Assert.Equal(4, accesses.Length);
        Assert.All(accesses, i => {
            Assert.Equal(filters, i.DiagnosticFilters);
            Assert.Equal(memory, i.Operation switch { ValueOperation.Load load => load.MemoryAccess, ValueOperation.Store store => store.MemoryAccess, _ => throw new InvalidOperationException() });
        });
        module.Functions[0] = StructuredControlFlowLowering.Run(target, module);
        Assert.Equal(new uint[] { expected, 0 }, new CanonicalExecution(module, [inputValue]).Run().Output);
        var native = SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(new uint[] { expected, 0 }, new CanonicalExecution(native, [inputValue]).Run().Output);
        if (qualified) Assert.Contains("volatile memory access", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
        else Assert.Equal(new uint[] { expected, 0 }, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [inputValue]).Run().Output);
    }

    [Theory]
    [InlineData(false, false, 0u, 8u, 10u)]
    [InlineData(false, false, 5u, 6u, 19u)]
    [InlineData(true, false, 0u, 9u, 10u)]
    [InlineData(true, false, 5u, 7u, 19u)]
    [InlineData(false, true, 0u, 16u, 10u)]
    [InlineData(false, true, 5u, 14u, 19u)]
    public void PointerJoinsAndMemoryDispatchBelongToTheTargetGraph(bool loop, bool repeat, uint input, uint first, uint second)
    {
        var module = CanonicalPointerReturnTests.Create(nested: true, loop: loop, repeat: repeat);
        var source = CanonicalHelperInliner.RunReferences(CanonicalShaderPipeline.Prepare(module));
        Assert.Empty(source.DeferredFunctions);
        Assert.Contains(source.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Parameters), p => p.Type is ShaderType.Pointer);
        var before = source.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        foreach (var function in source.Declarations.Functions)
            function.Body = new Block { Statements = { new Statement.Evaluate(new Expression.Reference("obsolete", ShaderType.U32)) } };
        var target = CanonicalReferenceLowering.Run(source);
        Assert.DoesNotContain(target.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Parameters),
            p => p.Type is ShaderType.Pointer || CanonicalTypes.Resource(p.Type));
        ModuleValidator.Validate(target, native: true);
        var structured = StructuredControlFlowLowering.Run(target);
        uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(structured, [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(structured, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(structured, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.All(source.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }
}
