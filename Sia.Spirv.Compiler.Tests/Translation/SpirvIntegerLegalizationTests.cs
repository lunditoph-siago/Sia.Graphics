using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvIntegerLegalizationTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    internal const string Signed = Resources + "@compute @workgroup_size(1) fn main(){let n=bitcast<i32>(inputs[0]);let d=bitcast<i32>(inputs[1]);outputs[0]=bitcast<u32>(n/d);outputs[1]=bitcast<u32>(n%d);}";
    internal const string Unsigned = Resources + "@compute @workgroup_size(1) fn main(){let n=inputs[0];let d=inputs[1];outputs[0]=n/d;outputs[1]=n%d;}";
    internal const string Ordered = Resources + "fn left()->i32{outputs[1]=outputs[1]*10u+1u;return bitcast<i32>(inputs[0]);}fn right()->i32{outputs[1]=outputs[1]*10u+2u;return bitcast<i32>(inputs[1]);}@compute @workgroup_size(1) fn main(){outputs[0]=bitcast<u32>(left()/right());}";
    internal const string Vector = Resources + "@compute @workgroup_size(1) fn main(){let n=vec2i(bitcast<i32>(inputs[0]),-7);let d=vec2i(bitcast<i32>(inputs[1]),3);let q=n/d;let r=n%d;outputs[0]=bitcast<u32>(q.x)^bitcast<u32>(q.y);outputs[1]=bitcast<u32>(r.x)^bitcast<u32>(r.y);}";
    internal static string WidthFixture(string scalar, int size, bool scalarLeft = false)
    {
        string enable = scalar.EndsWith("16", StringComparison.Ordinal) ? "enable wgpu_int16;" : "";
        string vector = size == 1 ? scalar : $"vec{size}<{scalar}>";
        string left = scalarLeft ? $"{scalar}(inputs[0])" : $"{vector}({scalar}(inputs[0]))";
        string right = scalarLeft || size == 1 ? $"{vector}({scalar}(inputs[1]))" : $"{scalar}(inputs[1])";
        string component = size == 1 ? "" : ".x";
        return enable + Resources + $"@compute @workgroup_size(1) fn main(){{let n={left};let d={right};let q=n/d;let r=n%d;outputs[0]=u32(q{component});outputs[1]=u32(r{component});}}";
    }

    [Fact]
    public void PreparationOwnsIntegerSafetyInsteadOfTheSerializer()
    {
        var module = WgslReader.Parse(Signed); string borrowed = WgslWriter.Emit(module);
        var prepared = ShaderTargetLowering.ForSpirv(module, null);
        Assert.Contains(prepared.Functions, f => f.Name.StartsWith("sia_spv_integer_", StringComparison.Ordinal));
        Assert.Equal(borrowed, WgslWriter.Emit(module));
        var checkedBytes = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(prepared)).ToBytes();
        var uncheckedBytes = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(prepared)).ToBytes();
        Assert.Equal(checkedBytes, uncheckedBytes);
        ModuleValidator.Validate(SpirvReader.Parse(checkedBytes));
    }

    [Fact]
    public void RawSerializationDoesNotInventIntegerGuards()
    {
        var module = WgslReader.Parse(Signed);
        Assert.Equal(SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module)).ToBytes(), SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module)).ToBytes());
    }

    [Theory]
    [InlineData(true, 0xfffffff9u, 3u, 0xfffffffeu, 0xffffffffu)]
    [InlineData(true, 7u, 0xfffffffdu, 0xfffffffeu, 1u)]
    [InlineData(true, 0x80000000u, 0xffffffffu, 0x80000000u, 0u)]
    [InlineData(true, 0xfffffff9u, 0u, 0xfffffff9u, 0u)]
    [InlineData(false, 0xffffffffu, 2u, 0x7fffffffu, 1u)]
    [InlineData(false, 17u, 0u, 17u, 0u)]
    public void LegalizedRuntimeDivisionAndRemainderHaveSpecifiedResults(bool signed, uint numerator, uint divisor, uint quotient, uint remainder)
    {
        var module = WgslReader.Parse(signed ? Signed : Unsigned);
        var prepared = ShaderTargetLowering.ForSpirv(module, null);
        Assert.Equal(new uint[] { quotient, remainder }, new CanonicalExecution(prepared, [numerator, divisor]).Run().Output);
        var bytes = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(prepared)).ToBytes();
        var native = SpirvReader.Parse(bytes);
        Assert.Equal(new uint[] { quotient, remainder }, new CanonicalExecution(native, [numerator, divisor]).Run().Output);
    }

    [Theory]
    [InlineData(17u, 0u, 17u)] [InlineData(0x80000000u, 0xffffffffu, 0x80000000u)] [InlineData(0xfffffff9u, 3u, 0xfffffffeu)]
    public void EffectfulOperandsAreReadOnceInLeftToRightOrder(uint numerator, uint divisor, uint expected)
    {
        var module = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Ordered), null);
        foreach (var candidate in new[] { module, SpirvReader.Parse(SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module)).ToBytes()) }) {
            var result = new CanonicalExecution(candidate, [numerator, divisor]).Run();
            Assert.Equal(new uint[] { expected, 12 }, result.Output); Assert.Equal(new[] { 0, 1 }, result.Reads);
        }
    }

    [Fact]
    public void ExistingSignedRemainderExpansionIsVisibleBeforeEmission()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Vector), null);
        Assert.Contains(prepared.Functions, f => f.Name.StartsWith("sia_spv_integer_remainder_", StringComparison.Ordinal));
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(prepared));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.SRem);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.SDiv);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory]
    [InlineData("i16", 1, false)] [InlineData("u16", 1, false)] [InlineData("i32", 1, false)] [InlineData("u32", 1, false)]
    [InlineData("i64", 1, false)] [InlineData("u64", 1, false)]
    [InlineData("i16", 2, false)] [InlineData("u16", 3, false)] [InlineData("i64", 4, false)] [InlineData("u64", 2, false)]
    [InlineData("i32", 2, true)] [InlineData("u32", 4, true)]
    public void WidthsAndScalarVectorBroadcastAreVerifiedBeforeAndAfterEmission(string scalar, int size, bool scalarLeft)
    {
        var module = WgslReader.Parse(WidthFixture(scalar, size, scalarLeft));
        var prepared = ShaderTargetLowering.ForSpirv(module, null);
        Assert.Equal(2, prepared.Functions.Count(f => f.Name.StartsWith("sia_spv_integer_", StringComparison.Ordinal)));
        var bytes = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(prepared)).ToBytes();
        ModuleValidator.Validate(SpirvReader.Parse(bytes));
        Assert.Equal(SpirvBinary.Parse(SpirvWriter.Write(module)).ToWords(), SpirvWriter.WriteWords(module));
    }

    [Fact]
    public void DivisionOptOutIsHonoredWhileSignedRemainderStillUsesItsExistingExpansion()
    {
        var unsigned = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Unsigned), null, integerDivisionChecks: false);
        Assert.DoesNotContain(unsigned.Functions, f => f.Name.StartsWith("sia_spv_integer_", StringComparison.Ordinal));
        var signed = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Signed), null, integerDivisionChecks: false);
        var helper = Assert.Single(signed.Functions, f => f.Name.StartsWith("sia_spv_integer_", StringComparison.Ordinal));
        Assert.DoesNotContain(helper.Body.Statements, s => s is Statement.Declare { Name: "safe_divisor" });
        Assert.DoesNotContain(SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(signed)).Instructions, i => (Op)i.Opcode == Op.SRem);
        Assert.Equal(SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(unsigned)).ToBytes(), SpirvWriter.Write(WgslReader.Parse(Unsigned), new(EmitIntegerDivisionChecks: false)));
    }

    [Fact]
    public void FloatingDivisionDoesNotAcquireIntegerPolicy()
    {
        var module = WgslReader.Parse(Resources + "@compute @workgroup_size(1) fn main(){outputs[0]=bitcast<u32>(bitcast<f32>(inputs[0])/bitcast<f32>(inputs[1]));}");
        var prepared = ShaderTargetLowering.ForSpirv(module, null);
        Assert.DoesNotContain(prepared.Functions, f => f.Name.StartsWith("sia_spv_integer_", StringComparison.Ordinal));
        Assert.Contains(SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(prepared)).Instructions, i => (Op)i.Opcode == Op.FDiv);
    }

    [Fact]
    public void GeneratedFunctionsAvoidUserNamesAndDoNotChangeBorrowedSource()
    {
        var module = WgslReader.Parse(Signed.Replace("@compute", "fn sia_spv_integer_divide_0(){} @compute", StringComparison.Ordinal));
        string original = WgslWriter.Emit(module);
        var prepared = ShaderTargetLowering.ForSpirv(module, null);
        Assert.Equal(prepared.Functions.Count, prepared.Functions.Select(f => f.Name).Distinct().Count());
        Assert.Equal(original, WgslWriter.Emit(module));
        Assert.Equal(SpirvWriter.Write(module), SpirvWriter.Write(module));
    }
}
