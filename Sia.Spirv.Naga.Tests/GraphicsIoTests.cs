using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class GraphicsIoTests
{
    [Theory]
    [InlineData("view_index", "u32", "", "fragment", 4439u, 4440u, "SPV_KHR_multiview")]
    [InlineData("draw_index", "u32", "enable draw_index;", "vertex", 4427u, 4426u, "SPV_KHR_shader_draw_parameters")]
    [InlineData("barycentric", "vec3f", "", "fragment", 5284u, 5286u, "SPV_KHR_fragment_shader_barycentric")]
    [InlineData("barycentric_no_perspective", "vec3f", "", "fragment", 5284u, 5287u, "SPV_KHR_fragment_shader_barycentric")]
    public void GraphicsInputsDeclareCapabilitiesAndRoundtrip(string builtin, string type, string enable, string stage, uint capability, uint decoration, string extension)
    {
        string result = stage == "vertex" ? "-> @builtin(position) vec4f { return vec4f(f32(value), 0.0, 0.0, 1.0); }" : "{}";
        byte[] bytes = ShaderTranslator.WgslToSpirv($"{enable} @{stage} fn main(@builtin({builtin}) value: {type}) {result}");
        var binary = SpirvBinary.Parse(bytes);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == capability);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 11 && i.Operands[2] == decoration);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Extension && SpirvBinary.ReadString(i.Operands, out _) == extension);
        string output = ShaderTranslator.SpirvToWgsl(bytes);
        Assert.Contains($"@builtin({builtin})", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Theory]
    [InlineData(1)][InlineData(8)]
    public void ClipDistanceArraysKeepTheirLengthAndEnable(int length)
    {
        string source = $"enable clip_distances; struct Out {{ @builtin(position) position: vec4f, @builtin(clip_distances) clip: array<f32, {length}>, }} @vertex fn main() -> Out {{ var value: Out; value.clip[0] = 0.5; return value; }}";
        byte[] bytes = ShaderTranslator.WgslToSpirv(source);
        Assert.Contains(SpirvBinary.Parse(bytes).Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 32);
        var module = SpirvReader.Parse(bytes); ModuleValidator.Validate(module);
        var result = Assert.IsType<ShaderType.Structure>(module.Functions.Single(f => f.Stage == ShaderStage.Vertex).ReturnType);
        Assert.Equal((uint)length, Assert.IsType<ShaderType.Array>(result.Members.Single(m => m.Binding?.Builtin == "clip_distances").Type).Length);
        string output = WgslWriter.Write(module);
        Assert.Contains("enable clip_distances;", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Theory]
    [InlineData("clip_distances")][InlineData("draw_index")][InlineData("primitive_index")]
    public void EnabledBuiltinNeedsItsSourceDirective(string builtin)
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse($"struct Input {{ @builtin({builtin}) value: u32, }}"));
    }

    [Theory]
    [InlineData("@compute @workgroup_size(1) fn main(@builtin(view_index) value: u32) {}")]
    [InlineData("enable draw_index; @fragment fn main(@builtin(draw_index) value: u32) {}")]
    [InlineData("@fragment fn main(@builtin(barycentric) value: vec2f) {}")]
    [InlineData("@fragment fn main() -> @builtin(barycentric) vec3f { return vec3f(); }")]
    [InlineData("enable clip_distances; struct Out { @builtin(position) position: vec4f, @builtin(clip_distances) clip: array<f32, 9>, } @vertex fn main() -> Out { return Out(); }")]
    [InlineData("enable clip_distances; struct Out { @builtin(position) position: vec4f, @builtin(clip_distances) clip: array<u32, 1>, } @vertex fn main() -> Out { return Out(); }")]
    public void WrongGraphicsBuiltinTypesStagesAndDirectionsAreRejected(string source)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source));
    }

    [Theory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(false, true)]
    public void SignedInputIsAdaptedAndUnusedBlockBuiltinsAreCulled(bool useClip, bool wholeStore)
    {
        var module = SpirvReader.Parse(VertexInterface(useClip, wholeStore).ToBytes());
        ModuleValidator.Validate(module);
        var entry = module.Functions.Single(f => f.Stage == ShaderStage.Vertex);
        Assert.Equal(ShaderType.U32, Assert.Single(entry.Arguments).Type);
        Assert.Contains(entry.Body.Statements, s => s is Statement.Store { Value: Expression.Convert { Bitcast: true, Type: var type } } && type == ShaderType.I32);
        var result = Assert.IsType<ShaderType.Structure>(entry.ReturnType);
        Assert.Equal(useClip || wholeStore, result.Members.Any(m => m.Binding?.Builtin == "clip_distances"));
        string output = WgslWriter.Write(module);
        Assert.Equal(useClip || wholeStore, output.Contains("enable clip_distances;", StringComparison.Ordinal));
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    // The helper is deliberately defined after the entry function, as in glslang output.
    private static SpirvBinary VertexInterface(bool useClip, bool wholeStore)
    {
        static SpirvInstruction I(Op op, params uint[] args) => new((ushort)op, args);
        var instructions = new List<SpirvInstruction>
        {
            I(Op.Capability, 1), I(Op.Capability, 32), I(Op.MemoryModel, 0, 1),
            I(Op.EntryPoint, new uint[] { 0, 21 }.Concat(SpirvBinary.StringWords("main")).Concat([11u, 13u]).ToArray()),
            I(Op.MemberDecorate, 9, 0, 11, 0), I(Op.MemberDecorate, 9, 1, 11, 3), I(Op.Decorate, 9, 2), I(Op.Decorate, 13, 11, 42),
            I(Op.TypeVoid, 1), I(Op.TypeFunction, 2, 1), I(Op.TypeFloat, 3, 32), I(Op.TypeVector, 4, 3, 4),
            I(Op.TypeInt, 5, 32, 1), I(Op.TypeInt, 6, 32, 0), I(Op.Constant, 6, 7, 1), I(Op.TypeArray, 8, 3, 7),
            I(Op.TypeStruct, 9, 4, 8), I(Op.TypePointer, 10, 3, 9), I(Op.Variable, 10, 11, 3),
            I(Op.TypePointer, 12, 1, 5), I(Op.Variable, 12, 13, 1), I(Op.TypePointer, 14, 3, 4), I(Op.Constant, 5, 15, 0),
            I(Op.Constant, 3, 16, 0), I(Op.Constant, 3, 17, 0x3f800000), I(Op.ConstantComposite, 4, 18, 16, 16, 16, 17),
            I(Op.TypePointer, 19, 3, 3), I(Op.ConstantComposite, 8, 26, 17), I(Op.ConstantComposite, 9, 27, 18, 26),
            I(Op.Function, 1, 21, 0, 2), I(Op.Label, 22), I(Op.FunctionCall, 1, 23, 30), I(Op.Return), I(Op.FunctionEnd),
            I(Op.Function, 1, 30, 0, 2), I(Op.Label, 31),
        };
        if (wholeStore) instructions.Add(I(Op.Store, 11, 27));
        else
        {
            instructions.Add(I(Op.AccessChain, 14, 24, 11, 15)); instructions.Add(I(Op.Store, 24, 18));
            if (useClip) { instructions.Add(I(Op.AccessChain, 19, 25, 11, 7, 15)); instructions.Add(I(Op.Store, 25, 17)); }
        }
        instructions.Add(I(Op.Return)); instructions.Add(I(Op.FunctionEnd));
        return new SpirvBinary { Bound = 32, Instructions = instructions };
    }
}
