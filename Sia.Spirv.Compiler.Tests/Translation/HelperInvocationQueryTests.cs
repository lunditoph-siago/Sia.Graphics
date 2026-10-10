using System.Collections.Immutable;
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

public class HelperInvocationQueryTests
{
    internal static SpirvBinary Fixture(bool derivative = false)
    {
        const string resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
        const string state = "fn state()->bool{return inputs[1]!=0u;}";
        string source = resources + state + (derivative
            ? "@fragment fn main()->@location(0) f32{if state(){return dpdx(1.0);}return 2.0;}"
            : "fn update(p:ptr<function,u32>,condition:bool)->u32{let before=state();if condition{discard;}"
                + "let after=state();*p=select(0u,1u,before)+select(0u,2u,after);_=state();return *p;}"
                + "@fragment fn main()->@location(0) u32{outputs[0]=43u;outputs[1]=23u;var value=99u;"
                + "let result=update(&value,inputs[0]!=0u);outputs[0]=result;outputs[1]=99u;return result+100u;}");
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(WgslReader.Parse(source)));
        uint boolean = binary.Instructions.Single(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        var code = new List<SpirvInstruction>(); bool queryFunction = false;
        foreach (var instruction in binary.Instructions) {
            var op = (Op)instruction.Opcode;
            if (op == Op.Function) queryFunction = instruction.Operands[0] == boolean;
            if (queryFunction && op == Op.ReturnValue) code.Add(new(5381, [boolean, instruction.Operands[0]]));
            if (!queryFunction || op is Op.Function or Op.Label or Op.ReturnValue or Op.FunctionEnd) code.Add(instruction);
            if (op == Op.FunctionEnd) queryFunction = false;
        }
        DeclareFeature(code);
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
    }

    internal static SpirvBinary PointerFixture()
    {
        var binary = InvocationDemotionTests.PointerFixture(); var code = binary.Instructions.ToList();
        uint boolean = code.Single(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        int demote = code.FindIndex(i => i.Opcode == 5380);
        code.Insert(demote + 1, new(5381, [boolean, binary.Bound + 1]));
        code.Insert(demote, new(5381, [boolean, binary.Bound]));
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound + 2, Instructions = code };
    }

    private static void DeclareFeature(List<SpirvInstruction> code)
    {
        if (!code.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5379))
            code.Insert(code.FindIndex(i => (Op)i.Opcode != Op.Capability), new((ushort)Op.Capability, [5379]));
        if (!code.Any(i => (Op)i.Opcode == Op.Extension && SpirvBinary.ReadString(i.Operands, out _) == "SPV_EXT_demote_to_helper_invocation"))
            code.Insert(code.FindIndex(i => (Op)i.Opcode is not (Op.Capability or Op.Extension)),
                new((ushort)Op.Extension, SpirvBinary.StringWords("SPV_EXT_demote_to_helper_invocation")));
    }

    [Theory] [InlineData(0u, 0u, 99u, 100u, false)] [InlineData(1u, 43u, 23u, 102u, true)]
    public void QuerySnapshotsSurviveHelperExpansionAndDemotion(uint input, uint first, uint second, uint result, bool demoted)
    {
        var source = Fixture(); byte[] before = source.ToBytes(); var module = SpirvReader.Parse(before);
        byte[] output = SpirvWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Contains(SpirvBinary.Parse(output).Instructions, i => i.Opcode == 5381);
        foreach (var candidate in new[] { module, SpirvReader.Parse(output) }) {
            var machine = new CanonicalExecution(candidate, [input, 0]);
            Assert.Equal(new[] { first, second }, machine.Run().Output); Assert.Equal(result, machine.EntryResult);
            Assert.Equal(demoted, machine.InvocationKilled);
            Assert.Equal(new[] { false, demoted, demoted }, machine.HelperQueryValues);
        }
        Assert.Equal(before, source.ToBytes()); Assert.Equal(output, SpirvWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Theory] [InlineData(0u)] [InlineData(1u)]
    public void InitiallyHelperQueryRemainsTrueAndSuppressesStores(uint input)
    {
        var module = SpirvReader.Parse(Fixture().ToBytes());
        foreach (var candidate in new[] { module, SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)) }) {
            var machine = new CanonicalExecution(candidate, [input, 0], initiallyHelper: true);
            Assert.Equal(new uint[] { 0, 0 }, machine.Run().Output); Assert.Equal(103u, machine.EntryResult);
            Assert.Equal(new[] { true, true, true }, machine.HelperQueryValues);
        }
    }

    [Theory] [InlineData(ShaderStage.Compute)] [InlineData(ShaderStage.Vertex)]
    public void QueryHelperCannotBeReachedFromNonFragmentStage(ShaderStage stage)
    {
        var module = new Module(); var helper = new ShaderFunction("state") { ReturnType = ShaderType.Bool };
        helper.Body.Statements.Add(new Statement.Return(new Expression.HelperInvocation())); module.Functions.Add(helper);
        var main = new ShaderFunction("main") { Stage = stage };
        main.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("state", [], ShaderType.Bool, CallBinding.Function))); module.Functions.Add(main);
        if (stage == ShaderStage.Vertex) {
            main.ReturnType = new ShaderType.Vector(4, ShaderType.F32); main.ReturnBinding = new(Builtin: "position");
            main.Body.Statements.Add(new Statement.Return(new Expression.Construct(main.ReturnType, [])));
        }
        var error = Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
        Assert.Contains("stage", error.Message);
    }

    [Fact]
    public void QueryOnlyFeatureIsCheckedBeforeEmission()
    {
        var module = SpirvReader.Parse(Fixture(derivative: true).ToBytes());
        var capability = Assert.Throws<ShaderException>(() => ShaderTargetValidator.ValidateModule(module,
            SpirvCompilationTarget.Default with { AllowedCapabilities = ImmutableHashSet.Create(1u) }, resources: false));
        Assert.Contains("5379", capability.Message);
        var extension = Assert.Throws<ShaderException>(() => ShaderTargetValidator.ValidateModule(module,
            SpirvCompilationTarget.Default with { AllowedExtensions = ImmutableHashSet<string>.Empty }, resources: false));
        Assert.Contains("extension", extension.Message);
    }

    [Fact]
    public void QueryHasOrderedEffectsAndVerifierRequiresBooleanResult()
    {
        var module = new Module(); var main = new ShaderFunction("main") { Stage = ShaderStage.Fragment }; module.Functions.Add(main);
        var graph = new ControlFlowFunction(main); var entry = graph.Block(); graph.Entry = entry.Id;
        var query = new ControlFlowInstruction(graph.Value(ShaderType.U32), new ValueOperation.HelperInvocation());
        entry.Instructions.Add(query); entry.Terminator = new ControlFlowTerminator.Return();
        Assert.Equal(ShaderEffects.Convergent | ShaderEffects.ReadInvocationState, query.Effects);
        Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module));
        entry.Instructions[0] = query with { Result = graph.Value(ShaderType.Bool) };
        ControlFlowVerifier.Validate(graph, module);
    }

    [Fact]
    public void UnusedNativePointerQueriesRetainOrderAndTrace()
    {
        var traces = new List<CanonicalPassTrace>(); var binary = PointerFixture(); byte[] before = binary.ToBytes();
        var module = SpirvReader.ReadBinary(binary, traces: traces);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import" && t.Before.Contains("helper-query effects="));
        var output = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        // Two reads in the pointer helper are copied into each of its two call sites.
        Assert.Equal(4, output.Instructions.Count(i => i.Opcode == 5381));
        int demote = output.Instructions.ToList().FindIndex(i => i.Opcode == 5380);
        Assert.Contains(output.Instructions.Take(demote), i => i.Opcode == 5381);
        Assert.Contains(output.Instructions.Skip(demote + 1), i => i.Opcode == 5381);
        foreach (var candidate in new[] { module, SpirvReader.Parse(output.ToBytes()) }) {
            var machine = new CanonicalExecution(candidate, [0u]); Assert.Equal(new uint[] { 1, 10 }, machine.Run().Output);
            Assert.Equal(new[] { false, true }, machine.HelperQueryValues);
        }
        Assert.Equal(before, binary.ToBytes());
    }

    [Fact]
    public void WgslRejectsDynamicQueryWithoutChangingNativeModule()
    {
        var module = SpirvReader.Parse(Fixture().ToBytes()); byte[] before = SpirvWriter.Write(module, SpirvCompilationTarget.Default);
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("helper", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, SpirvWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void QueryResultIsNonuniformThroughHelperSummary(bool canonical)
    {
        var module = SpirvReader.Parse(Fixture(derivative: true).ToBytes());
        var graphs = new Dictionary<string, ControlFlowFunction>();
        if (canonical) foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module); graphs.Add(function.Name, graph!);
        }
        var error = Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        Assert.Contains("uniform", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryRequiresCapabilityAndLegacyExtension()
    {
        var source = Fixture(derivative: true);
        foreach (bool capability in new[] { false, true }) {
            var code = source.Instructions.Where(i => capability ? (Op)i.Opcode != Op.Capability || i.Operands[0] != 5379
                : (Op)i.Opcode != Op.Extension || SpirvBinary.ReadString(i.Operands, out _) != "SPV_EXT_demote_to_helper_invocation").ToList();
            var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary {
                Version = source.Version, Bound = source.Bound, Instructions = code
            }.ToBytes()));
            Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains(capability ? "5379" : "extension", error.Message);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void QueryRejectsInvalidResultTypeOrOperands(bool extraOperand)
    {
        var source = Fixture(); uint integer = source.Instructions.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands[1] == 32).Operands[0];
        var code = source.Instructions.Select(i => i.Opcode == 5381 ? i with {
            Operands = extraOperand ? [.. i.Operands, 0u] : [integer, i.Operands[1]]
        } : i).ToList();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary {
            Version = source.Version, Bound = source.Bound, Instructions = code
        }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains(extraOperand ? "operand" : "bool", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeCoreQueryHasNoExtensionAndHonorsCapabilityPolicy()
    {
        var module = SpirvReader.Parse(Fixture().ToBytes());
        var target = SpirvCompilationTarget.Default with { Version = 0x10600, Environment = "vulkan1.3", AllowedExtensions = ImmutableHashSet<string>.Empty };
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, target, new() { PipelineConstants = new Dictionary<string, double>() }));
        Assert.Contains(binary.Instructions, i => i.Opcode == 5381);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Extension);
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, target with { AllowedCapabilities = ImmutableHashSet.Create(1u) }));
    }
}
