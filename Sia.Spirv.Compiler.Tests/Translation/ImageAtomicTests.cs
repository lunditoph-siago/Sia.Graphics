using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using static Sia.Spirv.Compiler.Translation.Tests.SpirvTests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class ImageAtomicTests
{
    [Theory]
    [InlineData("r32uint", "1u", "textureAtomicAdd", Op.AtomicIAdd)]
    [InlineData("r32uint", "1u", "textureAtomicMin", Op.AtomicUMin)]
    [InlineData("r32uint", "1u", "textureAtomicMax", Op.AtomicUMax)]
    [InlineData("r32uint", "1u", "textureAtomicAnd", Op.AtomicAnd)]
    [InlineData("r32uint", "1u", "textureAtomicOr", Op.AtomicOr)]
    [InlineData("r32uint", "1u", "textureAtomicXor", Op.AtomicXor)]
    [InlineData("r32sint", "-1i", "textureAtomicAdd", Op.AtomicIAdd)]
    [InlineData("r32sint", "-1i", "textureAtomicMin", Op.AtomicSMin)]
    [InlineData("r32sint", "-1i", "textureAtomicMax", Op.AtomicSMax)]
    [InlineData("r32sint", "-1i", "textureAtomicAnd", Op.AtomicAnd)]
    [InlineData("r32sint", "-1i", "textureAtomicOr", Op.AtomicOr)]
    [InlineData("r32sint", "-1i", "textureAtomicXor", Op.AtomicXor)]
    [InlineData("r64uint", "1lu", "textureAtomicMin", Op.AtomicUMin)]
    [InlineData("r64uint", "1lu", "textureAtomicMax", Op.AtomicUMax)]
    public void StorageImageOperationsPreserveTypeAndAccess(string format, string value, string operation, object expected)
    {
        string source = $"@group(0) @binding(0) var image: texture_storage_2d<{format}, atomic>; @compute @workgroup_size(1) fn main() {{ {operation}(image, vec2i(0), {value}); }}";
        byte[] bytes = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default); var binary = SpirvBinary.Parse(bytes);
        var pointer = binary.Instructions.Single(i => (Op)i.Opcode == Op.ImageTexelPointer);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.TypePointer && i.Operands[0] == pointer.Operands[0] && i.Operands[1] == 11);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == (Op)expected && i.Operands[2] == pointer.Operands[1]);
        if (format == "r64uint")
        {
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5016);
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 12);
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Extension && SpirvBinary.ReadString(i.Operands, out _) == "SPV_EXT_shader_image_int64");
        }
        var module = SpirvReader.Parse(bytes); ModuleValidator.Validate(module);
        Assert.Equal(StorageAccess.ReadWrite | StorageAccess.Atomic, Assert.IsType<ShaderType.Image>(Assert.Single(module.Globals).Type).Access);
        string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default); Assert.Contains(operation + "(", wgsl); Assert.Contains(format + ", atomic>", wgsl);
        ModuleValidator.Validate(SpirvReader.Parse(ShaderTranslator.WgslToSpirv(wgsl, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("1d", "1i")][InlineData("2d", "vec2u(1u)")]
    [InlineData("2d_array", "vec2i(1), 1lu")][InlineData("3d", "vec3i(1)")]
    public void DimensionsAndMixedWidthArrayIndicesRoundtrip(string dimension, string coordinates)
    {
        string source = $"@group(0) @binding(0) var image: texture_storage_{dimension}<r32uint, atomic>; @compute @workgroup_size(1) fn main() {{ textureAtomicAdd(image, {coordinates}, 1u); }}";
        byte[] bytes = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        string wgsl = ShaderTranslator.SpirvToWgsl(bytes, SpirvCompilationTarget.Default);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
        if (dimension == "2d_array")
        {
            Assert.Contains(SpirvBinary.Parse(bytes).Instructions, i => (Op)i.Opcode == Op.UConvert);
            Assert.DoesNotContain("u32(1lu)", wgsl);
        }
    }

    [Fact]
    public void BindingArrayIndexIsPreservedInTexelPointer()
    {
        const string source = "enable wgpu_binding_array; @group(0) @binding(0) var images: binding_array<texture_storage_2d<r32uint, atomic>, 2>; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) index: u32) { textureAtomicAdd(images[index], vec2i(0), 1u); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        var texel = binary.Instructions.Single(i => (Op)i.Opcode == Op.ImageTexelPointer);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == texel.Operands[2]);
        var module = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(module);
        Assert.Equal(StorageAccess.ReadWrite | StorageAccess.Atomic, Assert.IsType<ShaderType.Image>(Assert.IsType<ShaderType.BindingArray>(Assert.Single(module.Globals, g => g.Space == AddressSpace.Handle).Type).Element).Access);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("r32uint", "read_write", "textureAtomicAdd", "vec2i(0), 1u")]
    [InlineData("r32float", "atomic", "textureAtomicAdd", "vec2i(0), 1.0f")]
    [InlineData("rgba32uint", "atomic", "textureAtomicMin", "vec2i(0), 1u")]
    [InlineData("r64uint", "atomic", "textureAtomicAdd", "vec2i(0), 1lu")]
    [InlineData("r64uint", "atomic", "textureAtomicAnd", "vec2i(0), 1lu")]
    [InlineData("r32uint", "atomic", "textureAtomicSub", "vec2i(0), 1u")]
    [InlineData("r32uint", "atomic", "textureAtomicExchange", "vec2i(0), 1u")]
    [InlineData("r32uint", "atomic", "textureAtomicAdd", "vec3i(0), 1u")]
    [InlineData("r32uint", "atomic", "textureAtomicAdd", "vec2f(0), 1u")]
    [InlineData("r32uint", "atomic", "textureAtomicAdd", "vec2i(0), 1i")]
    [InlineData("r32uint", "atomic", "textureAtomicAdd", "vec2i(0), 1")]
    [InlineData("r32sint", "atomic", "textureAtomicAdd", "vec2i(0), 1")]
    public void UnsupportedImageAtomicsAreRejected(string format, string access, string operation, string arguments)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($"@group(0) @binding(0) var image: texture_storage_2d<{format}, {access}>; @compute @workgroup_size(1) fn main() {{ {operation}(image, {arguments}); }}", SpirvCompilationTarget.Default));
    }

    [Fact]
    public void AtomicAccessAlsoSupportsBufferAtomicsAndTextureLoadStore()
    {
        const string source = "@group(0) @binding(0) var<storage, atomic> data: atomic<u32>; @group(0) @binding(1) var image: texture_storage_2d<r32uint, atomic>; @compute @workgroup_size(1) fn main() { atomicAdd(&data, 1u); let v = textureLoad(image, vec2i(0)); textureStore(image, vec2i(0), v); }";
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default), SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("fn helper(t: texture_storage_2d<r32uint, atomic>) { textureAtomicAdd(t, vec2i(0), 1u); }")]
    [InlineData("@compute @workgroup_size(1) fn main() { let value = textureAtomicAdd(image, vec2i(0), 1u); }")]
    public void TextureParametersAndValueReturningCallsAreRejected(string function)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv("@group(0) @binding(0) var image: texture_storage_2d<r32uint, atomic>; " + function, SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData(0ul, 0u)][InlineData(4294967296ul, 0u)][InlineData(ulong.MaxValue, uint.MaxValue)]
    public void ConstantWidthConversionsWriteConcreteValues(ulong input, uint expected)
    {
        var module = new Module(); var function = new ShaderFunction("convert") { ReturnType = ShaderType.U32 };
        function.Body.Statements.Add(new Statement.Return(new Expression.Convert(ShaderType.U32, new Expression.Literal(input, new ShaderType.Scalar(ScalarKind.Uint, 8)))));
        module.Functions.Add(function);
        string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default); Assert.Contains($"return {expected}u;", wgsl);
        var parsed = WgslReader.Parse(wgsl); ModuleValidator.Validate(parsed);
        Assert.Equal(expected, Assert.IsType<Expression.Literal>(Assert.IsType<Statement.Return>(Assert.Single(Assert.Single(parsed.Functions).Body.Statements)).Value).Value);
    }

    private static SpirvBinary Input(params SpirvInstruction[] tail) => Binary(
        new[] {
            I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), Entry(20), I(Op.ExecutionMode, 20, 17, 1, 1, 1),
            I(Op.Decorate, 10, 34, 0), I(Op.Decorate, 10, 33, 0),
            I(Op.TypeVoid, 1), I(Op.TypeInt, 2, 32, 0), I(Op.TypeVector, 3, 2, 2),
            I(Op.TypeImage, 4, 2, 1, 0, 0, 0, 2, 33), I(Op.TypePointer, 5, 0, 4), I(Op.TypePointer, 6, 11, 2), I(Op.TypeFunction, 7, 1),
            I(Op.Variable, 5, 10, 0), I(Op.Constant, 2, 11, 0), I(Op.Constant, 2, 12, 1), I(Op.ConstantComposite, 3, 13, 11, 11),
            I(Op.Function, 1, 20, 0, 7), I(Op.Label, 21), I(Op.ImageTexelPointer, 6, 22, 10, 13, 11),
        }.Concat(tail).Concat(new[] { I(Op.Return), I(Op.FunctionEnd) }).ToArray());

    [Fact]
    public void IndependentSpirvInputAndCopiedTexelPointersAreAccepted()
    {
        var input = Input(I(Op.CopyObject, 6, 23, 22), I(Op.AtomicUMax, 2, 24, 23, 12, 11, 12));
        string wgsl = ShaderTranslator.SpirvToWgsl(input.ToBytes(), SpirvCompilationTarget.Default); Assert.Contains("textureAtomicMax", wgsl);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
    }

    internal static SpirvBinary MemoryFixture(uint scope, uint semantics, bool vulkan)
    {
        var input = Input(I(Op.AtomicUMax, 2, 24, 22, 14, 15, 12));
        var instructions = input.Instructions.ToList();
        int function = instructions.FindIndex(i => (Op)i.Opcode == Op.Function);
        instructions.InsertRange(function, [I(Op.Constant, 2, 14, scope), I(Op.Constant, 2, 15, semantics)]);
        if (vulkan)
        {
            int memory = instructions.FindIndex(i => (Op)i.Opcode == Op.MemoryModel);
            instructions[memory] = I(Op.MemoryModel, 0, 3);
            instructions.InsertRange(1, [I(Op.Capability, 5345), I(Op.Extension, SpirvBinary.StringWords("SPV_KHR_vulkan_memory_model"))]);
        }
        return new() { Bound = input.Bound, Instructions = instructions };
    }

    [Theory]
    [InlineData(3u, 0u, false)] [InlineData(1u, 2056u, false)] [InlineData(5u, 0u, true)]
    public void ImageAtomicScopeAndOrderingAreRetained(uint scope, uint semantics, bool vulkan)
    {
        var module = SpirvReader.Parse(MemoryFixture(scope, semantics, vulkan).ToBytes()); ModuleValidator.Validate(module);
        var native = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        var operation = Assert.Single(native.Instructions, i => (Op)i.Opcode == Op.AtomicUMax);
        var constants = native.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.Equal(scope, constants[operation.Operands[3]]); Assert.Equal(semantics, constants[operation.Operands[4]]);
        Assert.Equal(vulkan, SpirvReader.Parse(native.ToBytes()).VulkanMemoryModel);
        if (semantics == 0) ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        else Assert.Contains("no equivalent WGSL relaxed builtin", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
    }

    [Theory]
    [InlineData(Op.AtomicISub)][InlineData(Op.AtomicExchange)][InlineData(Op.AtomicSMin)]
    public void UnsupportedOrMistypedSpirvAtomicOpcodesAreRejected(object operation)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.SpirvToWgsl(Input(I((Op)operation, 2, 24, 22, 12, 11, 12)).ToBytes(), SpirvCompilationTarget.Default));
    }

    [Fact]
    public void ConsumedSpirvOldValueIsRejectedInsteadOfBeingDiscarded()
    {
        var input = Input(I(Op.AtomicUMax, 2, 24, 22, 12, 11, 12), I(Op.IAdd, 2, 25, 24, 12));
        var error = Assert.Throws<ShaderException>(() => ShaderTranslator.SpirvToWgsl(input.ToBytes(), SpirvCompilationTarget.Default));
        Assert.Contains("result is used", error.Message);
    }
}
