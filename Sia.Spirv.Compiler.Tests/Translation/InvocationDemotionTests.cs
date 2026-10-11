using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using System.Collections.Immutable;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class InvocationDemotionTests
{
    internal const string Source = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
        + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
        + "fn soften(p:ptr<function,u32>,condition:bool)->u32{if condition{discard;}*p+=7u;return *p+10u;}"
        + "@fragment fn main()->@location(0) u32{outputs[0]=43u;outputs[1]=23u;var value=2u;"
        + "let result=soften(&value,(inputs[0]&1u)!=0u);outputs[0]=result;outputs[1]=99u;return result+value+100u;}";

    internal static SpirvBinary PointerFixture()
    {
        var binary = NativeCanonicalPointerReturnTests.Fixture();
        uint entry = binary.Instructions.Single(i => (Op)i.Opcode == Op.EntryPoint).Operands[1];
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.ExecutionMode).Select(i => (Op)i.Opcode == Op.EntryPoint
            ? i with { Operands = new uint[] { 4 }.Concat(i.Operands.Skip(1)).ToArray() } : i).ToList();
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.EntryPoint) + 1, new((ushort)Op.ExecutionMode, [entry, 7]));
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        bool helper = false; int returns = 0, index;
        for (index = 0; index < code.Count; index++) {
            if ((Op)code[index].Opcode == Op.Function) helper = pointers.Contains(code[index].Operands[0]);
            if (helper && (Op)code[index].Opcode == Op.ReturnValue && ++returns == 2) break;
        }
        if (index == code.Count) throw new InvalidOperationException("Fixture needs two helper returns.");
        code.Insert(index, new(5380, [])); AddExtension(code, "SPV_EXT_demote_to_helper_invocation", 5379);
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
    }

    internal static SpirvBinary TerminateFixture(bool pointer = false)
    {
        var binary = pointer ? NativeInvocationKillTests.Fixture() : NativeInvocationKillTests.ColorFixture();
        var code = binary.Instructions.Select(i => (Op)i.Opcode == Op.Kill ? new SpirvInstruction(4416, []) : i).ToList();
        AddExtension(code, "SPV_KHR_terminate_invocation");
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
    }

    private static void AddExtension(List<SpirvInstruction> code, string name, uint? capability = null)
    {
        if (capability is uint cap) code.Insert(code.FindIndex(i => (Op)i.Opcode != Op.Capability), new((ushort)Op.Capability, [cap]));
        code.Insert(code.FindIndex(i => (Op)i.Opcode is not (Op.Capability or Op.Extension)), new((ushort)Op.Extension, SpirvBinary.StringWords(name)));
    }

    [Theory] [InlineData(0u, 19u, 99u, false)] [InlineData(1u, 43u, 23u, true)]
    public void DiscardKeepsLocalComputationAndHelperReturns(uint input, uint first, uint second, bool demoted)
    {
        var module = WgslReader.Parse(Source); byte[] binary = SpirvWriter.Write(module, SpirvCompilationTarget.Default); string text = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Contains(SpirvBinary.Parse(binary).Instructions, i => i.Opcode == 5380);
        Assert.DoesNotContain(SpirvBinary.Parse(binary).Instructions, i => (Op)i.Opcode == Op.Kill);
        foreach (var candidate in new[] { module, WgslReader.Parse(text), SpirvReader.Parse(binary) }) {
            var machine = new CanonicalExecution(candidate, [input]); Assert.Equal(new[] { first, second }, machine.Run().Output);
            Assert.Equal(demoted, machine.InvocationKilled); Assert.Equal(128u, machine.EntryResult);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ConditionalDemotionDoesNotMakeFollowingDerivativeControlNonuniform(bool canonical)
    {
        var module = WgslReader.Parse("fn soften(condition:bool)->f32{if condition{discard;}return dpdx(1.0);}"
            + "@fragment fn main(@location(0) value:f32)->@location(0) f32{return soften(value>0.0);}");
        var graphs = new Dictionary<string, ControlFlowFunction>();
        if (canonical) foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module); graphs.Add(function.Name, graph!);
        }
        _ = UniformityAnalysis.Validate(module, graphs);
    }

    [Theory] [InlineData(0u, 1u, 10u, true)] [InlineData(5u, 14u, 26u, false)]
    public void NativeDemotionRetainsNormalPointerReturnAndCanonicalEffects(uint input, uint first, uint second, bool demoted)
    {
        var traces = new List<CanonicalPassTrace>(); var source = PointerFixture(); byte[] before = source.ToBytes();
        var module = SpirvReader.ReadBinary(source, traces: traces);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import" && t.Before.Contains("helper-demote effects="));
        Assert.Equal(before, source.ToBytes());
        foreach (var candidate in new[] { module, WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)) }) {
            var machine = new CanonicalExecution(candidate, [input]); Assert.Equal(new[] { first, second }, machine.Run().Output);
            Assert.Equal(demoted, machine.InvocationKilled);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExplicitTerminationRetainsNativeOpcodeAndTypedWgslReturn(bool pointer)
    {
        var source = TerminateFixture(pointer); byte[] before = source.ToBytes(); var module = SpirvReader.Parse(before);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)); Assert.Contains(binary.Instructions, i => i.Opcode == 4416);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Extension && SpirvBinary.ReadString(i.Operands, out _) == "SPV_KHR_terminate_invocation");
        _ = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)); Assert.Equal(before, source.ToBytes());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void InvocationExtensionsRequireDeclarationsBeforeSpirv16(bool terminate)
    {
        var binary = terminate ? TerminateFixture() : PointerFixture();
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Extension || SpirvBinary.ReadString(i.Operands, out _) !=
            (terminate ? "SPV_KHR_terminate_invocation" : "SPV_EXT_demote_to_helper_invocation")).ToList();
        var invalid = new SpirvBinary { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(invalid.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("extension", error.Message, StringComparison.OrdinalIgnoreCase);
        _ = SpirvReader.Parse(new SpirvBinary { Version = 0x10600, Bound = binary.Bound, Instructions = code }.ToBytes());
    }

    [Fact]
    public void DemotionOutputRespectsExplicitCapabilityPolicy()
    {
        var module = WgslReader.Parse(Source);
        var error = Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, SpirvCompilationTarget.Default with { AllowedCapabilities = ImmutableHashSet.Create(1u) }));
        Assert.Contains("5379", error.Message);
        var early = Assert.Throws<ShaderException>(() => ShaderTargetValidator.ValidateModule(module,
            SpirvCompilationTarget.Default with { AllowedCapabilities = ImmutableHashSet.Create(1u) }, resources: false));
        Assert.Equal(DiagnosticStage.SpirvWrite, early.Diagnostic.Stage); Assert.Contains("5379", early.Message);
    }

    private sealed class EqualCapabilities : IEqualityComparer<uint>
    {
        public bool Equals(uint left, uint right) => true;
        public int GetHashCode(uint value) => 0;
    }

    [Fact]
    public void HostSetComparerCannotGrantDemotionCapability()
    {
        var target = SpirvCompilationTarget.Default with { AllowedCapabilities = ImmutableHashSet.Create(new EqualCapabilities(), 1u) };
        var error = Assert.Throws<ShaderException>(() => ShaderTargetValidator.ValidateModule(WgslReader.Parse(Source), target, resources: false));
        Assert.Contains("5379", error.Message);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void InvocationExtensionPolicyIsCheckedBeforeEmission(bool terminate)
    {
        var module = terminate ? SpirvReader.Parse(TerminateFixture().ToBytes()) : WgslReader.Parse(Source);
        var target = SpirvCompilationTarget.Default with { AllowedExtensions = ImmutableHashSet<string>.Empty };
        var early = Assert.Throws<ShaderException>(() => ShaderTargetValidator.ValidateModule(module, target, resources: false));
        Assert.Equal(DiagnosticStage.SpirvWrite, early.Diagnostic.Stage); Assert.Contains("extension", early.Message);
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, target));
        _ = SpirvWriter.Write(module, target with { Version = 0x10600, Environment = "vulkan1.3" });
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void Spirv16OutputUsesCoreInvocationInstructionsWithoutExtensions(bool terminate)
    {
        var module = terminate ? SpirvReader.Parse(TerminateFixture().ToBytes()) : WgslReader.Parse(Source);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default with { Version = 0x10600, Environment = "vulkan1.3" }));
        Assert.Equal(0x10600u, binary.Version);
        Assert.Contains(binary.Instructions, i => i.Opcode == (terminate ? 4416 : 5380));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Extension &&
            SpirvBinary.ReadString(i.Operands, out _).Contains(terminate ? "terminate_invocation" : "demote_to_helper_invocation"));
        _ = SpirvReader.Parse(binary.ToBytes());
    }

    [Fact]
    public void NativeDemotionRequiresCapability()
    {
        var source = PointerFixture();
        var binary = new SpirvBinary { Version = source.Version, Bound = source.Bound,
            Instructions = source.Instructions.Where(i => (Op)i.Opcode != Op.Capability || i.Operands[0] != 5379).ToList() };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(binary.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("5379", error.Message);
    }

    [Fact]
    public void DemotionCannotBeReachedFromComputeEntry()
    {
        var source = PointerFixture(); uint entry = source.Instructions.Single(i => (Op)i.Opcode == Op.EntryPoint).Operands[1];
        var code = source.Instructions.Where(i => (Op)i.Opcode != Op.ExecutionMode).Select(i => (Op)i.Opcode == Op.EntryPoint
            ? i with { Operands = new uint[] { 5 }.Concat(i.Operands.Skip(1)).ToArray() } : i).ToList();
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.EntryPoint) + 1, new((ushort)Op.ExecutionMode, [entry, 17, 1, 1, 1]));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary {
            Version = source.Version, Bound = source.Bound, Instructions = code
        }.ToBytes()));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage); Assert.Contains("stage", error.Message);
    }
}
