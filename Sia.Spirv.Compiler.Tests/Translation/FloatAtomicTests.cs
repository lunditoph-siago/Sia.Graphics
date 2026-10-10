using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class FloatAtomicTests
{
    private const string Source = """
        struct Data { scalar: atomic<f32>, elements: array<atomic<f32>, 2>, }
        @group(0) @binding(0) var<storage, read_write> data: Data;
        @compute @workgroup_size(1) fn main() {
            atomicStore(&data.scalar, 1.5);
            let loaded = atomicLoad(&data.scalar);
            let added = atomicAdd(&data.elements[0], loaded);
            let subtracted = atomicSub(&data.elements[1], added);
            let exchanged = atomicExchange(&data.scalar, subtracted);
        }
        """;

    [Fact]
    public void FloatAtomicsRoundtripThroughNestedStoragePaths()
    {
        byte[] bytes = ShaderTranslator.WgslToSpirv(Source, SpirvCompilationTarget.Default);
        var binary = SpirvBinary.Parse(bytes);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 6033);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Extension && SpirvBinary.ReadString(i.Operands, out _) == "SPV_EXT_shader_atomic_float_add");
        Assert.Equal(2, binary.Instructions.Count(i => (Op)i.Opcode == Op.AtomicFAddEXT));
        var subtract = binary.Instructions.Single(i => (Op)i.Opcode == Op.FNegate);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.AtomicFAddEXT && i.Operands[5] == subtract.Operands[1]);
        foreach (Op op in new[] { Op.AtomicStore, Op.AtomicLoad, Op.AtomicExchange }) Assert.Contains(binary.Instructions, i => (Op)i.Opcode == op);
        string wgsl = ShaderTranslator.SpirvToWgsl(bytes, SpirvCompilationTarget.Default);
        Assert.Contains("atomic<f32>", wgsl);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
        ModuleValidator.Validate(SpirvReader.Parse(ShaderTranslator.WgslToSpirv(wgsl, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("atomicLoad(&data)")]
    [InlineData("atomicStore(&data, 1.5)")]
    public void WorkgroupLoadAndStoreRemainSupported(string operation)
    {
        byte[] bytes = ShaderTranslator.WgslToSpirv($"var<workgroup> data: atomic<f32>; @compute @workgroup_size(1) fn main() {{ {operation}; }}", SpirvCompilationTarget.Default);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(bytes, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("storage, read_write", "atomicMin", "1.5")]
    [InlineData("storage, read_write", "atomicMax", "1.5")]
    [InlineData("storage, read_write", "atomicAnd", "1.5")]
    [InlineData("storage, read_write", "atomicOr", "1.5")]
    [InlineData("storage, read_write", "atomicXor", "1.5")]
    [InlineData("storage, read_write", "atomicCompareExchangeWeak", "1.5, 2.0")]
    [InlineData("workgroup", "atomicAdd", "1.5")]
    [InlineData("workgroup", "atomicSub", "1.5")]
    [InlineData("workgroup", "atomicExchange", "1.5")]
    public void UnsupportedFloatOperationsAndSpacesAreRejected(string space, string operation, string values)
    {
        string binding = space.StartsWith("storage", StringComparison.Ordinal) ? "@group(0) @binding(0) " : "";
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($"{binding}var<{space}> data: atomic<f32>; @compute @workgroup_size(1) fn main() {{ {operation}(&data, {values}); }}", SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData("enable f16;", "f16")]
    [InlineData("", "f64")]
    [InlineData("enable wgpu_int16;", "i16")]
    [InlineData("enable wgpu_int16;", "u16")]
    public void UnsupportedAtomicWidthsAreRejected(string directive, string type)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($"{directive} @group(0) @binding(0) var<storage, read_write> data: atomic<{type}>;", SpirvCompilationTarget.Default));
        var module = new Module();
        var component = type[0] == 'f' ? new ShaderType.Scalar(ScalarKind.Float, type == "f64" ? 8 : 2) : new ShaderType.Scalar(type[0] == 'i' ? ScalarKind.Sint : ScalarKind.Uint, 2);
        module.Globals.Add(new("data", new ShaderType.Atomic(component), AddressSpace.Storage, Binding: new(0, 0)));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }

    [Fact]
    public void IntegerOpcodeWithFloatOperandsIsRejected()
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(Source, SpirvCompilationTarget.Default));
        var invalid = new SpirvBinary { Bound = binary.Bound, Instructions = binary.Instructions.Select(i => (Op)i.Opcode == Op.AtomicFAddEXT ? i with { Opcode = (ushort)Op.AtomicIAdd } : i).ToArray() };
        Assert.Throws<ShaderException>(() => SpirvReader.Parse(invalid.ToBytes()));
    }
}
