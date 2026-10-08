using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using static Sia.Spirv.Compiler.Translation.Tests.SpirvTests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class AtomicMemoryTests
{
    internal static SpirvBinary AggregateFixture(int mode)
    {
        var instructions = new List<SpirvInstruction> {
            I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), Entry(40), I(Op.ExecutionMode, 40, 17, 1, 1, 1),
            I(Op.Decorate, 3, 6, 4), I(Op.Decorate, 4, 2), I(Op.MemberDecorate, 4, 0, 35, 0), I(Op.MemberDecorate, 4, 1, 35, 8),
            I(Op.Decorate, 20, 34, 0), I(Op.Decorate, 20, 33, 0), I(Op.Decorate, 21, 34, 0), I(Op.Decorate, 21, 33, 1),
            I(Op.TypeVoid, 1), I(Op.TypeInt, 2, 32, 0), I(Op.Constant, 2, 10, 0), I(Op.Constant, 2, 11, 1), I(Op.Constant, 2, 12, 2),
            I(Op.Constant, 2, 13, 9), I(Op.Constant, 2, 14, 7), I(Op.Constant, 2, 15, 99), I(Op.TypeArray, 3, 2, 12),
            I(Op.TypeStruct, 4, 3, 2), I(Op.TypePointer, 5, 12, 4), I(Op.TypePointer, 6, 12, 2), I(Op.TypeFunction, 7, 1),
            I(Op.ConstantComposite, 3, 16, 13, 14), I(Op.ConstantComposite, 4, 17, 16, 15),
            I(Op.Variable, 5, 20, 12), I(Op.Variable, 5, 21, 12), I(Op.Function, 1, 40, 0, 7), I(Op.Label, 41),
            I(Op.AccessChain, 6, 31, 20, 10, 10), I(Op.Store, mode == 3 ? 21u : 20u, 17), I(Op.AtomicIAdd, 2, 42, 31, 11, 10, 11) };
        if (mode == 2) instructions.Add(I(Op.CopyMemory, 20, 20));
        if (mode == 3) instructions.Add(I(Op.CopyMemory, 20, 21));
        if (mode == 1) instructions.Add(I(Op.CopyMemory, 21, 20));
        else instructions.AddRange([I(Op.Load, 4, 43, 20), I(Op.Store, 21, 43)]);
        instructions.AddRange([I(Op.Return), I(Op.FunctionEnd)]);
        return new() { Bound = 100, Instructions = instructions };
    }
    internal static SpirvBinary Fixture(Op operation, uint scope, uint semantics, uint unequal = 0, bool vulkan = false)
    {
        var instructions = new List<SpirvInstruction> { I(Op.Capability, 1) };
        if (vulkan)
        {
            instructions.Add(I(Op.Capability, 5345));
            if (scope == 1) instructions.Add(I(Op.Capability, 5346));
            instructions.Add(I(Op.Extension, SpirvBinary.StringWords("SPV_KHR_vulkan_memory_model")));
        }
        instructions.AddRange([
            I(Op.MemoryModel, 0, vulkan ? 3u : 1u), Entry(30), I(Op.ExecutionMode, 30, 17, 1, 1, 1),
            I(Op.Decorate, 3, 2), I(Op.MemberDecorate, 3, 0, 35, 0),
            I(Op.Decorate, 20, 34, 0), I(Op.Decorate, 20, 33, 0), I(Op.Decorate, 21, 34, 0), I(Op.Decorate, 21, 33, 1),
            I(Op.TypeVoid, 1), I(Op.TypeInt, 2, 32, 0), I(Op.TypeStruct, 3, 2), I(Op.TypePointer, 4, 12, 3),
            I(Op.TypePointer, 5, 12, 2), I(Op.TypeFunction, 6, 1),
            I(Op.Constant, 2, 7, 0), I(Op.Constant, 2, 8, 1), I(Op.Constant, 2, 9, scope),
            I(Op.Constant, 2, 10, semantics), I(Op.Constant, 2, 11, unequal), I(Op.Constant, 2, 12, 7), I(Op.Constant, 2, 13, 9),
            I(Op.Variable, 4, 20, 12), I(Op.Variable, 4, 21, 12), I(Op.Function, 1, 30, 0, 6), I(Op.Label, 31),
            I(Op.AccessChain, 5, 32, 20, 7), I(Op.AccessChain, 5, 33, 21, 7)]);
        instructions.Add(operation switch
        {
            Op.AtomicStore => I(operation, 32, 9, 10, 12),
            Op.AtomicLoad or Op.AtomicIIncrement or Op.AtomicIDecrement => I(operation, 2, 34, 32, 9, 10),
            Op.AtomicCompareExchange => I(operation, 2, 34, 32, 9, 10, 11, 12, 13),
            _ => I(operation, 2, 34, 32, 9, 10, 12)
        });
        instructions.AddRange([I(Op.Store, 33, operation == Op.AtomicStore ? 12u : 34u), I(Op.Return), I(Op.FunctionEnd)]);
        return new() { Bound = 100, Instructions = instructions };
    }

    [Theory]
    [InlineData(1u, false)] [InlineData(2u, false)] [InlineData(3u, false)] [InlineData(4u, false)]
    [InlineData(1u, true)] [InlineData(2u, true)] [InlineData(3u, true)] [InlineData(4u, true)] [InlineData(5u, true)]
    public void NativeScopeAndMemoryModelSurviveRoundtrip(uint scope, bool vulkan)
    {
        var module = SpirvReader.Parse(Fixture(Op.AtomicIAdd, scope, 0, vulkan: vulkan).ToBytes());
        Assert.Equal(vulkan, module.VulkanMemoryModel);
        Assert.Equal(new SpirvAtomicMemory(scope, 0), Assert.Single(Calls(module), c => c.Function == "atomicAdd").AtomicMemory);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double>());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(resolved));
        AssertAtomic(binary, Op.AtomicIAdd, scope, 0);
        Assert.Equal(vulkan ? 3u : 1u, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.MemoryModel).Operands[1]);
        Assert.Equal(vulkan && scope == 1, binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5346));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData(Op.AtomicLoad, 66u)] [InlineData(Op.AtomicStore, 68u)] [InlineData(Op.AtomicExchange, 72u)]
    [InlineData(Op.AtomicIAdd, 72u)] [InlineData(Op.AtomicISub, 72u)] [InlineData(Op.AtomicUMin, 72u)] [InlineData(Op.AtomicUMax, 72u)]
    [InlineData(Op.AtomicAnd, 72u)] [InlineData(Op.AtomicOr, 72u)] [InlineData(Op.AtomicXor, 72u)]
    [InlineData(Op.AtomicIIncrement, 72u)] [InlineData(Op.AtomicIDecrement, 72u)] [InlineData(Op.AtomicCompareExchange, 72u)]
    [InlineData(Op.AtomicIAdd, 80u)] [InlineData(Op.AtomicIAdd, 32840u)] [InlineData(Op.AtomicIAdd, 8260u)] [InlineData(Op.AtomicCompareExchange, 57416u)]
    public void NativeMemoryOrderingSurvivesWithoutRelaxation(object operationValue, uint semantics)
    {
        var operation = (Op)operationValue;
        bool vulkan = (semantics & (8192 | 16384 | 32768)) != 0;
        uint unequal = (semantics & 32768) | (operation == Op.AtomicCompareExchange ? (semantics & 16384) | 66u : 0u);
        var module = SpirvReader.Parse(Fixture(operation, 1, semantics, unequal, vulkan).ToBytes());
        ModuleValidator.Validate(module);
        Assert.Contains("no equivalent WGSL relaxed builtin", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
        var native = SpirvBinary.Parse(SpirvWriter.Write(module));
        Op emitted = operation == Op.AtomicIIncrement ? Op.AtomicIAdd : operation == Op.AtomicIDecrement ? Op.AtomicISub : operation;
        AssertAtomic(native, emitted, 1, semantics, operation == Op.AtomicCompareExchange ? unequal : null);
        var roundtrip = SpirvReader.Parse(native.ToBytes());
        Assert.Equal(Assert.Single(Calls(module), c => c.AtomicMemory is not null).AtomicMemory,
            Assert.Single(Calls(roundtrip), c => c.AtomicMemory is not null).AtomicMemory);
    }

    [Theory]
    [InlineData(Op.AtomicLoad, 4u, 0u, false)] [InlineData(Op.AtomicStore, 2u, 0u, false)]
    [InlineData(Op.AtomicIAdd, 6u, 0u, false)] [InlineData(Op.AtomicIAdd, 1u, 0u, false)]
    [InlineData(Op.AtomicIAdd, 16u, 0u, true)] [InlineData(Op.AtomicIAdd, 32768u, 0u, false)]
    [InlineData(Op.AtomicIAdd, 8192u, 0u, true)] [InlineData(Op.AtomicIAdd, 8258u, 0u, true)]
    [InlineData(Op.AtomicCompareExchange, 72u, 68u, false)] [InlineData(Op.AtomicCompareExchange, 68u, 66u, false)]
    [InlineData(Op.AtomicCompareExchange, 32840u, 66u, true)]
    public void InvalidNativeMemoryRequirementsAreRejected(object operationValue, uint semantics, uint unequal, bool vulkan)
    {
        var operation = (Op)operationValue;
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(SpirvReader.Parse(Fixture(operation, 1, semantics, unequal, vulkan).ToBytes())));
    }

    [Fact]
    public void InvocationOrderAndQueueFamilyModelAreChecked()
    {
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(SpirvReader.Parse(Fixture(Op.AtomicIAdd, 4, 66).ToBytes())));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(SpirvReader.Parse(Fixture(Op.AtomicIAdd, 5, 0).ToBytes())));
    }

    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)] [InlineData(5u)]
    public void RelaxedMemoryClassBitsCanUseWgslAddressSpaceScope(uint scope)
    {
        var module = SpirvReader.Parse(Fixture(Op.AtomicIAdd, scope, 256, vulkan: scope == 5).ToBytes());
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        AssertAtomic(SpirvBinary.Parse(SpirvWriter.Write(module)), Op.AtomicIAdd, scope, 256);
    }

    [Fact]
    public void CrossDeviceScopeCannotBeSilentlyNarrowedToWgsl()
    {
        var module = SpirvReader.Parse(Fixture(Op.AtomicIAdd, 0, 0).ToBytes());
        Assert.Contains("no equivalent WGSL builtin scope", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
    }

    [Theory]
    [InlineData(9u, 0, 9u, 7u, 1)] [InlineData(9u, 2, 9u, 7u, 3)] [InlineData(12u, 2, 12u, 12u, 1)]
    public void StrongCompareRetriesSpuriousFailureAndReturnsObservedValue(uint initial, int spurious, uint expectedOld, uint expectedMemory, int attempts)
    {
        var module = SpirvReader.Parse(Fixture(Op.AtomicCompareExchange, 1, 0).ToBytes());
        string wgsl = WgslWriter.Write(module); var lowered = WgslReader.Parse(wgsl); ModuleValidator.Validate(lowered);
        var helper = Assert.Single(lowered.Functions, f => f.Name.StartsWith("sia_atomic_", StringComparison.Ordinal));
        var invocation = Assert.Single(Calls(lowered), c => c.Function == helper.Name);
        var machine = new WeakMachine(helper, invocation, initial, spurious);
        Assert.Equal(expectedOld, machine.Run()); Assert.Equal(expectedMemory, machine.Memory); Assert.Equal(attempts, machine.Attempts);
        Assert.Equal("spirvAtomicCompareExchange", Assert.Single(Calls(module), c => c.AtomicMemory is not null).Function);
        AssertAtomic(SpirvBinary.Parse(SpirvWriter.Write(module)), Op.AtomicCompareExchange, 1, 0, 0);
    }

    [Fact]
    public void StrongCompareCapturesDynamicIndexAndOperandsOutsideRetryLoop()
    {
        var module = WgslReader.Parse("""
            @group(0) @binding(0) var<storage, read_write> data:array<atomic<u32>,4>;
            var<private> counter:u32;
            fn next()->u32 {counter++;return counter;}
            @compute @workgroup_size(1) fn main() {let result=atomicCompareExchangeWeak(&data[next()],next(),next());}
            """);
        var main = module.Functions.Single(f => f.Stage is not null);
        var declaration = Assert.IsType<Statement.Declare>(Assert.Single(main.Body.Statements, s => s is Statement.Declare { Initializer: Expression.Call { Function: "atomicCompareExchangeWeak" } }));
        var weak = Assert.IsType<Expression.Call>(declaration.Initializer);
        int position = main.Body.Statements.IndexOf(declaration);
        main.Body.Statements.RemoveRange(position, main.Body.Statements.Count - position);
        main.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("spirvAtomicCompareExchange", weak.Arguments, ShaderType.U32) { AtomicMemory = new(1, 0, 0) }));
        var lowered = WgslReader.Parse(WgslWriter.Write(module)); ModuleValidator.Validate(lowered);
        var helper = Assert.Single(lowered.Functions, f => f.Name.StartsWith("sia_atomic_", StringComparison.Ordinal));
        Assert.Equal(3, helper.Arguments.Count);
        Assert.DoesNotContain(Calls(helper.Body), c => c.Function == "next");
        Assert.Equal(3, Calls(lowered.Functions.Single(f => f.Stage is not null).Body).Count(c => c.Function == "next"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StrongCompareSupportsWorkgroupMemoryAndContinuingBlocks(bool continuing)
    {
        string operation = "let r=atomicCompareExchangeWeak(&value,9u,7u);output=r.old_value;";
        string body = continuing ? "var n=0u;loop {n++;continuing {" + operation + "break if n==1u;}}" : operation;
        string source = "var<workgroup> value:atomic<u32>; @group(0) @binding(0) var<storage,read_write> output:u32; @compute @workgroup_size(1) fn main(){" + body + "}";
        var module = SpirvReader.Parse(ShaderTranslator.WgslToSpirv(source));
        var lowered = WgslReader.Parse(WgslWriter.Write(module)); ModuleValidator.Validate(lowered);
        Assert.Single(lowered.Functions, f => f.Name.StartsWith("sia_atomic_", StringComparison.Ordinal));
        Assert.Single(Calls(lowered), c => c.Function == "atomicCompareExchangeWeak");
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(lowered)));
    }

    [Fact]
    public void FloatingPointAtomicMemoryRequirementsSurviveNativeRoundtrip()
    {
        var atomic = new ShaderType.Atomic(ShaderType.F32); var pointer = new ShaderType.Pointer(atomic, AddressSpace.Storage);
        var module = new Module(); module.Globals.Add(new("value", atomic, AddressSpace.Storage, Binding: new(0, 0)));
        var function = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        function.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("atomicAdd",
            [new Expression.Unary("&", new Expression.Reference("value", pointer), pointer), new Expression.Literal(2f, ShaderType.F32)], ShaderType.F32)
            { AtomicMemory = new(1, 72) })); module.Functions.Add(function);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module)); AssertAtomic(binary, Op.AtomicFAddEXT, 1, 72);
        var imported = SpirvReader.Parse(binary.ToBytes());
        Assert.Equal(new SpirvAtomicMemory(1, 72), Assert.Single(Calls(imported), c => c.Function == "atomicAdd").AtomicMemory);
        AssertAtomic(SpirvBinary.Parse(SpirvWriter.Write(imported)), Op.AtomicFAddEXT, 1, 72);
        Assert.Throws<ShaderException>(() => WgslWriter.Write(imported));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void WholeStructArrayLoadsStoresAndAliasedCopiesLowerAtomicLeaves(int mode)
    {
        var module = SpirvReader.Parse(AggregateFixture(mode).ToBytes()); ModuleValidator.Validate(module);
        var structure = Assert.IsType<ShaderType.Structure>(module.Globals[0].Type);
        Assert.IsType<ShaderType.Atomic>(Assert.IsType<ShaderType.Array>(structure.Members[0].Type).Element);
        Assert.Equal(ShaderType.U32, structure.Members[1].Type);
        var calls = Calls(module).ToArray(); Assert.Contains(calls, c => c.Function == "atomicStore"); Assert.Contains(calls, c => c.Function == "atomicLoad");
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        var imported = SpirvReader.Parse(SpirvWriter.Write(module)); ModuleValidator.Validate(imported);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(imported)));
    }

    [Fact]
    public void CooperativeFeatureCannotChangeSequentialAtomicsToAnInvalidMemoryModel()
    {
        var module = SpirvReader.Parse(Fixture(Op.AtomicIAdd, 1, 80).ToBytes());
        module.Globals.Add(new("matrix", new ShaderType.CooperativeMatrix(8, 8, ShaderType.F32, CooperativeRole.A), AddressSpace.Private));
        Assert.Contains("required Vulkan memory model", Assert.Throws<ShaderException>(() => SpirvWriter.Write(module)).Message);
    }

    private static void AssertAtomic(SpirvBinary binary, Op operation, uint scope, uint semantics, uint? unequal = null)
    {
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant && i.Operands.Length == 3).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        var operands = Assert.Single(binary.Instructions, i => (Op)i.Opcode == operation).Operands;
        int index = operation == Op.AtomicStore ? 1 : 3;
        Assert.Equal(scope, constants[operands[index]]); Assert.Equal(semantics, constants[operands[index + 1]]);
        if (unequal is uint value) Assert.Equal(value, constants[operands[index + 2]]);
    }
    private static IEnumerable<Expression.Call> Calls(Module module) => module.Functions.SelectMany(f => Calls(f.Body));
    private static IEnumerable<Expression.Call> Calls(Block block)
    {
        foreach (var s in block.Statements)
        {
            IEnumerable<Expression> expressions = s switch
            {
                Statement.Declare { Initializer: { } e } => [e], Statement.Evaluate e => [e.Value], Statement.Store st => [st.Target, st.Value],
                Statement.Return { Value: { } e } => [e], Statement.If i => [i.Condition], Statement.Loop { BreakIf: { } e } => [e], _ => []
            };
            foreach (var e in expressions) foreach (var c in Calls(e)) yield return c;
            IEnumerable<Block> bodies = s switch { Statement.Nested n => [n.Body], Statement.If i => [i.Accept, i.Reject], Statement.Loop l => [l.Body, l.Continuing], _ => [] };
            foreach (var body in bodies) foreach (var c in Calls(body)) yield return c;
        }
    }
    private static IEnumerable<Expression.Call> Calls(Expression e)
    {
        if (e is Expression.Call call) yield return call;
        IEnumerable<Expression> children = e switch
        {
            Expression.Call c => c.Arguments, Expression.Unary u => [u.Operand], Expression.Load l => [l.Pointer],
            Expression.Member m => [m.Base], Expression.Access a => [a.Base, a.Index], Expression.Binary b => [b.Left, b.Right], _ => []
        };
        foreach (var child in children) foreach (var c in Calls(child)) yield return c;
    }

    // The WGSL contract permits weak CAS to fail spuriously. Force that outcome
    // to distinguish strong lowering from a single weak call on actual output IR.
    private sealed class WeakMachine(ShaderFunction helper, Expression.Call invocation, uint initial, int spurious)
    {
        private sealed class BreakLoop : Exception;
        private readonly Dictionary<string, object> values = [];
        public uint Memory { get; private set; } = initial;
        public int Attempts { get; private set; }
        public uint Run()
        {
            for (int i = 0; i < helper.Arguments.Count; i++) values.Add(helper.Arguments[i].Name, Eval(invocation.Arguments[i]));
            return Execute(helper.Body) ?? throw new InvalidOperationException("No returned old value.");
        }
        private uint? Execute(Block block)
        {
            foreach (var s in block.Statements)
                switch (s)
                {
                    case Statement.Declare d: values[d.Name] = Eval(d.Initializer!); break;
                    case Statement.Store { Target: Expression.Reference r } st: values[r.Name] = Eval(st.Value); break;
                    case Statement.Break: throw new BreakLoop();
                    case Statement.Return r: return (uint)Eval(r.Value!);
                    case Statement.If i: if (Execute((bool)Eval(i.Condition) ? i.Accept : i.Reject) is uint result) return result; break;
                    case Statement.Loop l:
                        try
                        {
                            for (int n = 0; n < 10; n++) if (Execute(l.Body) is uint loopResult) return loopResult;
                            throw new InvalidOperationException("CAS retry failed to terminate.");
                        }
                        catch (BreakLoop) { break; }
                    default: throw new InvalidOperationException($"Unhandled test statement {s}.");
                }
            return null;
        }
        private object Eval(Expression e) => e switch
        {
            Expression.Literal l => l.Value, Expression.Reference r => values[r.Name],
            Expression.Load l => Eval(l.Pointer), Expression.Construct { Components.Count: 0, Type: ShaderType.Scalar } => 0u,
            Expression.Member m => ((Dictionary<string, object>)Eval(m.Base))[m.Name],
            Expression.Binary { Operator: "||" } b => (bool)Eval(b.Left) || (bool)Eval(b.Right),
            Expression.Binary { Operator: "!=" } b => !Eval(b.Left).Equals(Eval(b.Right)),
            Expression.Call { Function: "atomicCompareExchangeWeak" } c => Weak(c),
            _ => throw new InvalidOperationException($"Unhandled test expression {e}.")
        };
        private object Weak(Expression.Call call)
        {
            uint expected = (uint)Eval(call.Arguments[1]), desired = (uint)Eval(call.Arguments[2]), old = Memory;
            bool exchanged = old == expected && Attempts >= spurious; Attempts++;
            if (exchanged) Memory = desired;
            return new Dictionary<string, object> { ["old_value"] = old, ["exchanged"] = exchanged };
        }
    }
}
