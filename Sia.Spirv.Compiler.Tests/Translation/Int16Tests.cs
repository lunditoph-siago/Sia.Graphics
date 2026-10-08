using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class Int16Tests
{
    [Theory]
    [InlineData("i16(32767)", 32767)][InlineData("i16(-32768)", -32768)]
    [InlineData("u16(65535)", 65535)][InlineData("u16(-1i)", 65535)]
    [InlineData("i16(65535u)", -1)][InlineData("i16(f16(33000.0))", 32752)]
    [InlineData("i16(33000.0f)", 32767)][InlineData("u16(70000.0f)", 65535)]
    [InlineData("u16(-10.0f)", 0)][InlineData("countLeadingZeros(u16(1))", 15)]
    [InlineData("countLeadingZeros(u16(0))", 16)][InlineData("firstLeadingBit(u16(0))", 65535)]
    [InlineData("firstLeadingBit(i16(-1))", -1)][InlineData("reverseBits(u16(1))", 32768)]
    public void ConstantCastsAndBitOperationsRespectSixteenBits(string expression, long expected)
    {
        var module = WgslReader.Parse($"enable wgpu_int16; enable f16; const value = {expression};");
        ModuleValidator.Validate(module);
        Assert.Equal(expected, System.Convert.ToInt64(Assert.IsType<Expression.Literal>(module.Constants[0].Value).Value));
    }

    [Fact]
    public void ScalarVectorStorageAndSubgroupOperationsRoundtrip()
    {
        const string source = "enable wgpu_int16; struct Data { signed: i16, unsigned: u16, vector: vec2<u16>, } @group(0) @binding(0) var<storage, read_write> data: Data; @compute @workgroup_size(1) fn main(@builtin(subgroup_invocation_id) lane: u32) { var s = i16(lane); s = s / i16(-1); s = subgroupAdd(s); data.signed = s; data.unsigned = u16(lane); data.vector = vec2<u16>(u16(1), u16(65535)); }";
        byte[] bytes = ShaderTranslator.WgslToSpirv(source);
        var binary = SpirvBinary.Parse(bytes);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 22);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Constant && i.Operands[^1] == 0xffff8000u);
        string output = ShaderTranslator.SpirvToWgsl(bytes);
        Assert.Contains("enable wgpu_int16;", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Fact]
    public void PackedBitcastsPreserveSignedComponents()
    {
        const string source = "enable wgpu_int16; const packed = bitcast<u32>(vec2<i16>(i16(-1), i16(2))); const unpacked = bitcast<vec2<u16>>(0x8000ffffu);";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        Assert.Equal(0x0002ffffu, Assert.IsType<Expression.Literal>(module.Constants[0].Value).Value);
        var vector = Assert.IsType<Expression.Construct>(module.Constants[1].Value);
        Assert.Equal((ushort)65535, Assert.IsType<Expression.Literal>(vector.Components[0]).Value);
        Assert.Equal((ushort)32768, Assert.IsType<Expression.Literal>(vector.Components[1]).Value);
    }

    [Fact]
    public void NarrowPipelineValuesAreRangeChecked()
    {
        var module = WgslReader.Parse("enable wgpu_int16; override value: u16; @compute @workgroup_size(1) fn main() { let x = value; }");
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["value"] = 65535.9 });
        Assert.Equal((ushort)65535, Assert.IsType<Expression.Literal>(resolved.Constants[0].Value).Value);
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["value"] = 65536 }));
    }

    [Theory]
    [InlineData("i16")][InlineData("u16")][InlineData("i64")][InlineData("u64")]
    public void Non32BitFindInstructionsUseCoreBinarySearch(string type)
    {
        string enable = type.EndsWith("16", StringComparison.Ordinal) ? "enable wgpu_int16;" : "";
        string source = $"{enable} @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) index: u32) {{ let value = vec2<{type}>({type}(index)); let a = countLeadingZeros(value); let b = countTrailingZeros(value); let c = firstLeadingBit(value); let d = firstTrailingBit(value); let e = countOneBits(value); let f = reverseBits(value); }}";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.ExtInst && i.Operands[3] is 73 or 74 or 75);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ShiftRightLogical);
        var wordTypes = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypeInt && i.Operands[1] == 32).Select(i => i.Operands[0]).ToHashSet();
        wordTypes.UnionWith(binary.Instructions.Where(i => (Op)i.Opcode == Op.TypeVector && wordTypes.Contains(i.Operands[1])).Select(i => i.Operands[0]).ToArray());
        Assert.All(binary.Instructions.Where(i => (Op)i.Opcode is Op.BitCount or Op.BitReverse), i => Assert.Contains(i.Operands[0], wordTypes));
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void WgslRepackingHelpersPreserveSingleArgumentEvaluationAndAvoidNames()
    {
        const string source = "enable wgpu_int16; var<private> counter: u32; fn next() -> vec2<u16> { counter++; return vec2<u16>(u16(counter)); } fn naga_bitcast_0() {} @compute @workgroup_size(1) fn main() { let packed = bitcast<u32>(next()); let pair = bitcast<vec2<u16>>(packed); }";
        string output = WgslWriter.Write(WgslReader.Parse(source));
        Assert.Contains("fn naga_bitcast_1(", output);
        Assert.Equal(1, output.Split("= next();", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("bitcast<vec2<u16>>", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Theory]
    [InlineData("extractBits(i16(-1), 4u, 8u)", "-1")]
    [InlineData("extractBits(u16(65535), 12u, 9u)", "15")]
    [InlineData("extractBits(u16(65535), 16u, 1u)", "0")]
    [InlineData("insertBits(i16(0), i16(-1), 12u, 9u)", "-4096")]
    [InlineData("insertBits(u16(0), u16(65535), 16u, 5u)", "0")]
    [InlineData("extractBits(18446744073709551615lu, 32u, 32u)", "4294967295")]
    [InlineData("extractBits(-1li, 31u, 33u)", "-1")]
    [InlineData("extractBits(-1li, 0u, 0u)", "0")]
    [InlineData("insertBits(0lu, 18446744073709551615lu, 60u, 99u)", "17293822569102704640")]
    [InlineData("extractBits(0xffffffffu, 0xffffffffu, 0xffffffffu)", "0")]
    [InlineData("insertBits(0xffffffffu, 0u, 0xffffffffu, 1u)", "4294967295")]
    public void BitfieldConstantEvaluationClampsRangesAndExtendsSigns(string expression, string expected)
    {
        var module = WgslReader.Parse($"enable wgpu_int16; const value = {expression};");
        ModuleValidator.Validate(module);
        Assert.Equal(expected, System.Convert.ToString(Assert.IsType<Expression.Literal>(module.Constants[0].Value).Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("i16")][InlineData("u16")][InlineData("i64")][InlineData("u64")]
    public void BitfieldRuntimeLoweringSupportsVectorsAndDynamicRanges(string type)
    {
        string enable = type.EndsWith("16", StringComparison.Ordinal) ? "enable wgpu_int16;" : "";
        string source = $"{enable} @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) {{ let v = vec2<{type}>({type}(i)); let a = extractBits(v, i, i); let b = insertBits(v, v, i, i); }}";
        byte[] bytes = ShaderTranslator.WgslToSpirv(source);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(bytes)));
        if (type.EndsWith("64", StringComparison.Ordinal)) Assert.DoesNotContain(SpirvBinary.Parse(bytes).Instructions, i => (Op)i.Opcode is Op.BitFieldUExtract or Op.BitFieldSExtract or Op.BitFieldInsert);
    }

    [Fact]
    public void WgslWriterDiscoversRequiredEnablesFromSharedIrTypes()
    {
        var module = new Module();
        module.Globals.Add(new("value", new ShaderType.Vector(2, ShaderType.I16), AddressSpace.Private));
        module.Structures.Add(new("HalfData", [new("value", ShaderType.F16)]));
        string output = WgslWriter.Write(module);
        Assert.Contains("enable wgpu_int16;", output); Assert.Contains("enable f16;", output);
        Assert.Empty(module.Enables);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Fact]
    public void NarrowStageIoDeclaresItsStorageCapabilityAndFlatInterpolation()
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("enable wgpu_int16; enable f16; @fragment fn main(@location(0) @interpolate(flat) value: u16, @location(1) half: f16) -> @location(0) vec4f { return vec4f(f32(value) + f32(half)); }"));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 4436);
        uint input = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 30 && i.Operands[2] == 0 && binary.Instructions.Any(v => (Op)v.Opcode == Op.Variable && v.Operands[1] == i.Operands[0] && v.Operands[2] == 1)).Operands[0];
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[0] == input && i.Operands[1] == 14);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Theory]
    [InlineData("var<private> value: i16;")]
    [InlineData("enable wgpu_int16; var<workgroup> value: atomic<u16>;")]
    [InlineData("enable wgpu_int16; const value = u16(65536);")]
    [InlineData("enable wgpu_int16; const value = i16(32768);")]
    [InlineData("enable wgpu_int16; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) index: u32) { let bad = u16(index) / u16(0); }")]
    [InlineData("enable wgpu_int16; @fragment fn main(@location(0) @interpolate(linear) value: u16) -> @location(0) vec4f { return vec4f(f32(value)); }")]
    [InlineData("enable wgpu_int16; @fragment fn main(@location(0) value: u16) -> @location(0) vec4f { return vec4f(f32(value)); }")]
    public void RejectsMissingExtensionUnsupportedAtomicsAndOutOfRangeAbstractValues(string source) =>
        Assert.Throws<ShaderException>(() => WgslWriter.Write(WgslReader.Parse(source)));

    [Theory]
    [InlineData("var<private> value: i16; enable wgpu_int16;")]
    [InlineData("const_assert true; enable wgpu_int16;")]
    [InlineData("; enable wgpu_int16;")]
    public void EnableDirectivesCannotRetroactivelyAuthorizeEarlierDeclarations(string source) =>
        Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
}
