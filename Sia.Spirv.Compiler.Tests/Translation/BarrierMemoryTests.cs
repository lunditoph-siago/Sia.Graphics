using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using static Sia.Spirv.Compiler.Translation.Tests.SpirvTests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class BarrierMemoryTests
{
    internal static SpirvBinary Fixture(bool control, uint execution, uint scope, uint semantics, bool vulkan = false, uint size = 4, bool conditional = false)
    {
        var code = new List<SpirvInstruction> { I(Op.Capability, 1) };
        if (execution == 3 && control) code.Add(I(Op.Capability, 61));
        if (vulkan)
        {
            code.Add(I(Op.Capability, 5345)); if (scope == 1) code.Add(I(Op.Capability, 5346));
            code.Add(I(Op.Extension, SpirvBinary.StringWords("SPV_KHR_vulkan_memory_model")));
        }
        code.AddRange([
            I(Op.MemoryModel, 0, vulkan ? 3u : 1u), Entry(30, 22), I(Op.ExecutionMode, 30, 17, size, 1, 1),
            I(Op.Decorate, 5, 6, 4), I(Op.Decorate, 6, 2), I(Op.MemberDecorate, 6, 0, 35, 0),
            I(Op.Decorate, 20, 34, 0), I(Op.Decorate, 20, 33, 0), I(Op.Decorate, 20, 24),
            I(Op.Decorate, 21, 34, 0), I(Op.Decorate, 21, 33, 1), I(Op.Decorate, 22, 11, 29),
            I(Op.TypeVoid, 1), I(Op.TypeInt, 2, 32, 0), I(Op.TypeBool, 3), I(Op.TypePointer, 4, 1, 2),
            I(Op.TypeRuntimeArray, 5, 2), I(Op.TypeStruct, 6, 5), I(Op.TypePointer, 7, 12, 6), I(Op.TypePointer, 8, 12, 2), I(Op.TypeFunction, 9, 1),
            I(Op.Constant, 2, 10, 0), I(Op.Constant, 2, 11, 7), I(Op.Constant, 2, 12, execution), I(Op.Constant, 2, 13, scope), I(Op.Constant, 2, 14, semantics),
            I(Op.Variable, 7, 20, 12), I(Op.Variable, 7, 21, 12), I(Op.Variable, 4, 22, 1),
            I(Op.Function, 1, 30, 0, 9), I(Op.Label, 31), I(Op.Load, 2, 32, 22),
            I(Op.AccessChain, 8, 33, 20, 10, 32), I(Op.Load, 2, 34, 33), I(Op.IAdd, 2, 35, 34, 11), I(Op.AccessChain, 8, 36, 21, 10, 32)]);
        if (conditional) code.AddRange([I(Op.IEqual, 3, 37, 32, 10), I(Op.SelectionMerge, 38, 0), I(Op.BranchConditional, 37, 39, 38), I(Op.Label, 39)]);
        code.Add(control ? I(Op.ControlBarrier, 12, 13, 14) : I(Op.MemoryBarrier, 13, 14));
        if (conditional) code.AddRange([I(Op.Branch, 38), I(Op.Label, 38)]);
        code.AddRange([I(Op.Store, 36, 35), I(Op.Return), I(Op.FunctionEnd)]);
        return new() { Bound = 100, Instructions = code };
    }

    [Theory]
    [InlineData(true, 2u, 2u, 264u, false)] [InlineData(true, 2u, 1u, 264u, false)]
    [InlineData(true, 2u, 3u, 264u, false)] [InlineData(true, 3u, 3u, 264u, false)]
    [InlineData(true, 3u, 2u, 264u, false)] [InlineData(true, 2u, 1u, 72u, false)]
    [InlineData(true, 2u, 2u, 72u, false)] [InlineData(true, 2u, 2u, 2056u, false)]
    [InlineData(true, 2u, 2u, 66u, false)] [InlineData(true, 2u, 2u, 68u, false)]
    [InlineData(true, 2u, 4u, 0u, false)] [InlineData(true, 2u, 5u, 264u, true)]
    [InlineData(true, 2u, 1u, 24648u, true)] [InlineData(true, 2u, 2u, 272u, false)]
    [InlineData(false, 0u, 1u, 72u, false)] [InlineData(false, 0u, 2u, 264u, false)]
    [InlineData(false, 0u, 3u, 264u, false)] [InlineData(false, 0u, 4u, 264u, false)]
    [InlineData(false, 0u, 5u, 72u, true)] [InlineData(false, 0u, 1u, 24648u, true)]
    public void NativeBarrierOperandsSurviveCloningAndReemission(bool control, uint execution, uint scope, uint semantics, bool vulkan)
    {
        var module = SpirvReader.Parse(Fixture(control, execution, scope, semantics, vulkan).ToBytes()); ModuleValidator.Validate(module);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double>());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(resolved));
        var instruction = Assert.Single(binary.Instructions, i => (Op)i.Opcode == (control ? Op.ControlBarrier : Op.MemoryBarrier));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == (control ? Op.MemoryBarrier : Op.ControlBarrier));
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.Equal(control ? new[] { execution, scope, semantics } : new[] { scope, semantics }, instruction.Operands.Select(id => constants[id]));
        Assert.Equal(vulkan, SpirvReader.Parse(binary.ToBytes()).VulkanMemoryModel);
        Assert.Equal(vulkan && scope == 1, binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5346));
    }

    [Theory]
    [InlineData(2u, 2u, 264u, false)] [InlineData(2u, 1u, 264u, false)] [InlineData(2u, 3u, 264u, false)]
    [InlineData(3u, 3u, 264u, false)] [InlineData(2u, 2u, 72u, false)] [InlineData(2u, 2u, 2056u, false)]
    [InlineData(2u, 2u, 66u, false)] [InlineData(2u, 2u, 68u, false)] [InlineData(2u, 4u, 0u, false)]
    [InlineData(2u, 5u, 264u, true)]
    public void EquivalentControlBarriersProduceValidWgsl(uint execution, uint scope, uint semantics, bool vulkan)
    {
        var module = SpirvReader.Parse(Fixture(true, execution, scope, semantics, vulkan).ToBytes());
        string source = WgslWriter.Write(module); ModuleValidator.Validate(WgslReader.Parse(source));
        if (execution == 3) { Assert.Contains("subgroupBarrier();", source); Assert.DoesNotContain("workgroupBarrier();", source); }
        else Assert.DoesNotContain("subgroupBarrier();", source);
    }

    [Theory]
    [InlineData(3u, 2u, 264u, false)] [InlineData(3u, 3u, 72u, false)] [InlineData(2u, 1u, 72u, false)]
    [InlineData(2u, 5u, 72u, true)] [InlineData(2u, 1u, 24648u, true)] [InlineData(2u, 2u, 272u, false)]
    public void WgslCannotNarrowNativeBarrierRequirements(uint execution, uint scope, uint semantics, bool vulkan)
    {
        var module = SpirvReader.Parse(Fixture(true, execution, scope, semantics, vulkan).ToBytes());
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
    }

    [Theory]
    [InlineData(true, 2u, 2u, 256u, false)] [InlineData(false, 0u, 2u, 0u, false)]
    [InlineData(true, 2u, 2u, 262u, false)] [InlineData(true, 2u, 2u, 32776u, true)]
    [InlineData(true, 2u, 5u, 264u, false)] [InlineData(true, 2u, 4u, 264u, false)]
    [InlineData(true, 1u, 2u, 264u, false)] [InlineData(true, 2u, 2u, 272u, true)]
    [InlineData(true, 2u, 2u, 8192u, true)] [InlineData(false, 0u, 1u, 8u, false)]
    [InlineData(true, 3u, 2u, 8u, false)] [InlineData(false, 0u, 3u, 136u, false)]
    public void InvalidBarrierRequirementsAreRejected(bool control, uint execution, uint scope, uint semantics, bool vulkan)
    {
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(SpirvReader.Parse(Fixture(control, execution, scope, semantics, vulkan).ToBytes())));
    }

    [Theory]
    [InlineData(1u)] [InlineData(4u)]
    public void ConditionalMemoryFenceDoesNotIntroduceAnUnprovenCollectiveWait(uint size)
    {
        var module = SpirvReader.Parse(Fixture(false, 0, 2, 264, size: size, conditional: true).ToBytes());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.MemoryBarrier);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.ControlBarrier);
        if (size == 1) ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        else Assert.Contains("uniformity-proven", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FenceHelpersAccountForEveryReachableEntry(bool shared)
    {
        var module = WgslReader.Parse("fn fence()->u32 {workgroupBarrier();return 1u;} @compute @workgroup_size(1) fn one(){_=fence();} @compute @workgroup_size(4) fn many(){" + (shared ? "_=fence();" : "") + "}");
        var helper = module.Functions.Single(f => f.Name == "fence");
        helper.Body.Statements[0] = new Statement.MemoryBarrier(false, true) { NativeMemory = new(2, 264) };
        if (shared) Assert.Contains("uniformity-proven", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
        else ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        Assert.IsType<Statement.MemoryBarrier>(helper.Body.Statements[0]);
    }

    [Theory]
    [InlineData(1d)] [InlineData(4d)]
    public void PendingWorkgroupSizeDoesNotUseAnOverrideDefaultAsProof(double size)
    {
        var module = WgslReader.Parse("override n=1u; @compute @workgroup_size(n) fn main(){workgroupBarrier();}");
        module.Functions[0].Body.Statements[0] = new Statement.MemoryBarrier(false, true) { NativeMemory = new(2, 264) };
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["n"] = size });
        if (size == 1) ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(resolved)));
        else Assert.Throws<ShaderException>(() => WgslWriter.Write(resolved));
    }

    [Theory]
    [InlineData("storageBarrier", 2u, 72u)] [InlineData("workgroupBarrier", 2u, 264u)]
    [InlineData("textureBarrier", 2u, 2056u)] [InlineData("subgroupBarrier", 3u, 264u)]
    public void WgslBarriersUseTheirSpecifiedMemoryScope(string operation, uint scope, uint semantics)
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("@compute @workgroup_size(4) fn main(){" + operation + "();}"));
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.Equal(new[] { scope, scope, semantics }, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.ControlBarrier).Operands.Select(id => constants[id]));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
    }

    [Theory]
    [InlineData(false, 1u)] [InlineData(true, 1u)] [InlineData(false, 2u)] [InlineData(true, 2u)]
    public void NativeDeviceFencesAreAvailableInGraphicsStagesButWorkgroupScopeIsNot(bool vertex, uint scope)
    {
        var module = WgslReader.Parse(vertex ? "@vertex fn main()->@builtin(position) vec4f{return vec4f();}" : "@fragment fn main(){}");
        module.Functions[0].Body.Statements.Insert(0, new Statement.MemoryBarrier(true, false) { NativeMemory = new(scope, 72) });
        if (scope == 2) Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
        else
        {
            var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
            Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.MemoryBarrier);
            Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.ControlBarrier);
            ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
            Assert.Contains("uniformity-proven", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DefaultFenceMemoryScopeCannotBeAmbiguousOrUsedInAnUnavailableStage(bool mixed)
    {
        var module = WgslReader.Parse(mixed ? "@compute @workgroup_size(1) fn main(){}" : "@fragment fn main(){}");
        module.Functions[0].Body.Statements.Add(new Statement.MemoryBarrier(true, false, Subgroup: mixed));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }
}
