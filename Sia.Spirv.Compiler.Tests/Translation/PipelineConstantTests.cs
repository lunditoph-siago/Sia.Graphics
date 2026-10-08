using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PipelineConstantTests
{
    [Fact]
    public void ResolvesDependentDefaultsGlobalsAndWorkgroupDimensionsWithoutMutatingInput()
    {
        const string source = "@id(7) override size: u32; override area = size * 2u; var<private> initial: u32 = area + 1u; @compute @workgroup_size(area, 2i) fn main() { let x = area + 3u; }";
        var input = WgslReader.Parse(source);
        var resolved = PipelineConstantResolver.Resolve(input, new Dictionary<string, double> { ["7"] = 4.9 });
        Assert.All(resolved.Constants, c => Assert.False(c.IsOverride));
        Assert.Equal(8u, Assert.IsType<Expression.Literal>(resolved.Constants.Single(c => c.Name == "area").Value).Value);
        Assert.Equal(9u, Assert.IsType<Expression.Literal>(resolved.Globals[0].Initializer).Value);
        var function = Assert.Single(resolved.Functions);
        Assert.Equal(8u, Assert.IsType<Expression.Literal>(function.WorkgroupSize[0]).Value);
        Assert.Equal(11u, Assert.IsType<Expression.Literal>(Assert.IsType<Statement.Declare>(function.Body.Statements[0]).Initializer).Value);
        Assert.True(input.Constants[0].IsOverride); Assert.Null(input.Constants[0].Value);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(input, new() { PipelineConstants = new Dictionary<string, double> { ["7"] = 4 } }));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode is Op.SpecConstant or Op.SpecConstantTrue or Op.SpecConstantFalse);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ExecutionMode && i.Operands.SequenceEqual(new uint[] { i.Operands[0], 17, 8, 2, 1 }));
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary.ToBytes())));
    }

    [Fact]
    public void ExplicitValueReplacesDependentDefault()
    {
        var input = WgslReader.Parse("override base: u32 = 2u; override derived = base * 3u; @compute @workgroup_size(derived) fn main() {}");
        var result = PipelineConstantResolver.Resolve(input, new Dictionary<string, double> { ["derived"] = 7 });
        Assert.Equal(7u, Assert.IsType<Expression.Literal>(result.Functions[0].WorkgroupSize[0]).Value);
    }

    [Fact]
    public void LocalNamesAndParametersShadowOverridesOnlyInsideTheirScope()
    {
        var input = WgslReader.Parse("override value: u32 = 4u; fn helper(value: u32) -> u32 { return value; } @compute @workgroup_size(value) fn main() { { let value = 9u; let local = value; } let global = value; }");
        var output = WgslWriter.Write(PipelineConstantResolver.Resolve(input, new Dictionary<string, double>()));
        Assert.Contains("return value;", output);
        Assert.Contains("let local: u32 = value;", output);
        Assert.Contains("let global: u32 = 4u;", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }

    [Theory]
    [InlineData("size", 2d)] // Explicit @id replaces its name as the API identifier.
    [InlineData("unknown", 2d)]
    [InlineData("7", double.NaN)]
    [InlineData("7", double.PositiveInfinity)]
    [InlineData("7", -1d)]
    [InlineData("7", 4294967296d)]
    [InlineData("7", 0d)]
    public void RejectsUnknownNonfiniteOutOfRangeAndZeroWorkgroupValues(string key, double value)
    {
        var input = WgslReader.Parse("@id(7) override size: u32; @compute @workgroup_size(size) fn main() {}");
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(input, new Dictionary<string, double> { [key] = value }));
    }

    [Fact]
    public void MissingRequiredValueIsAnError()
    {
        var input = WgslReader.Parse("override required: f32; @compute @workgroup_size(1) fn main() { let v = required; }");
        Assert.Contains("Missing value", Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(input, new Dictionary<string, double>())).Message);
    }

    [Theory]
    [InlineData(double.NaN, false)][InlineData(0d, false)][InlineData(double.PositiveInfinity, true)][InlineData(-2d, true)]
    public void BooleanPipelineValuesFollowScalarConversion(double number, bool expected)
    {
        var input = WgslReader.Parse("override enabled: bool; @compute @workgroup_size(1) fn main() {}");
        var result = PipelineConstantResolver.Resolve(input, new Dictionary<string, double> { ["enabled"] = number });
        Assert.Equal(expected, Assert.IsType<Expression.Literal>(result.Constants[0].Value).Value);
    }

    [Theory]
    [InlineData("var<private> runtime: u32; override bad: u32 = runtime;")]
    [InlineData("var<private> runtime: u32; var<private> bad: u32 = runtime;")]
    [InlineData("override bad: u32 = bad;")]
    [InlineData("@compute @workgroup_size(1lu) fn main() {}")]
    public void RejectsRuntimeInitializersCyclesAndWideWorkgroupDimensions(string source) =>
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse(source)));

    [Fact]
    public void WorkgroupDimensionsAcceptConcreteSignedAndUnsignedExpressions()
    {
        var bytes = ShaderTranslator.WgslToSpirv("const a = 2i; const b = 3u; @compute @workgroup_size(a, b, b - 1u) fn main() {}");
        Assert.Contains(SpirvBinary.Parse(bytes).Instructions, i => (Op)i.Opcode == Op.ExecutionMode && i.Operands.Skip(1).SequenceEqual(new uint[] { 17, 2, 3, 2 }));
    }

    [Fact]
    public void SpirvSpecializationIdsAreAssignedWithoutCollidingWithExplicitIds()
    {
        var bytes = ShaderTranslator.WgslToSpirv("override first = 1u; @id(0) override second = 2u; @compute @workgroup_size(1) fn main() { let pair = vec2(first, second); }");
        var binary = SpirvBinary.Parse(bytes);
        Assert.Equal(new uint[] { 1, 0 }, binary.Instructions.Where(i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 1).Select(i => i.Operands[2]));
        var module = SpirvReader.Parse(bytes);
        Assert.Equal(2, module.Constants.Count(c => c.IsOverride));
        var plain = new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = binary.Instructions.Where(i => (Op)i.Opcode != Op.Decorate || i.Operands[1] != 1).ToArray() };
        Assert.DoesNotContain(SpirvReader.Parse(plain.ToBytes()).Constants, c => c.IsOverride);
    }

    [Fact]
    public void CompositeSpecializationPreservesScalarDependenciesWithoutBecomingAnOverride()
    {
        var bytes = ShaderTranslator.WgslToSpirv("@id(3) override first = 1u; @id(5) override second = 2u; var<private> output: vec2u; @compute @workgroup_size(1) fn main() { let pair = vec2(first, second); output = pair; }");
        var binary = SpirvBinary.Parse(bytes);
        var composite = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.CompositeConstruct);
        var instructions = binary.Instructions.ToList(); instructions.Remove(composite);
        instructions.Insert(instructions.FindIndex(i => (Op)i.Opcode == Op.Function), new((ushort)Op.SpecConstantComposite, composite.Operands));
        var specialized = new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = instructions };
        var module = SpirvReader.Parse(specialized.ToBytes());
        Assert.Equal(2, module.Constants.Count(c => c.IsOverride));
        ModuleValidator.Validate(module);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["3"] = 7, ["5"] = 9 });
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(resolved)));
        Assert.Contains("vec2<u32>(7u, 9u)", WgslWriter.Write(resolved));
    }

    [Fact]
    public void SpirvWriterDoesNotInventAZeroDefaultForRequiredOverrides() =>
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv("override required: u32; @compute @workgroup_size(1) fn main() { let x = required; }"));
}
