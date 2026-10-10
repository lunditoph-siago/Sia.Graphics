using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class MeshNativeControlFlowTests
{
    private static SpirvInstruction I(Op op, params uint[] args) => new((ushort)op, args);

    [Theory]
    [InlineData(true, 0u, false)] [InlineData(true, 1u, false)]
    [InlineData(false, 0u, false)] [InlineData(false, 1u, false)]
    [InlineData(true, 0u, true)] [InlineData(true, 1u, true)]
    [InlineData(false, 0u, true)] [InlineData(false, 1u, true)]
    public void TaskHelperTerminationUnwindsCallersAndPreservesEarlierEffects(bool payload, uint selector, bool continuing)
    {
        var module = SpirvReader.Parse(TaskFixture(payload, selector, continuing).ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        var machine = new TaskMachine(module);
        var result = Assert.IsType<object[]>(machine.Run());
        Assert.Equal(new object[] { selector == 0 ? 1u : 2u, 1u, 1u }, result);
        if (payload) Assert.Equal(selector == 0 ? 55u : 77u, machine.Globals["g12"]);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.EmitMeshTasksEXT);
        var again = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(again);
        var machineAgain = new TaskMachine(again);
        Assert.Equal(result, Assert.IsType<object[]>(machineAgain.Run()));
        if (payload) Assert.Equal(selector == 0 ? 55u : 77u, machineAgain.Globals[again.Functions.Single(f => f.Stage == ShaderStage.Task).TaskPayload!]);
    }

    internal static SpirvBinary TaskFixture(bool payload, uint selector, bool continuing = false)
    {
        var code = new List<SpirvInstruction> {
            I(Op.Capability, 1), I(Op.Capability, 5283), I(Op.Extension, SpirvBinary.StringWords("SPV_EXT_mesh_shader")), I(Op.MemoryModel, 0, 1),
            I(Op.EntryPoint, new uint[] { 5364, 30 }.Concat(SpirvBinary.StringWords("main")).Concat(payload ? [12u] : Array.Empty<uint>()).ToArray()),
            I(Op.ExecutionMode, 30, 17, 2, 1, 1),
            I(Op.TypeVoid, 1), I(Op.TypeInt, 2, 32, 0), I(Op.TypeBool, 3), I(Op.TypeFunction, 5, 1), I(Op.TypeFunction, 6, 2, 2),
            I(Op.Constant, 2, 7, 0), I(Op.Constant, 2, 8, 1), I(Op.Constant, 2, 9, 77), I(Op.Constant, 2, 10, 99),
            I(Op.Constant, 2, 14, 2), I(Op.Constant, 2, 15, 55),
        };
        if (payload) code.AddRange([I(Op.TypePointer, 11, 5402, 2), I(Op.Variable, 11, 12, 5402)]);
        code.AddRange([I(Op.Function, 1, 30, 0, 5), I(Op.Label, 31)]);
        if (payload) code.Add(I(Op.Store, 12, 15));
        if (continuing) code.AddRange([I(Op.Branch, 40), I(Op.Label, 40), I(Op.LoopMerge, 43, 42, 0), I(Op.Branch, 41), I(Op.Label, 41), I(Op.Branch, 42), I(Op.Label, 42)]);
        code.Add(I(Op.FunctionCall, 2, 32, 60, selector == 0 ? 7u : 8u));
        if (payload) code.Add(I(Op.Store, 12, 9));
        if (continuing) code.AddRange([I(Op.IEqual, 3, 45, 32, 8), I(Op.BranchConditional, 45, 43, 40), I(Op.Label, 43)]);
        code.Add(I(Op.EmitMeshTasksEXT, payload ? [14,8,8,12] : [14,8,8]));
        code.AddRange([I(Op.FunctionEnd), I(Op.Function, 2, 60, 0, 6), I(Op.FunctionParameter, 2, 61), I(Op.Label, 62), I(Op.FunctionCall, 2, 63, 80, 61)]);
        if (payload) code.Add(I(Op.Store, 12, 10));
        code.AddRange([I(Op.ReturnValue, 63), I(Op.FunctionEnd), I(Op.Function, 2, 80, 0, 6), I(Op.FunctionParameter, 2, 81), I(Op.Label, 82),
            I(Op.IEqual, 3, 83, 81, 7), I(Op.SelectionMerge, 86, 0), I(Op.BranchConditional, 83, 84, 85), I(Op.Label, 84),
            I(Op.EmitMeshTasksEXT, payload ? [8,8,8,12] : [8,8,8]), I(Op.Label, 85), I(Op.ReturnValue, 8), I(Op.Label, 86), I(Op.Unreachable), I(Op.FunctionEnd)]);
        return new() { Version = 0x00010400, Bound = 87, Instructions = code };
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MeshCountHelperCanServeMultipleEntriesAndOutputAggregates(bool shared)
    {
        var module = SpirvReader.Parse(MeshFixture(shared).ToBytes()); ModuleValidator.Validate(module);
        Assert.Equal(2, module.Functions.Count(f => f.Stage == ShaderStage.Mesh));
        var helper = module.Functions.Single(f => f.Name == "sia_fn60");
        var guard = Assert.IsType<Statement.If>(Assert.Single(helper.Body.Statements.Take(1)));
        Assert.Equal(shared ? 2 : 4, guard.Accept.Statements.Count);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        Assert.Equal(2, SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)).Instructions.Count(i => (Op)i.Opcode == Op.SetMeshOutputsEXT));
    }

    internal static SpirvBinary MeshFixture(bool shared)
    {
        var input = MeshShaderTests.AggregateFixture(0); var code = input.Instructions.ToList();
        int entry = code.FindIndex(i => (Op)i.Opcode == Op.EntryPoint);
        code.InsertRange(entry + 1, [I(Op.EntryPoint, new uint[] { 5365, 40 }.Concat(SpirvBinary.StringWords("second")).Concat(shared ? [12u,15u] : [112u,115u]).ToArray()),
            I(Op.ExecutionMode, 40, 17, 4, 1, 1), I(Op.ExecutionMode, 40, 26, 1), I(Op.ExecutionMode, 40, 5270, 1), I(Op.ExecutionMode, 40, 27)]);
        if (!shared) code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeVoid), I(Op.Decorate, 115, 11, 5294));
        int first = code.FindIndex(i => (Op)i.Opcode == Op.Function);
        var declarations = new List<SpirvInstruction> { I(Op.TypeFunction, 26, 1, 5, 5) };
        if (!shared) declarations.AddRange([I(Op.Variable, 11, 112, 3), I(Op.Variable, 14, 115, 3)]);
        code.InsertRange(first, declarations);
        code[code.FindIndex(i => (Op)i.Opcode == Op.SetMeshOutputsEXT)] = I(Op.FunctionCall, 1, 64, 60, 6, 6);
        code.AddRange([I(Op.Function, 1, 40, 0, 2), I(Op.Label, 41), I(Op.FunctionCall, 1, 44, 60, 6, 6),
            I(Op.Store, shared ? 12u : 112u, 21), I(Op.AccessChain, 24, 43, shared ? 15u : 115u, 7), I(Op.Store, 43, 7), I(Op.Return), I(Op.FunctionEnd),
            I(Op.Function, 1, 60, 0, 26), I(Op.FunctionParameter, 5, 61), I(Op.FunctionParameter, 5, 62), I(Op.Label, 63), I(Op.SetMeshOutputsEXT, 61, 62), I(Op.Return), I(Op.FunctionEnd)]);
        return new() { Version = input.Version, Bound = 116, Instructions = code };
    }

    // Runs the imported scalar task program to check observable writes after
    // termination, independently of the generated control-flow shape.
    private sealed class TaskMachine(Module module)
    {
        public Dictionary<string, object> Globals { get; } = module.Globals.ToDictionary(g => g.Name, g => Zero(g.Type));
        private readonly Stack<Dictionary<string, object>> frames = new();
        private sealed class Returned(object? value) : Exception { public object? Value { get; } = value; }
        private sealed class BreakLoop : Exception;
        private sealed class ContinueLoop : Exception;
        public object? Run() { var entry = module.Functions.Single(f => f.Stage == ShaderStage.Task); return Call(entry.Name, entry.Arguments.Select(a => Zero(a.Type)).ToArray()); }
        private static object Zero(ShaderType type) => type switch { ShaderType.Scalar { Kind: ScalarKind.Bool } => false, ShaderType.Vector v => Enumerable.Range(0, v.Size).Select(_ => Zero(v.Component)).ToArray(), _ => 0u };
        private object? Call(string name, object[] args)
        {
            var function = module.Functions.Single(f => f.Name == name);
            frames.Push(function.Arguments.Select((a, i) => (a.Name, Value: args[i])).ToDictionary(p => p.Name, p => p.Value));
            try { Body(function.Body); return null; } catch (Returned result) { return result.Value; } finally { frames.Pop(); }
        }
        private object Eval(Expression e) => e switch {
            Expression.Literal l => l.Value,
            Expression.Reference r => frames.Peek().TryGetValue(r.Name, out var value) ? value : Globals[r.Name],
            Expression.Load l => Eval(l.Pointer),
            Expression.Access a => ((object[])Eval(a.Base))[(uint)Eval(a.Index)],
            Expression.Construct { Components.Count: 0 } c => Zero(c.Type),
            Expression.Construct c => c.Components.Select(Eval).ToArray(),
            Expression.Call c => Call(c.Function, c.Arguments.Select(Eval).ToArray())!,
            Expression.Binary { Operator: "==" } b => Equals(Eval(b.Left), Eval(b.Right)),
            Expression.Unary { Operator: "!" } u => !(bool)Eval(u.Operand),
            _ => throw new InvalidOperationException("Unexpected task fixture expression: " + e),
        };
        private void Body(Block block)
        {
            foreach (var statement in block.Statements)
                switch (statement)
                {
                    case Statement.Declare d: frames.Peek().Add(d.Name, d.Initializer is null ? Zero(d.Type) : Eval(d.Initializer)); break;
                    case Statement.Store { Target: Expression.Reference r } s:
                        if (frames.Peek().ContainsKey(r.Name)) frames.Peek()[r.Name] = Eval(s.Value); else Globals[r.Name] = Eval(s.Value); break;
                    case Statement.Evaluate e: Eval(e.Value); break;
                    case Statement.If i: Body((bool)Eval(i.Condition) ? i.Accept : i.Reject); break;
                    case Statement.Return r: throw new Returned(r.Value is null ? null : Eval(r.Value));
                    case Statement.Break: throw new BreakLoop();
                    case Statement.Continue: throw new ContinueLoop();
                    case Statement.Loop l:
                        for (int iterations = 0; ; iterations++)
                        {
                            Assert.True(iterations < 32, "Task fixture did not terminate.");
                            try { Body(l.Body); } catch (BreakLoop) { break; } catch (ContinueLoop) { }
                            Body(l.Continuing);
                            if (l.BreakIf is not null && (bool)Eval(l.BreakIf)) break;
                        }
                        break;
                    default: throw new InvalidOperationException("Unexpected task fixture statement: " + statement);
                }
        }
    }
}
