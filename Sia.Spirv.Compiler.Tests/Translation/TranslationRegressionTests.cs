using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class TranslationRegressionTests
{
    [Fact]
    public void BitcastRepackingPreservesHalfNanPayloadsAndComponentOrder()
    {
        var module = WgslReader.Parse("enable f16; const halves = bitcast<vec2<f16>>(0x7c007e01u); const bits = bitcast<u32>(halves); const wide = bitcast<u64>(vec2u(0x89abcdefu, 0x01234567u));");
        Assert.Equal(0x7c007e01u, Assert.IsType<Expression.Literal>(module.Constants[1].Value).Value);
        Assert.Equal(0x0123456789abcdeful, Assert.IsType<Expression.Literal>(module.Constants[2].Value).Value);
        string output = WgslWriter.Write(module);
        var roundtrip = WgslReader.Parse(output);
        Assert.Equal(0x7c007e01u, Assert.IsType<Expression.Literal>(roundtrip.Constants[1].Value).Value);
    }

    [Theory]
    [InlineData("f32")][InlineData("vec2f")][InlineData("f16")][InlineData("f64")]
    public void NanClassificationHasValidWgslBitRepresentation(string type)
    {
        var scalar = new ShaderType.Scalar(ScalarKind.Float, type == "f16" ? 2 : type == "f64" ? 8 : 4);
        ShaderType data = type == "vec2f" ? new ShaderType.Vector(2, scalar) : scalar;
        ShaderType result = data is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.Bool) : ShaderType.Bool;
        var module = new Module(); if (type == "f16") module.Enables.Add("f16");
        var function = new ShaderFunction("f") { ReturnType = result }; function.Arguments.Add(new("a", data));
        function.Body.Statements.Add(new Statement.Return(new Expression.Call("isNan", [new Expression.Reference("a", data)], result))); module.Functions.Add(function);
        string output = WgslWriter.Write(module);
        Assert.DoesNotContain("isNan(", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(SpirvWriter.Write(module))));
    }

    [Fact]
    public void UnorderedFloatComparisonRetainsNanAcceptance()
    {
        var source = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("fn f(a: f32, b: f32) -> bool { return a < b; }"));
        var binary = new SpirvBinary { Version = source.Version, Bound = source.Bound, Instructions = source.Instructions.Select(i => (Op)i.Opcode == Op.FOrdLessThan ? new SpirvInstruction((ushort)Op.FUnordLessThan, i.Operands) : i).ToArray() };
        var module = SpirvReader.Parse(binary.ToBytes());
        var output = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Equal(2, output.Instructions.Count(i => (Op)i.Opcode == Op.IsNan));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Fact]
    public void NonSemanticDebugMetadataCanAppearAtModuleScopeAndAfterReturn()
    {
        var original = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("@compute @workgroup_size(1) fn main() {}"));
        uint import = original.Bound, moduleDebug = import + 1, functionDebug = import + 2;
        uint voidType = original.Instructions.Single(i => (Op)i.Opcode == Op.TypeVoid).Operands[0];
        var instructions = new List<SpirvInstruction>(); bool moduleInserted = false, functionInserted = false;
        foreach (var instruction in original.Instructions)
        {
            if ((Op)instruction.Opcode == Op.MemoryModel)
                instructions.Add(new((ushort)Op.ExtInstImport, new uint[] { import }.Concat(SpirvBinary.StringWords("NonSemantic.Shader.DebugInfo.100")).ToArray()));
            if (!moduleInserted && (Op)instruction.Opcode == Op.Function)
            { instructions.Add(new((ushort)Op.ExtInst, [voidType, moduleDebug, import, 0])); moduleInserted = true; }
            instructions.Add(instruction);
            if (!functionInserted && (Op)instruction.Opcode == Op.Return)
            { instructions.Add(new((ushort)Op.ExtInst, [voidType, functionDebug, import, 0])); functionInserted = true; }
        }
        var binary = new SpirvBinary { Version = original.Version, Bound = functionDebug + 1, Instructions = instructions };
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void WideNumericLiteralsAndAtomicsRoundtripWithoutNarrowing()
    {
        const string source = "var<workgroup> a: atomic<u64>; @compute @workgroup_size(1) fn main() { var x = 18446744073709551615lu; var y = -9223372036854775807li - 1li; let z = 1e30lf; atomicAdd(&a, x); let n = y / -1li; }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 12);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.TypeFloat && i.Operands[1] == 64);
        string output = ShaderTranslator.SpirvToWgsl(binary.ToBytes());
        Assert.Contains("18446744073709551615lu", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Fact]
    public void WideConstantsPreserveBitPatternsAndBitOperationWidth()
    {
        var module = WgslReader.Parse("const a = bitcast<i64>(18446744073709551615lu); const b = countLeadingZeros(0lu); const c = reverseBits(1li); const d = firstLeadingBit(-1li); const e = u64(1e30f);");
        Assert.Equal(new object[] { -1L, 64ul, long.MinValue, -1L, 18446742974197923840ul }, module.Constants.Select(c => Assert.IsType<Expression.Literal>(c.Value).Value));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Fact]
    public void OptionalRuntimeFoldDoesNotRejectWrappingArithmetic()
    {
        const string source = "@compute @workgroup_size(1) fn main() { var x = 0li; x += 31li - 1002003004005006li + -0x7fffffffffffffffli; }";
        ModuleValidator.Validate(SpirvReader.Parse(ShaderTranslator.WgslToSpirv(source)));
        Assert.Throws<ShaderException>(() => WgslReader.Parse("const x = 9223372036854775807li + 1li;"));
    }

    [Fact]
    public void WorkgroupUniformLoadOfAtomicReturnsScalarWithAtomicRead()
    {
        const string source = "var<workgroup> a: atomic<u32>; @compute @workgroup_size(1) fn main() { let value: u32 = workgroupUniformLoad(&a); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.AtomicLoad);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void StorageBufferBindingArrayRetainsAddressSpaceAndRuntimeMemberQueries()
    {
        const string source = "enable wgpu_binding_array; struct S { x: u32, values: array<u32> } @group(0) @binding(0) var<storage, read_write> buffers: binding_array<S>; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { let n = arrayLength(&buffers[i].values); buffers[i].x = n; }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5308);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5307);
        string output = ShaderTranslator.SpirvToWgsl(binary.ToBytes());
        Assert.Contains("var<storage, read_write>", output);
        Assert.Contains("binding_array<", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Fact]
    public void DynamicTextureBindingArraysPreserveLengthComparisonAndNonUniformDecorations()
    {
        const string source = "enable wgpu_binding_array; @group(0) @binding(0) var t: binding_array<texture_depth_2d>; @group(0) @binding(1) var s: binding_array<sampler_comparison, 4>; @fragment fn main(@location(0) @interpolate(flat) i: u32) -> @location(0) f32 { return textureSampleCompareLevel(t[i], s[i], vec2f(0), 0.5); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5302);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 5300);
        string output = ShaderTranslator.SpirvToWgsl(binary.ToBytes());
        Assert.Contains("binding_array<texture_depth_2d>", output);
        Assert.Contains("binding_array<sampler_comparison, 4>", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Fact]
    public void ConstantBindingArrayIndexDoesNotRequireNonUniformCapability()
    {
        const string source = "enable wgpu_binding_array; @group(0) @binding(0) var t: binding_array<texture_2d<f32>, 4>; @fragment fn main() -> @location(0) vec4f { return textureLoad(t[0], vec2i(0), 0); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] is 5301 or 5302);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void BaseClampToEdgeSamplesLevelZeroWithHalfTexelBounds()
    {
        const string source = "@group(0) @binding(0) var t: texture_2d<f32>; @group(0) @binding(1) var s: sampler; @compute @workgroup_size(1) fn main() { let value = textureSampleBaseClampToEdge(t, s, vec2f(-1, 2)); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ImageQuerySizeLod);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ExtInst && i.Operands[3] == 43);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ImageSampleExplicitLod && i.Operands[4] == 2);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void BitConstantsUseConcreteWidthAndSignedLeadingBitRules()
    {
        var module = WgslReader.Parse("const a = firstLeadingBit(-1); const b = countLeadingZeros(0u); const c = countTrailingZeros(0); const d = firstTrailingBit(0u); const e = reverseBits(1i); const f = countOneBits(255u);");
        Assert.Equal(new object[] { -1, 32u, 32, uint.MaxValue, int.MinValue, 8u }, module.Constants.Select(c => Assert.IsType<Expression.Literal>(c.Value).Value));
    }

    [Fact]
    public void PredeclaredMathResultsRoundtripThroughSpirvStructureAdapters()
    {
        const string source = "fn f(a: vec2f) -> vec2f { let m = modf(a); let e = frexp(a); return m.fract + m.whole + ldexp(e.fract, e.exp); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ExtInst && i.Operands[3] == 36);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.ExtInst && i.Operands[3] == 35);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void MatrixArithmeticAndConversionsOperateOnColumns()
    {
        const string source = "enable f16; fn f(a: mat3x4h, b: mat3x4h) -> mat3x4f { return mat3x4f(-(a + b) - b); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        var matrixIds = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypeMatrix).Select(i => i.Operands[0]).ToHashSet();
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode is Op.FAdd or Op.FSub or Op.FNegate or Op.FConvert && matrixIds.Contains(i.Operands[0]));
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void NumericConstantCastsClampFloatsAndReinterpretIntegerBits()
    {
        var module = WgslReader.Parse("enable f16; const a = u32(-65504h); const b = i32(1e30f); const c = u32(1e30f); const d = u32(-1i); const e = i32(4294967295u);");
        Assert.Equal(new object[] { 0u, 2147483520, 4294967040u, uint.MaxValue, -1 }, module.Constants.Select(c => Assert.IsType<Expression.Literal>(c.Value).Value));
        Assert.Throws<ShaderException>(() => WgslReader.Parse("const a: u32 = -1;"));
    }

    [Fact]
    public void RuntimeFloatCastClampsBeforeConversionForScalarAndVector()
    {
        const string source = "@compute @workgroup_size(1) fn main() { var a = vec2f(1e30, -1e30); let b = vec2i(a); let c = u32(a.x); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Equal(2, binary.Instructions.Count(i => (Op)i.Opcode == Op.ExtInst && i.Operands[3] == 43));
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void UnreachableReturnIsValidatedButNotEmitted()
    {
        const string source = "@fragment fn main() -> @location(0) vec4f { return vec4f(); return vec4f(1); }";
        ModuleValidator.Validate(SpirvReader.Parse(ShaderTranslator.WgslToSpirv(source)));
    }

    [Fact]
    public void UniformMatrixColumnsPreserveOriginalByteOffsets()
    {
        const string source = "struct S { prefix: f32, m: mat2x2f, tail: f32, } @group(0) @binding(0) var<uniform> data: S; fn f(i: u32) -> f32 { return data.m[i][0]; }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        var physical = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.TypeStruct && i.Operands.Length == 5);
        var offsets = binary.Instructions.Where(i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == physical.Operands[0] && i.Operands[2] == 35).OrderBy(i => i.Operands[1]).Select(i => i.Operands[3]);
        Assert.Equal(new uint[] { 0, 8, 16, 24 }, offsets);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void FunctionPointerSubobjectUsesTemporaryAndCopiesResultBack()
    {
        const string source = "fn mutate(p: ptr<function, i32>) { *p = 9; } fn f() -> i32 { var a = array<i32, 2>(1, 2); mutate(&a[1]); return a[1]; }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        var variables = binary.Instructions.Where(i => (Op)i.Opcode == Op.Variable).Select(i => i.Operands[1]).ToHashSet();
        var call = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.FunctionCall);
        Assert.Contains(call.Operands[3], variables);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 4442);
        var f = WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())); ModuleValidator.Validate(f);
    }

    [Fact]
    public void SignedVectorRemainderAvoidsVulkanPoisonInstruction()
    {
        const string source = "fn f(a: vec2i, b: vec2i) -> vec2i { return a % b; }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.SRem);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.SDiv);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void SampleMaskUsesSpirvArrayAndWgslScalar()
    {
        const string source = "@fragment fn f(@builtin(sample_mask) mask: u32) -> @builtin(sample_mask) u32 { return mask; }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        var array = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.TypeArray);
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands[2] == array.Operands[0]).Select(i => i.Operands[1]);
        Assert.Contains(1u, pointers); Assert.Contains(3u, pointers);
        var roundtrip = WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes()));
        Assert.Equal(ShaderType.U32, Assert.Single(roundtrip.Functions, f => f.Stage is not null).Arguments[0].Type);
    }

    [Theory]
    [InlineData("force")][InlineData("less_equal")][InlineData("greater_equal")][InlineData("unchanged")]
    public void EarlyDepthModeSurvivesBothBackends(string mode)
    {
        string source = $"@fragment @early_depth_test({mode}) fn f() -> @builtin(frag_depth) f32 {{ return 0.5; }}";
        var module = WgslReader.Parse(source);
        Assert.Contains($"@early_depth_test({mode})", WgslWriter.Write(module));
        var roundtrip = SpirvReader.Parse(SpirvWriter.Write(module));
        var entry = Assert.Single(roundtrip.Functions, f => f.Stage is not null);
        Assert.True(entry.EarlyDepthTest); Assert.Equal(mode == "force" ? null : mode, entry.ConservativeDepth);
    }

    [Fact]
    public void AtomicCompareResultRemainsPredeclaredInWgsl()
    {
        const string source = "var<workgroup> a: atomic<u32>; @compute @workgroup_size(1) fn main() { let r = atomicCompareExchangeWeak(&a, 1u, 2u); let old = r.old_value; }";
        string output = WgslWriter.Write(WgslReader.Parse(source));
        Assert.DoesNotContain("struct sia_naga_temp", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
        ModuleValidator.Validate(SpirvReader.Parse(ShaderTranslator.WgslToSpirv(source)));
    }

    [Fact]
    public void InferredZeroVectorAndMatrixConstructorsMaterialize()
    {
        var module = WgslReader.Parse("const zero = vec2(); const m = mat2x2(vec2(1, 2), vec2(3, 4)); fn f() { var a = zero; var b = m; }");
        ModuleValidator.Validate(module); SpirvWriter.Write(module);
    }

    [Fact]
    public void AbstractMathConstantsAndRuntimeSelectMaterialize()
    {
        var module = WgslReader.Parse("const p = pow(2, 3); const c = cross(vec3(1.,0.,0.), vec3(0.,1.,0.)); fn f(a: bool) -> f32 { let s = sign(vec2(-1)); return select(0.0, p, a) + c.z + f32(s.x); }");
        ModuleValidator.Validate(module); SpirvWriter.Write(module);
        Assert.Equal(8d, Assert.IsType<Expression.Literal>(module.Constants[0].Value).Value);
    }

    [Fact]
    public void AbstractScientificNotationIsValidWgsl()
    {
        var module = WgslReader.Parse("const big = 1e20;");
        WgslReader.Parse(WgslWriter.Write(module));
    }

    [Theory]
    [InlineData("texture_2d<f32>", "sampler", "vec2f(0.5)", "textureSample(t, s, uv)", "vec4f")]
    [InlineData("texture_2d_array<f32>", "sampler", "vec2f(0.5)", "textureSampleLevel(t, s, uv, 1, 0.0)", "vec4f")]
    [InlineData("texture_depth_2d", "sampler_comparison", "vec2f(0.5)", "textureSampleCompareLevel(t, s, uv, 0.5)", "f32")]
    [InlineData("texture_depth_cube_array", "sampler_comparison", "vec3f(0.5)", "textureSampleCompareLevel(t, s, uv, 0, 0.5)", "f32")]
    [InlineData("texture_2d<f32>", "sampler", "vec2f(0.5)", "textureGather(1, t, s, uv)", "vec4f")]
    public void TextureSamplingAndComparisonRoundtrip(string texture, string sampler, string coordinates, string operation, string result)
    {
        string source = $"@group(0) @binding(0) var t: {texture}; @group(0) @binding(1) var s: {sampler}; @fragment fn main() -> @location(0) {result} {{ let uv = {coordinates}; return {operation}; }}";
        string output = ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source));
        ModuleValidator.Validate(WgslReader.Parse(output));
        Assert.Contains(sampler, output);
    }

    [Fact]
    public void StorageTextureReadWriteAndQueryRoundtrip()
    {
        const string source = "@group(0) @binding(0) var t: texture_storage_2d<rgba8unorm, read_write>; @compute @workgroup_size(1) fn main() { let size = textureDimensions(t); let value = textureLoad(t, vec2i(0)); textureStore(t, vec2i(size) - vec2i(1), value); }";
        string output = ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source));
        ModuleValidator.Validate(WgslReader.Parse(output));
        Assert.Contains("textureStore", output); Assert.Contains("textureLoad", output);
    }

    [Fact]
    public void MultisampledTextureFetchRoundtrips()
    {
        const string source = "@group(0) @binding(0) var t: texture_multisampled_2d<f32>; @fragment fn main() -> @location(0) vec4f { return textureLoad(t, vec2i(0), 0); }";
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source))));
    }

    [Fact]
    public void SpirvMemoryBarrierRemainsDistinctFromControlBarrier()
    {
        var source = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("@compute @workgroup_size(1) fn main() { workgroupBarrier(); }"));
        var instructions = source.Instructions.Select(i => (Op)i.Opcode == Op.ControlBarrier ? new SpirvInstruction((ushort)Op.MemoryBarrier, i.Operands[1..]) : i).ToArray();
        var binary = new SpirvBinary { Bound = source.Bound, Version = source.Version, Instructions = instructions };
        var module = SpirvReader.Parse(binary.ToBytes());
        Assert.Contains(module.Functions.SelectMany(f => f.Body.Statements), s => s is Statement.MemoryBarrier);
        var output = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(output.Instructions, i => (Op)i.Opcode == Op.MemoryBarrier);
        Assert.DoesNotContain(output.Instructions, i => (Op)i.Opcode == Op.ControlBarrier);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Fact]
    public void SubgroupBarrierDoesNotBecomeWorkgroupExecutionBarrier()
    {
        const string source = "@compute @workgroup_size(1) fn main() { subgroupBarrier(); }";
        string output = ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains("subgroupBarrier();", output);
        Assert.DoesNotContain("workgroupBarrier();", output);
    }

    [Fact]
    public void ShadowedForAndWhileBodyLocalsDoNotChangeLoopConditionOrContinuing()
    {
        const string source = "fn f(a: i32) { for (var a = 0; a < 1; a++) { let a = true; } while a > 2 { let a = false; } }";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module); SpirvWriter.Write(module);
    }

    [Fact]
    public void PackedIntegerConstantsPreserveSignsAndClamping()
    {
        const string source = "const p = pack4xI8(vec4i(-1, 1, -128, 127)); const v = unpack4xI8(p); const d = dot4I8Packed(p, p); const c = pack4xI8Clamp(vec4i(-200, 200, 0, 1));";
        var module = WgslReader.Parse(source);
        Assert.Equal(0x7f8001ffu, Assert.IsType<Expression.Literal>(module.Constants[0].Value).Value);
        var vector = Assert.IsType<Expression.Construct>(module.Constants[1].Value);
        Assert.Equal(new object[] { -1, 1, -128, 127 }, vector.Components.Cast<Expression.Literal>().Select(v => v.Value));
        Assert.Equal(32515, Assert.IsType<Expression.Literal>(module.Constants[2].Value).Value);
        Assert.Equal(0x01007f80u, Assert.IsType<Expression.Literal>(module.Constants[3].Value).Value);
        ModuleValidator.Validate(module);
    }

    [Fact]
    public void RuntimePackedIntegerOperationsRoundtripWithoutExtensionInstructions()
    {
        const string source = "fn f(a: vec4i, b: u32) -> i32 { let p = pack4xI8Clamp(a); let u = unpack4xU8(b); return dot4I8Packed(p, b) + i32(u.x); }";
        string output = ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source));
        ModuleValidator.Validate(WgslReader.Parse(output));
    }
}
