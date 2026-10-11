using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeCanonicalComparisonTests
{
    internal static SpirvBinary InHelpers(SpirvBinary binary)
    {
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        var types = code.Where(i => (Op)i.Opcode == Op.TypeFunction).ToList();
        var functions = new List<SpirvInstruction>();
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        foreach (var comparison in code.Where(i => (Op)i.Opcode is Op.PtrEqual or Op.PtrNotEqual or Op.PtrDiff).ToArray()) {
            var a = comparison.Operands;
            uint pointer = code.First(i => i.Operands.Length >= 2 && i.Operands[1] == a[2]
                && (Op)i.Opcode is Op.AccessChain or Op.FunctionCall or Op.Load or Op.Select or Op.Phi or Op.CopyObject).Operands[0];
            var signature = types.FirstOrDefault(i => i.Operands is [_, var r, var p, var q] && r == a[0] && p == pointer && q == pointer);
            if (signature is null) { signature = I(Op.TypeFunction, next++, a[0], pointer, pointer); types.Add(signature); }
            uint function = next++, left = next++, right = next++, label = next++, result = next++;
            functions.AddRange([I(Op.Function, a[0], function, 0, signature.Operands[0]), I(Op.FunctionParameter, pointer, left),
                I(Op.FunctionParameter, pointer, right), I(Op.Label, label), I((Op)comparison.Opcode, a[0], result, left, right),
                I(Op.ReturnValue, result), I(Op.FunctionEnd)]);
            code[code.IndexOf(comparison)] = I(Op.FunctionCall, a[0], a[1], function, a[2], a[3]);
        }
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), types.Where(t => !code.Contains(t)));
        code.AddRange(functions);
        return new() { Version = binary.Version, Bound = next, Instructions = code };
    }

    internal static SpirvBinary Fixture(string mode)
    {
        var binary = mode == "select" || mode == "slot" ? NativeCanonicalPointerReturnTests.Fixture(nested: true, select: true)
            : NativeCanonicalPointerPhiTests.Fixture(loop: mode == "loop", iterations: 1);
        if (mode == "slot") binary = PointerMemoryTests.StorePointers(binary, "select", privateSlot: true);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        var addresses = code.Where(i => (Op)i.Opcode == Op.AccessChain).Select(i => i.Operands[1]).ToHashSet();
        var call = code.First(i => (Op)i.Opcode == Op.FunctionCall && i.Operands.Length == 6 && addresses.Contains(i.Operands[3])).Operands;
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint u32 = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint Constant(uint value) {
            var existing = code.FirstOrDefault(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == u32 && v == value);
            if (existing is not null) return existing.Operands[1];
            uint id = next++; code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), I(Op.Constant, u32, id, value)); return id;
        }
        uint zero=Constant(0), one=Constant(1), two=Constant(2);
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeInt), I(Op.Decorate, call[0], 6, 4));
        int position=code.FindIndex(i => (Op)i.Opcode == Op.FunctionCall && i.Operands[1] == call[1]);
        position=code.FindIndex(position, i => (Op)i.Opcode == Op.Return);
        uint equal=next++, unequal=next++, a=next++, b=next++, encoded=next++, difference=next++;
        code.InsertRange(position, [I(Op.PtrEqual, boolean, equal, call[1], call[4]), I(Op.PtrNotEqual, boolean, unequal, call[1], call[4]),
            I(Op.Select, u32, a, equal, one, zero), I(Op.Select, u32, b, unequal, two, zero), I(Op.IAdd, u32, encoded, a, b),
            I(Op.PtrDiff, u32, difference, call[1], call[4]), I(Op.Store, call[3], encoded), I(Op.Store, call[4], difference)]);
        var globals=code.Where(i => (Op)i.Opcode == Op.Variable && i.Operands[2] != 7).Select(i => i.Operands[1]).ToArray();
        code=code.Select(i => {
            if ((Op)i.Opcode != Op.EntryPoint) return i;
            _ = SpirvBinary.ReadString(i.Operands.AsSpan(2), out int words);
            var listed=i.Operands[(2+words)..].ToHashSet(); return i with { Operands=[..i.Operands,..globals.Where(listed.Add)] };
        }).ToList();
        return new() { Version=0x10400, Bound=next, Instructions=code };
    }

    [Theory]
    [InlineData("select",0u,1u,0u)] [InlineData("select",5u,2u,uint.MaxValue)]
    [InlineData("phi",0u,1u,0u)] [InlineData("phi",5u,2u,uint.MaxValue)]
    [InlineData("loop",0u,2u,uint.MaxValue)] [InlineData("loop",5u,1u,0u)]
    [InlineData("slot",0u,1u,0u)] [InlineData("slot",5u,2u,uint.MaxValue)]
    public void NativeComparisonsUseCapturedCanonicalCoordinates(string mode,uint input,uint equality,uint difference)
    {
        var binary=Fixture(mode); byte[] original=binary.ToBytes();
        var traces=new List<CanonicalPassTrace>(); var deferrals=new List<CanonicalDeferral>();
        var module=SpirvReader.ReadBinary(binary,traces:traces,deferrals:deferrals);
        Assert.Contains(traces,t => t.Pass=="native-cfg-import");
        Assert.DoesNotContain(deferrals,d => d.Function=="<SPIR-V>"); Assert.Equal(original,binary.ToBytes());
        uint[] expected=[equality,difference];
        Assert.Equal(expected,new CanonicalExecution(module,[input]).Run().Output);
        Assert.Equal(expected,new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module,SpirvCompilationTarget.Default)),[input]).Run().Output);
        Assert.Equal(expected,new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module,SpirvCompilationTarget.Default)),[input]).Run().Output);
    }

    [Theory] [InlineData(0u, 1u, 0u)] [InlineData(5u, 2u, uint.MaxValue)]
    public void ComparisonHelpersBindActualAddresses(uint input, uint equality, uint difference)
    {
        var binary = InHelpers(Fixture("select")); var original = binary.ToBytes();
        var module = SpirvReader.ReadBinary(binary);
        Assert.Equal(original, binary.ToBytes());
        uint[] expected = [equality, difference];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Fact]
    public void ComparisonHelpersDiagnosePotentialAliasesAfterArgumentBinding()
    {
        var binary = InHelpers(PointerComparisonTests.CrossBindingDifferenceFixture());
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(binary));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains("potentially aliased", error.Message);
    }

    [Theory] [InlineData("==", 0u)] [InlineData("!=", 1u)] [InlineData("-", uint.MaxValue)]
    public void OwnedComparisonsRetainSymbolIdentityAndDiagnosticMetadata(string operation, uint expected)
    {
        var module = WgslReader.Parse("@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;@compute @workgroup_size(1) fn main(){}");
        var graph = new ControlFlowFunction(module.Functions.Single()); var block = graph.Block(); graph.Entry = block.Id;
        SsaValue Add(ShaderType type, ValueOperation op) { var value = graph.Value(type); block.Instructions.Add(new(value, op)); return value; }
        var global = module.Globals.Single(); var rootType = new ShaderType.Pointer(global.Type, global.Space, global.Access);
        var first = Add(rootType, new ValueOperation.Symbol(global.Name)); var second = Add(rootType, new ValueOperation.Symbol(global.Name));
        var zero = Add(ShaderType.U32, new ValueOperation.Literal(0u)); var one = Add(ShaderType.I32, new ValueOperation.Literal(1));
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage);
        var left = Add(pointer, new ValueOperation.Access(first, zero)); var right = Add(pointer, new ValueOperation.Access(second, one));
        var span = new SourceSpan(40, 8); DiagnosticFilter[] filters = [new(DiagnosticSeverity.Off, "derivative_uniformity")];
        var result = graph.Value(operation == "-" ? ShaderType.U32 : ShaderType.Bool);
        block.Instructions.Add(new(result, new ValueOperation.Binary(operation, left, right), span) { DiagnosticFilters = filters });
        if (operation != "-") result = Add(ShaderType.U32, new ValueOperation.Select(result, Add(ShaderType.U32, new ValueOperation.Literal(1u)), zero));
        block.Instructions.Add(new(null, new ValueOperation.Store(left, result))); block.Terminator = new ControlFlowTerminator.Return();
        var source = new CanonicalModule(module, new Dictionary<string, ControlFlowFunction> { [graph.Signature.Name] = graph },
            new Dictionary<string, string>(), new HashSet<string> { graph.Signature.Name });
        ModuleValidator.Validate(source, native: true); string original = ControlFlowPrinter.Write(graph);
        module.Functions.Single().Body = new Block { Statements = { new Statement.Evaluate(new Expression.Reference("obsolete", ShaderType.U32)) } };
        var target = CanonicalReferenceLowering.Run(source);
        Assert.Equal(original, ControlFlowPrinter.Write(graph)); ModuleValidator.Validate(target, native: true);
        var lowered = target.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions).Where(i => i.Span == span).ToArray();
        Assert.NotEmpty(lowered); Assert.All(lowered, i => Assert.Equal(filters, i.DiagnosticFilters));
        Assert.DoesNotContain(target.Functions.Values, PointerAliasAnalysis.HasComparisons);
        var structured = StructuredControlFlowLowering.Run(target);
        Assert.Equal(new uint[] { expected, 0 }, new CanonicalExecution(structured, []).Run().Output);
        Assert.Equal(new uint[] { expected, 0 }, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(structured, SpirvCompilationTarget.Default)), []).Run().Output);
        Assert.Equal(new uint[] { expected, 0 }, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(structured, SpirvCompilationTarget.Default)), []).Run().Output);
    }
}
