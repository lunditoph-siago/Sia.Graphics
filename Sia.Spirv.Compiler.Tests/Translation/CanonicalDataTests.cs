using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Tests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalDataTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>; @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    internal const string NumericVectorLoop = Resources + "@compute @workgroup_size(1) fn main(){var a=vec2f(1.0,2.0);var b=vec2f(3.0,4.0);var i=0u;loop{if i>=inputs[0]{break;}let old=a;a=b;b=old;i++;}let v=a+vec2f(0.5);outputs[0]=bitcast<u32>(v.x);outputs[1]=bitcast<u32>(dot(v,vec2f(2.0,4.0)));}";
    internal const string MatrixAggregate = Resources + "struct Pair{transform:mat2x2f,value:vec2f}fn helper(p:Pair)->vec2f{return p.transform*p.value;}@compute @workgroup_size(1) fn main(){let m=mat2x2f(1.0,2.0,3.0,4.0);var p=Pair(m,vec2f(2.0,3.0));if inputs[0]>0u{p=Pair(transpose(m),vec2f(2.0,3.0));}let v=helper(p);outputs[0]=bitcast<u32>(v.x);outputs[1]=bitcast<u32>(v.y);}";
    internal const string OrderedSelect = Resources + "fn take()->vec2f{outputs[1]+=1u;return vec2f(f32(outputs[1]),f32(outputs[1])+1.0);}@compute @workgroup_size(1) fn main(){let v=select(take(),take(),true);outputs[0]=bitcast<u32>(v.x);}";
    internal const string FloatBuiltin = Resources + "@compute @workgroup_size(1) fn main(){let x=bitcast<f32>(inputs[0]);outputs[0]=bitcast<u32>(clamp(abs(x),1.0f,4.0f));outputs[1]=bitcast<u32>(sqrt(4.0f));}";
    internal const string FiniteArrayLoop = Resources + "fn identity(v:array<u32,2>)->array<u32,2>{return v;}@compute @workgroup_size(1) fn main(){var a=array<u32,2>(1u,2u);var b=array<u32,2>(3u,4u);var i=0u;loop{if i>=inputs[0]{break;}let old=a;a=identity(b);b=old;i++;}let v=identity(a);outputs[0]=v[0];outputs[1]=v[1];}";
    internal const string RasterVertex = "@vertex fn main(@location(0) value:vec2f)->@builtin(position) vec4f{return vec4f(value,0.0f,1.0f);}";
    internal const string RasterFragment = "@fragment fn main(@location(0) value:vec2f)->@location(0) vec4f{return vec4f(value,0.0f,1.0f);}";

    [Theory]
    [InlineData("NumericVectorLoop")] [InlineData("MatrixAggregate")] [InlineData("OrderedSelect")] [InlineData("FloatBuiltin")] [InlineData("FiniteArrayLoop")]
    public void DataAndNumericBuiltinsActuallyMigrateThroughBothFrontendRoutes(string fixture)
    {
        var input = WgslReader.Parse(Source(fixture));
        foreach (var module in new[] { input, SpirvReader.Parse(SpirvWriter.Write(input)) }) {
            var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
            var prepared = CanonicalShaderPipeline.Run(module, traces, deferrals);
            Assert.Empty(deferrals); Assert.Equal(module.Functions.Count * 3, traces.Count);
            Assert.All(module.Functions, f => Assert.NotSame(f, prepared.Functions.Single(p => p.Name == f.Name)));
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
            ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        }
    }
    private static string Source(string fixture) => fixture switch {
        "NumericVectorLoop" => NumericVectorLoop, "MatrixAggregate" => MatrixAggregate, "OrderedSelect" => OrderedSelect,
        "FloatBuiltin" => FloatBuiltin, "FiniteArrayLoop" => FiniteArrayLoop, _ => throw new ArgumentException("Unknown fixture.", nameof(fixture))
    };
    private static ControlFlowFunction Graph(Module module)
    {
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(f => f.Stage == ShaderStage.Compute), module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module); return graph!;
    }
    [Fact]
    public void VectorAndAggregateSlotsPromoteToTypedLoopAndBranchMerges()
    {
        foreach (var fixture in new[] { NumericVectorLoop, MatrixAggregate, FiniteArrayLoop }) {
            var module = WgslReader.Parse(fixture); var graph = Graph(module); LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
            Assert.Contains(graph.Blocks.SelectMany(b => b.Parameters), p => p.Type is ShaderType.Vector or ShaderType.Structure or ShaderType.Array);
            Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Local);
        }
    }
    [Fact]
    public void PureNumericSelectKeepsBothOrderedUnknownEffectCalls()
    {
        var graph = Graph(WgslReader.Parse(OrderedSelect)); var instructions = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
        var calls = instructions.Where(i => i.Operation is ValueOperation.Call).ToArray(); Assert.Equal(2, calls.Length);
        Assert.All(calls, i => Assert.Equal(ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall, i.Effects));
        var select = Assert.Single(instructions, i => i.Operation is ValueOperation.Builtin { Function: "select" });
        Assert.Equal(ShaderEffects.None, select.Effects);
        var definitions = instructions.Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        SsaValue Unwrap(SsaValue value) {
            while (definitions[value.Id].Operation is ValueOperation.Let alias) value = alias.Value;
            return value;
        }
        Assert.Equal(calls.Select(c => c.Result!.Value), ((ValueOperation.Builtin)select.Operation).Arguments.Take(2).Select(Unwrap));
        Assert.True(Array.IndexOf(instructions, calls[0]) < Array.IndexOf(instructions, calls[1]));
        Assert.True(Array.IndexOf(instructions, calls[1]) < Array.IndexOf(instructions, select));
    }
    [Theory]
    [InlineData("constructor")] [InlineData("builtin")] [InlineData("swizzle")] [InlineData("conversion")]
    public void SharedTypeRulesRejectMalformedNumericOperations(string defect)
    {
        var module = WgslReader.Parse(NumericVectorLoop); var graph = Graph(module);
        var block = graph.Blocks.First(b => b.Instructions.Any(i => i.Result?.Type is ShaderType.Vector));
        int index = block.Instructions.FindIndex(i => i.Result?.Type is ShaderType.Vector);
        var instruction = block.Instructions[index]; var operand = ((ValueOperation.Construct)instruction.Operation).Components[0];
        graph.Blocks.First(b => b.Id == block.Id).Instructions[index] = instruction with { Operation = defect switch {
            "constructor" => new ValueOperation.Construct([operand, operand, operand]),
            "builtin" => new ValueOperation.Builtin("sqrt", [operand], instruction.Result!.Value.Type),
            "swizzle" => new ValueOperation.Swizzle(operand, "xy"),
            _ => new ValueOperation.Convert(operand, false)
        }};
        Assert.Contains("illegal", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
    }
    [Theory]
    [InlineData("Vectors")] [InlineData("IntegerAndBooleanVectors")] [InlineData("SquareMatrices")] [InlineData("RectangularMatrices")]
    public void MaintainedCilMathKernelsUseCanonicalDataOperations(string kernelName)
    {
        var kernel = SpirvTestAssembly.GetKernel(typeof(MathShaders), kernelName);
        var module = new SpirvCompiler().CompileModule(new SpirvModuleCompilationRequest(File.ReadAllBytes(SpirvTestAssembly.Path), kernel.MetadataToken,
            File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location)));
        var deferrals = new List<CanonicalDeferral>(); _ = CanonicalShaderPipeline.Run(module, deferrals: deferrals); Assert.Empty(deferrals);
    }

    [Theory]
    [InlineData(RasterVertex, ShaderStage.Vertex)]
    [InlineData(RasterFragment, ShaderStage.Fragment)]
    public void RasterDataRetainsEntryStageAndIoAcrossBothAdapters(string source, ShaderStage stage)
    {
        var input = WgslReader.Parse(source);
        foreach (var module in new[] { input, SpirvReader.Parse(SpirvWriter.Write(input)) }) {
            var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
            var output = CanonicalShaderPipeline.Run(module, traces, deferrals);
            Assert.Empty(deferrals); Assert.Equal(module.Functions.Count * 3, traces.Count);
            var entry = Assert.Single(output.Functions, f => f.Stage is not null);
            var original = Assert.Single(module.Functions, f => f.Stage is not null);
            Assert.Equal(stage, entry.Stage); Assert.Equal(original.ReturnBinding, entry.ReturnBinding);
            Assert.Equal(original.Arguments, entry.Arguments);
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(output)));
            ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(output)));
        }
    }
}
