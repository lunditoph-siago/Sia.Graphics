using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeCanonicalPointerReturnTests
{
    internal static SpirvBinary Fixture(bool nested = false, bool select = false, bool repeat = false, bool qualified = false)
    {
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage);
        var module = new Module { WorkgroupInitializationRequired = false };
        var array = new ShaderType.Array(ShaderType.U32, null);
        module.Globals.Add(new("inputs", array, AddressSpace.Storage, StorageAccess.Read, new(0, 0)));
        module.Globals.Add(new("outputs", array, AddressSpace.Storage, Binding: new(0, 1)));
        Expression U(uint value) => Expression.U32(value);
        Expression Ref(string name, ShaderType type) => new Expression.Reference(name, type);
        Expression Deref(string name) => new Expression.Unary("*", Ref(name, pointer), pointer);
        Expression Address(Expression place) => new Expression.Unary("&", place, place.Type);
        Expression Cell(string name, uint index, StorageAccess access = StorageAccess.ReadWrite) => new Expression.Access(
            Ref(name, new ShaderType.Pointer(array, AddressSpace.Storage, access)), U(index), pointer with { Access = access });
        var memory = qualified ? new SpirvMemoryAccess(1) : null;
        var pick = new ShaderFunction("pick") { ReturnType = pointer };
        pick.Arguments.AddRange([new("p", pointer), new("q", pointer), new("condition", ShaderType.Bool)]);
        pick.Body.Statements.Add(new Statement.Store(Deref("p"), new Expression.Binary("+", new Expression.Load(Deref("p")) { MemoryAccess = memory }, U(1), ShaderType.U32)) { MemoryAccess = memory });
        if (select) pick.Body.Statements.Add(new Statement.Return(new Expression.Select(Ref("condition", ShaderType.Bool), Address(Deref("p")), Address(Deref("q")))));
        else {
            var yes = new Block(); yes.Statements.Add(new Statement.Return(Address(Deref("p"))));
            var no = new Block(); no.Statements.Add(new Statement.Return(Address(Deref("q"))));
            pick.Body.Statements.Add(new Statement.If(Ref("condition", ShaderType.Bool), yes, no));
        }
        module.Functions.Add(pick);
        if (nested) {
            var forward = new ShaderFunction("forward") { ReturnType = pointer };
            forward.Arguments.AddRange(pick.Arguments);
            forward.Body.Statements.Add(new Statement.Return(new Expression.Call("pick", [Ref("p", pointer), Ref("q", pointer), Ref("condition", ShaderType.Bool)], pointer)));
            module.Functions.Add(forward);
        }
        var main = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        main.Body.Statements.Add(new Statement.Store(Cell("outputs", 0), new Expression.Load(Cell("inputs", 0, StorageAccess.Read))));
        main.Body.Statements.Add(new Statement.Store(Cell("outputs", 1), U(10)));
        var condition = new Expression.Binary("!=", new Expression.Binary("&", new Expression.Load(Cell("inputs", 0, StorageAccess.Read)), U(1), ShaderType.U32), U(0), ShaderType.Bool);
        Expression Call(Expression flag) => new Expression.Call(nested ? "forward" : "pick", [Address(Cell("outputs", 0)), Address(Cell("outputs", 1)), flag], pointer);
        var body = new Block();
        body.Statements.Add(new Statement.Declare("chosen", pointer, Call(condition), false));
        body.Statements.Add(new Statement.Store(Deref("chosen"), new Expression.Binary("+", new Expression.Load(Deref("chosen")), U(7), ShaderType.U32)));
        if (repeat) {
            var counter = new Expression.Reference("counter", new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function));
            var exit = new Block(); exit.Statements.Add(new Statement.Break());
            body.Statements.Insert(0, new Statement.If(new Expression.Binary(">=", new Expression.Load(counter), U(2), ShaderType.Bool), exit, new()));
            body.Statements.Add(new Statement.Store(counter, new Expression.Binary("+", new Expression.Load(counter), U(1), ShaderType.U32)));
            main.Body.Statements.Add(new Statement.Declare("counter", ShaderType.U32, U(0)));
            main.Body.Statements.Add(new Statement.Loop(body, new()));
            // The final address must agree with the stable input predicate.
            main.Body.Statements.Add(new Statement.Declare("chosen", pointer, new Expression.Select(condition, Address(Cell("outputs", 0)), Address(Cell("outputs", 1))), false));
        }
        else main.Body.Statements.AddRange(body.Statements);
        main.Body.Statements.Add(new Statement.Declare("first", ShaderType.U32, new Expression.Load(Deref("chosen")), false));
        main.Body.Statements.Add(new Statement.Declare("second", ShaderType.U32, new Expression.Load(Deref("chosen")), false));
        main.Body.Statements.Add(new Statement.Store(Cell("outputs", 1), new Expression.Binary("+", Ref("first", ShaderType.U32), Ref("second", ShaderType.U32), ShaderType.U32)));
        // Even an unused native pointer result must retain this increment.
        main.Body.Statements.Add(new Statement.Evaluate(Call(Expression.Bool(true))));
        module.Functions.Add(main); ModuleValidator.ValidateNative(module);
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module)); var instructions = binary.Instructions.ToList();
        instructions.Insert(1, new((ushort)Op.Capability, [4441]));
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = instructions };
    }

    [Theory]
    [InlineData(false, false, false, 0u, 2u, 34u)] [InlineData(false, false, false, 5u, 14u, 26u)]
    [InlineData(true, false, false, 0u, 2u, 34u)] [InlineData(true, false, false, 5u, 14u, 26u)]
    [InlineData(true, true, false, 0u, 2u, 34u)] [InlineData(true, true, false, 5u, 14u, 26u)]
    [InlineData(true, false, true, 0u, 3u, 48u)] [InlineData(true, false, true, 5u, 22u, 42u)]
    public void NativeCallsRetainEffectsOnceForMultipleUsesAndUnusedResults(bool nested, bool select, bool repeat, uint input, uint first, uint second)
    {
        var binary = Fixture(nested, select, repeat); byte[] before = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        Assert.Contains(traces, t => t.Pass == "pointer-return-helper-expansion");
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.DoesNotContain(module.Functions, f => f.ReturnType is ShaderType.Pointer);
        Assert.Equal(before, binary.ToBytes()); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Theory] [InlineData("array")] [InlineData("workgroup")] [InlineData("cross")]
    public void ExistingNativeReturnsEnterTheSharedCfgBeforeAddressDispatch(string kind)
    {
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(PointerReturnTests.Fixture(kind, "select", false, true, true), traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "pointer-return-helper-expansion" && t.Before.Contains("ptr<", StringComparison.Ordinal));
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        ModuleValidator.Validate(module); Assert.NotEmpty(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Theory] [InlineData("scalar", false)] [InlineData("atomic", true)]
    public void NativeSlotHelpersMigrateWhileAtomicFamiliesKeepTheirDeferral(string kind, bool atomic)
    {
        var traces = new List<CanonicalPassTrace>();
        var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(PointerReturnTests.Fixture(kind, "select", !atomic, true, false), traces: traces, deferrals: deferrals);
        if (atomic) Assert.Contains(deferrals, d => d.Function == "<SPIR-V>" && d.Feature.Contains("atomic", StringComparison.Ordinal));
        else {
            Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
            Assert.Contains(traces, t => t.Pass == "native-slot-helper-expansion");
            Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        }
        ModuleValidator.Validate(module); Assert.NotEmpty(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void NativeQualifiedAccessesRetainTheirCountAfterHelperExpansion()
    {
        var module = SpirvReader.ReadBinary(Fixture(nested: true, qualified: true));
        var output = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(2, output.Instructions.Count(i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && (i.Operands[2] & 1) != 0));
        Assert.Equal(2, output.Instructions.Count(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && (i.Operands[3] & 1) != 0));
        var wgsl = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(MemoryDecorations.Volatile, wgsl.Globals.Single(g => g.Binding?.Binding == 1).MemoryDecorations);
        Assert.Equal(MemoryDecorations.None, wgsl.Globals.Single(g => g.Binding?.Binding == 0).MemoryDecorations);
    }

    [Fact]
    public void NativeValidationDoesNotRelaxTheLegacyPublicParameterGate()
    {
        var module = WgslReader.Parse("fn read(p:ptr<storage,u32,read>)->u32{return *p;}");
        ModuleValidator.ValidateNative(module);
        Assert.Contains("function or private", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module)).Message);
    }

    [Fact]
    public void NativePointerCallsRetainTheirOriginalWordOffsetInCanonicalTraces()
    {
        var binary = SpirvBinary.Parse(Fixture(nested: true).ToBytes());
        var call = binary.Instructions.First(i => (Op)i.Opcode == Op.FunctionCall);
        var traces = new List<CanonicalPassTrace>(); _ = SpirvReader.ReadBinary(binary, traces: traces);
        string span = "span=" + call.WordOffset * 4 + ":" + call.WordCount * 4;
        Assert.Contains(traces, t => t.Pass == "pointer-return-helper-expansion" && t.Before.Contains(span, StringComparison.Ordinal));
    }

    [Fact]
    public void NativeStorageBufferRestrictionIsCheckedBeforeCanonicalDispatchErasesRoots()
    {
        var binary = PointerReturnTests.Fixture("cross", "select", false, true, true);
        var instructions = binary.Instructions.Select(i => (Op)i.Opcode == Op.Capability && i.Operands is [4442]
            ? i with { Operands = [4441] } : i).ToList();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(new() { Version = binary.Version, Bound = binary.Bound, Instructions = instructions }));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains("one storage buffer structure", error.Message);
    }
}
