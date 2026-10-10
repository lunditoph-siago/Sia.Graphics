using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using static Sia.Spirv.Compiler.Translation.Tests.SpirvTests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class MemoryAccessTests
{
    internal static uint[] Operands(uint flags, uint scopeId = 15) => new[] { flags }
        .Concat((flags & 2) != 0 ? new uint[] { 4 } : [])
        .Concat((flags & 8) != 0 ? new[] { scopeId } : [])
        .Concat((flags & 16) != 0 ? new[] { scopeId } : []).ToArray();

    internal static SpirvBinary Fixture(uint loadFlags, uint storeFlags, bool vulkan = false, bool copy = false, bool separate = false, uint scope = 5)
    {
        var input = BarrierMemoryTests.Fixture(false, 0, 2, 264, vulkan);
        var code = new List<SpirvInstruction>();
        foreach (var i in input.Instructions)
        {
            Op op = (Op)i.Opcode; var a = i.Operands;
            if (op == Op.MemoryBarrier) continue;
            if (op == Op.TypeFunction) { code.Add(i); code.Add(I(Op.Constant, 2, 15, scope)); continue; }
            if (op == Op.EntryPoint && separate) { code.Add(I(op, a.Concat(new uint[] { 20, 21 }).ToArray())); continue; }
            if (op == Op.Load && a[2] == 33)
            {
                if (!copy) code.Add(I(op, a.Concat(Operands(loadFlags)).ToArray()));
                continue;
            }
            if (copy && op == Op.IAdd) continue;
            if (op == Op.Store && a[0] == 36)
            {
                code.Add(copy ? I(Op.CopyMemory, new uint[] { 36, 33 }.Concat(Operands(storeFlags))
                    .Concat(separate ? Operands(loadFlags) : []).ToArray()) : I(op, a.Concat(Operands(storeFlags)).ToArray()));
                continue;
            }
            code.Add(i);
        }
        if (vulkan && scope == 1) code.Insert(2, I(Op.Capability, 5346));
        return new() { Version = separate ? 0x10400u : input.Version, Bound = input.Bound, Instructions = code };
    }

    private static uint[] Access(SpirvBinary binary, SpirvInstruction instruction, int start)
    {
        var operands = instruction.Operands[start..].ToArray();
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        int index = 1 + ((operands[0] & 2) != 0 ? 1 : 0);
        if ((operands[0] & 8) != 0) { operands[index] = constants[operands[index]]; index++; }
        if ((operands[0] & 16) != 0) operands[index] = constants[operands[index]];
        return operands;
    }

    [Theory]
    [InlineData(0u, 0u, false)] [InlineData(1u, 1u, false)] [InlineData(2u, 2u, false)]
    [InlineData(4u, 4u, false)] [InlineData(7u, 7u, false)] [InlineData(32u, 32u, true)]
    [InlineData(48u, 40u, true)] [InlineData(55u, 47u, true)]
    public void OrdinaryAccessOperandsSurviveCloningAndNativeEmission(uint loadFlags, uint storeFlags, bool vulkan)
    {
        var module = SpirvReader.Parse(Fixture(loadFlags, storeFlags, vulkan).ToBytes()); ModuleValidator.Validate(module);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double>());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(resolved, SpirvCompilationTarget.Default));
        Assert.Equal(Operands(loadFlags, 5), Access(binary, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3), 3));
        Assert.Equal(Operands(storeFlags, 5), Access(binary, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2), 2));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        if ((loadFlags & (8 | 16)) == 0 && (storeFlags & (8 | 16)) == 0 || vulkan)
        {
            string text = WgslWriter.Write(module, SpirvCompilationTarget.Default); ModuleValidator.Validate(WgslReader.Parse(text));
            Assert.Equal((loadFlags & 1) != 0 || (storeFlags & 1) != 0, text.Contains("@volatile"));
            Assert.Equal(((loadFlags | storeFlags) & 24) != 0, text.Contains("@coherent"));
        }
        else Assert.Contains("availability/visibility", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
    }

    [Theory]
    [InlineData(1u, false)] [InlineData(7u, false)] [InlineData(32u, true)] [InlineData(56u, true)]
    public void OneCopyMaskIsSplitBetweenReadAndWrite(uint flags, bool vulkan)
    {
        var module = SpirvReader.Parse(Fixture(flags, flags, vulkan, copy: true).ToBytes());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(Operands(flags & ~8u, 5), Access(binary, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3), 3));
        Assert.Equal(Operands(flags & ~16u, 5), Access(binary, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2), 2));
    }

    [Theory]
    [InlineData(48u, 40u)] [InlineData(55u, 47u)] [InlineData(0u, 1u)] [InlineData(1u, 0u)]
    public void CopyMasksKeepSourceAndTargetRequirementsSeparate(uint loadFlags, uint storeFlags)
    {
        var module = SpirvReader.Parse(Fixture(loadFlags, storeFlags, true, copy: true, separate: true).ToBytes());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(Operands(loadFlags, 5), Access(binary, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3), 3));
        Assert.Equal(Operands(storeFlags, 5), Access(binary, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2), 2));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AtomicContainingAggregateCopiesKeepVolatileOnEveryRealMemoryLeaf(int mode)
    {
        var input = AtomicMemoryTests.AggregateFixture(mode);
        var code = input.Instructions.Select(i => (Op)i.Opcode is Op.Load or Op.Store or Op.CopyMemory
            ? I((Op)i.Opcode, i.Operands.Concat(new uint[] { 3, 4 }).ToArray()) : i).ToArray();
        var module = SpirvReader.Parse(new SpirvBinary { Bound = input.Bound, Instructions = code }.ToBytes());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(PipelineConstantResolver.Resolve(module, new Dictionary<string, double>()), SpirvCompilationTarget.Default));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.AtomicIAdd);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode is Op.AtomicLoad or Op.AtomicStore);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 1);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && i.Operands[2] == 1);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(7u)] [InlineData(32u)] [InlineData(48u)]
    public void CooperativeAccessesKeepTheirNativeOperands(uint flags)
    {
        var input = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(CooperativeMatrixTests.Source, SpirvCompilationTarget.Default));
        uint scopeId = input.Instructions.First(i => (Op)i.Opcode == Op.Constant && i.Operands[2] == 3).Operands[1];
        var code = input.Instructions.Select(i => (Op)i.Opcode == Op.CooperativeMatrixLoadKHR ? I((Op)i.Opcode, i.Operands.Concat(Operands(flags, scopeId)).ToArray())
            : (Op)i.Opcode == Op.CooperativeMatrixStoreKHR ? I((Op)i.Opcode, i.Operands.Concat(Operands((flags & ~16u) | ((flags & 16) != 0 ? 8u : 0u), scopeId)).ToArray()) : i).ToArray();
        var module = SpirvReader.Parse(new SpirvBinary { Version = input.Version, Bound = input.Bound, Instructions = code }.ToBytes());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(PipelineConstantResolver.Resolve(module, new Dictionary<string, double>()), SpirvCompilationTarget.Default));
        Assert.All(binary.Instructions.Where(i => (Op)i.Opcode == Op.CooperativeMatrixLoadKHR), i => Assert.Equal(Operands(flags, 3), Access(binary, i, 5)));
        if ((flags & 16) != 0) Assert.Contains("availability/visibility", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
        else ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData(8u, 0u, true)] [InlineData(0u, 16u, true)] [InlineData(16u, 0u, true)]
    [InlineData(32u, 32u, false)] [InlineData(64u, 0u, false)]
    public void InvalidPerAccessRequirementsCannotBeIgnored(uint loadFlags, uint storeFlags, bool vulkan)
    {
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(SpirvReader.Parse(Fixture(loadFlags, storeFlags, vulkan).ToBytes())));
    }

    [Fact]
    public void VolatileWorkgroupAccessReportsItsMissingWgslRepresentation()
    {
        var module = WgslReader.Parse("var<workgroup> x:u32; @compute @workgroup_size(1) fn main(){x=1u;}");
        var store = Assert.IsType<Statement.Store>(module.Functions[0].Body.Statements[0]);
        module.Functions[0].Body.Statements[0] = store with { MemoryAccess = new(1) };
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        Assert.Contains("proven WGSL storage-buffer root", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void VulkanGlobalVolatileLowersToAccessFlagsIncludingAtomics(bool atomic, bool alias)
    {
        string source = atomic ? "@volatile @group(0) @binding(0) var<storage,read_write> x:atomic<u32>; @compute @workgroup_size(1) fn main(){_=atomicAdd(&x,1u);_=atomicCompareExchangeWeak(&x,2u,3u);}"
            : "@volatile @group(0) @binding(0) var<storage,read_write> x:u32; @compute @workgroup_size(1) fn main(){x=x+1u;}";
        if (alias) source = atomic ? source.Replace("fn main(){", "fn main(){let p=&x;").Replace("atomicAdd(&x,", "atomicAdd(p,").Replace("atomicCompareExchangeWeak(&x,", "atomicCompareExchangeWeak(p,")
            : source.Replace("fn main(){x=x+1u;}", "fn main(){let p=&x;*p=*p+1u;}");
        var module = WgslReader.Parse(source); module.VulkanMemoryModel = true;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 21);
        if (atomic)
        {
            var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
            Assert.All(binary.Instructions.Where(i => (Op)i.Opcode is Op.AtomicIAdd or Op.AtomicCompareExchange), i => Assert.Equal(32768u, constants[i.Operands[4]]));
            var compare = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.AtomicCompareExchange);
            Assert.Equal(32768u, constants[compare.Operands[5]]);
        }
        else
        {
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 1);
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && i.Operands[2] == 1);
        }
        var back = SpirvReader.Parse(binary.ToBytes());
        if (atomic) Assert.Contains("volatile strong compare/exchange", Assert.Throws<ShaderException>(() => WgslWriter.Write(back, SpirvCompilationTarget.Default)).Message);
        else
        {
            var text = WgslWriter.Write(back, SpirvCompilationTarget.Default); Assert.Contains("@volatile", text);
            ModuleValidator.Validate(WgslReader.Parse(text));
        }
    }

    [Theory]
    [InlineData(0u)] [InlineData(3u)]
    public void InvalidAlignmentAndMalformedTailsAreRejected(uint alignment)
    {
        var input = Fixture(2, 0);
        var code = input.Instructions.Select(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3
            ? I(Op.Load, i.Operands.Take(4).Concat(new[] { alignment }).ToArray()) : i).ToArray();
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(SpirvReader.Parse(new SpirvBinary { Bound = input.Bound, Instructions = code }.ToBytes())));
        var missing = code.Select(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 ? I(Op.Load, i.Operands.Take(4).ToArray()) : i).ToArray();
        Assert.Contains("Missing per-access", Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Bound = input.Bound, Instructions = missing }.ToBytes())).Message);
    }

    [Fact]
    public void CopyWithTwoMasksRequiresItsNativeVersion()
    {
        var input = Fixture(1, 1, copy: true, separate: true);
        Assert.Contains("1.4", Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = 0x10300, Bound = input.Bound, Instructions = input.Instructions }.ToBytes())).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ShadowedPointerParameterIsNotMistakenForAStorageBinding(bool store)
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> memory:u32; var<private> backing:u32; @compute @workgroup_size(1) fn main(){}");
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Private);
        var helper = new ShaderFunction("helper") { ReturnType = new ShaderType.Void() };
        helper.Arguments.Add(new("memory", pointer));
        var parameter = new Expression.Unary("*", new Expression.Reference("memory", pointer), pointer);
        helper.Body.Statements.Add(store ? new Statement.Store(parameter, Expression.U32(1)) { MemoryAccess = new(1) }
            : new Statement.Declare("value", ShaderType.U32, new Expression.Load(parameter) { MemoryAccess = new(1) }, false));
        module.Functions.Add(helper);
        var global = new Expression.Reference("backing", pointer);
        module.Functions[0].Body.Statements.Add(new Statement.Evaluate(new Expression.Call("helper", [new Expression.Unary("&", global, pointer)], new ShaderType.Void(), CallBinding.Function)));
        ModuleValidator.Validate(module);
        Assert.Contains("proven WGSL storage-buffer root", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
        Assert.Equal(MemoryDecorations.None, module.Globals[0].MemoryDecorations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VolatileStrongCompareCannotIntroduceRetryAccesses(bool vulkan)
    {
        var input = AtomicMemoryTests.Fixture(Op.AtomicCompareExchange, 1, vulkan ? 32768u : 0u, vulkan ? 32768u : 0u, vulkan);
        var code = input.Instructions.ToList();
        if (!vulkan) code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeVoid), I(Op.Decorate, 20, 21));
        var module = SpirvReader.Parse(new SpirvBinary { Bound = input.Bound, Instructions = code }.ToBytes());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.AtomicCompareExchange);
        Assert.Contains("cannot be duplicated", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
    }

    [Theory]
    [InlineData(21u, false)] [InlineData(23u, false)] [InlineData(21u, true)] [InlineData(23u, true)]
    public void VulkanInputCannotUseLegacyVolatileOrCoherentDecorations(uint decoration, bool member)
    {
        var input = Fixture(0, 0, true); var code = input.Instructions.ToList();
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeVoid), member ? I(Op.MemberDecorate, 6, 0, decoration) : I(Op.Decorate, 20, decoration));
        Assert.Contains("banned with the Vulkan", Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Bound = input.Bound, Instructions = code }.ToBytes())).Message);
    }
}
