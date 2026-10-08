using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using Xunit;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CooperativeMatrixTests
{
    internal const string Source = """
        enable wgpu_cooperative_matrix;
        @group(0) @binding(0) var<storage,read> inputs:array<f32>;
        @group(0) @binding(1) var<storage,read_write> outputs:array<f32>;
        fn multiply(a:coop_mat8x8<f32,A>,b:coop_mat8x8<f32,B>,c:coop_mat8x8<f32,C>)->coop_mat8x8<f32,C> {
            return coopMultiplyAdd(a,b,c);
        }
        @compute @workgroup_size(32) fn main() {
            let a=coopLoad<coop_mat8x8<f32,A>>(&inputs[0]);
            let b=coopLoadT<coop_mat8x8<f32,B>>(&inputs[64],16u);
            var c=coop_mat8x8<f32,C>();
            c=multiply(a,b,c);
            c=(-1.0)*c+c*2.0+0.5*c-c;
            coopStore(c,&outputs[0]);
            coopStoreT(c,&outputs[64],16i);
        }
        """;

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    public void LoadMultiplyStoreAndArithmeticRoundtrip(int size, bool half)
    {
        string source = Source.Replace("8x8", $"{size}x{size}");
        if (half) source = "enable f16;" + source.Replace("f32", "f16");
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands is [6022]);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands is [5345]);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands is [5346]);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.MemoryModel && i.Operands is [0, 3]);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.CooperativeMatrixMulAddKHR);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.MatrixTimesScalar);
        var back = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(back);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(back)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(back)));
    }

    [Fact]
    public void MixedAccumulatorPreservesDistinctComponentWidths()
    {
        string source = "enable f16;" + Source.Replace("var<storage,read> inputs:array<f32>", "var<storage,read> inputs:array<f16>")
            .Replace("coop_mat8x8<f32,A>", "coop_mat8x8<f16,A>").Replace("coop_mat8x8<f32,B>", "coop_mat8x8<f16,B>");
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        var helper = module.Functions.Single(f => f.Name == "multiply");
        Assert.Equal(ShaderType.F16, ((ShaderType.CooperativeMatrix)helper.Arguments[0].Type).Component);
        Assert.Equal(ShaderType.F32, ((ShaderType.CooperativeMatrix)helper.ReturnType).Component);
        var back = SpirvReader.Parse(SpirvWriter.Write(module));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(back)));
    }

    [Fact]
    public void TypeRoleLayoutAndDefaultStrideHaveExactNativeOperands()
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(Source));
        uint Value(uint id) => binary.Instructions.Single(i => (Op)i.Opcode == Op.Constant && i.Operands[1] == id).Operands[2];
        var types = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypeCooperativeMatrixKHR).ToArray();
        Assert.Equal(new uint[] { 0, 1, 2 }, types.Select(i => Value(i.Operands[5])).Order().ToArray());
        Assert.All(types, t => { Assert.Equal(3u, Value(t.Operands[2])); Assert.Equal(8u, Value(t.Operands[3])); Assert.Equal(8u, Value(t.Operands[4])); });
        var loads = binary.Instructions.Where(i => (Op)i.Opcode == Op.CooperativeMatrixLoadKHR).ToArray();
        Assert.Equal(1u, Value(loads[0].Operands[3])); Assert.Equal(8u, Value(loads[0].Operands[4]));
        Assert.Equal(0u, Value(loads[1].Operands[3])); Assert.Equal(16u, Value(loads[1].Operands[4]));
        var stores = binary.Instructions.Where(i => (Op)i.Opcode == Op.CooperativeMatrixStoreKHR).ToArray();
        Assert.Equal(1u, Value(stores[0].Operands[2])); Assert.Equal(8u, Value(stores[0].Operands[3]));
        Assert.Equal(0u, Value(stores[1].Operands[2])); Assert.Equal(16u, Value(stores[1].Operands[3]));
    }

    [Fact]
    public void LoadComponentFollowsMemoryAsInPinnedReference()
    {
        string source = "enable f16; enable wgpu_cooperative_matrix; @group(0) @binding(0) var<storage,read> data:array<f16>; @compute @workgroup_size(32) fn main(){let value=coopLoad<coop_mat8x8<f32,C>>(&data[0]);}";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        var value = (Statement.Declare)module.Functions[0].Body.Statements[0];
        Assert.Equal(ShaderType.F16, ((ShaderType.CooperativeMatrix)value.Type).Component);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Fact]
    public void DeviceScopeCapabilityIsAddedOnlyWhenNeededByVulkanMemoryModel()
    {
        string source = Source.Replace("fn multiply(", "@group(0) @binding(2) var<storage,read_write> count:atomic<u32>; fn multiply(")
            .Replace("var c=", "_=atomicAdd(&count,1u); storageBarrier(); var c=");
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands is [5346]);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
        var ordinary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("enable wgpu_cooperative_matrix; @compute @workgroup_size(1) fn main(){}"));
        Assert.Contains(ordinary.Instructions, i => (Op)i.Opcode == Op.MemoryModel && i.Operands is [0, 1]);
    }

    [Theory]
    [InlineData("coop_mat8x8<f32,D>")]
    [InlineData("coop_mat8x8<u32,A>")]
    [InlineData("coop_mat8x8<f64,A>")]
    [InlineData("coop_mat8x8<vec2f,A>")]
    [InlineData("coop_mat8x8<f16,A>")]
    [InlineData("coop_mat8x8<f32>")]
    public void InvalidTypeTemplatesFail(string type)
    {
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse("enable wgpu_cooperative_matrix; var<private> value:"+type+";")));
    }

    [Theory]
    [InlineData("_=coopMultiplyAdd(c,b,a);")]
    [InlineData("_=coopMultiplyAdd(a,b);")]
    [InlineData("_=coopLoad<f32>(&inputs[0]);")]
    [InlineData("_=coopLoad<coop_mat8x8<f32,A>>(&inputs[0],1.0);")]
    [InlineData("coopStore(c,&inputs[0]);")]
    [InlineData("_=bitcast<f32>(c);")]
    [InlineData("_=c*c;")]
    [InlineData("_=c/c;")]
    [InlineData("_=-c;")]
    [InlineData("_=coop_mat8x8<f32,C>(1.0);")]
    public void InvalidOperationsFail(string statement)
    {
        string source = Source.Replace("c=multiply(a,b,c);", statement);
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse(source)));
    }

    [Fact]
    public void EnableStageAndAllocationRestrictionsAreChecked()
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse(Source.Replace("enable wgpu_cooperative_matrix;", "")));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse(Source.Replace("@compute @workgroup_size(32)", "@fragment"))));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse("enable wgpu_cooperative_matrix; var<workgroup> value:coop_mat8x8<f32,A>;")));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse("enable wgpu_cooperative_matrix; @group(0) @binding(0) var<storage,read> value:coop_mat8x8<f32,A>;")));
    }

    [Fact]
    public void ContainersAndAliasesRetainCooperativeTypes()
    {
        string source="enable wgpu_cooperative_matrix; alias M=coop_mat16x16<f32,C>; struct S{value:M} var<private> values:array<S,2>; @compute @workgroup_size(32) fn main(){var x:M; values[0].value=x;}";
        var module=WgslReader.Parse(source); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(SpirvWriter.Write(module)))));
        Assert.Equal(new TypeLayout(64,1024),TypeLayout.Of(new ShaderType.CooperativeMatrix(16,16,ShaderType.F32,CooperativeRole.C)));
    }

    [Fact]
    public void NativeNegationLowersToReferenceSupportedWgslScaling()
    {
        var module = WgslReader.Parse(Source); var body = module.Functions.Single(f => f.Stage is not null).Body;
        int index = body.Statements.FindLastIndex(s => s is Statement.Store { Value.Type: ShaderType.CooperativeMatrix });
        var store = (Statement.Store)body.Statements[index];
        body.Statements[index] = store with { Value = new Expression.Unary("-", store.Value, store.Value.Type) };
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.FNegate);
        string wgsl = WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()));
        Assert.Contains("-1f *", wgsl); ModuleValidator.Validate(WgslReader.Parse(wgsl));
    }

    [Fact]
    public void NativeScalarSplatRetainsValueAndReportsWgslLimitation()
    {
        var module = WgslReader.Parse(Source); var body = module.Functions.Single(f => f.Stage is not null).Body;
        int index = body.Statements.FindIndex(s => s is Statement.Declare { Name: "c" });
        var declaration = (Statement.Declare)body.Statements[index];
        body.Statements[index] = declaration with { Initializer = new Expression.Construct(declaration.Type, [new Expression.Literal(2f, ShaderType.F32)]) };
        var back = SpirvReader.Parse(SpirvWriter.Write(module)); ModuleValidator.Validate(back);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(back));
        var cooperativeTypes = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypeCooperativeMatrixKHR).Select(i => i.Operands[0]).ToHashSet();
        var splat = Assert.Single(binary.Instructions, i => (Op)i.Opcode is Op.CompositeConstruct or Op.ConstantComposite
            && i.Operands.Length == 3 && cooperativeTypes.Contains(i.Operands[0]));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Constant && i.Operands[1] == splat.Operands[2]
            && i.Operands[2] == BitConverter.SingleToUInt32Bits(2f));
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(back));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("scalar splat", error.Message);
    }

    [Fact]
    public void NativeMemoryReinterpretationIsNotSilentlyChangedToWgslConversion()
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(Source));
        var scalar = binary.Instructions.Single(i => (Op)i.Opcode == Op.TypeFloat && i.Operands[1] == 32);
        var integer = binary.Instructions.Single(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]);
        var instructions = new List<SpirvInstruction>(); bool inserted = false;
        foreach (var instruction in binary.Instructions)
        {
            if ((Op)instruction.Opcode == Op.TypeInt && instruction.Operands[0] == integer.Operands[0]) continue;
            if ((Op)instruction.Opcode == Op.TypeRuntimeArray && instruction.Operands[1] == scalar.Operands[0])
            {
                if (!inserted) { instructions.Add(integer); inserted = true; }
                instructions.Add(new(instruction.Opcode, [instruction.Operands[0], integer.Operands[0]]));
            }
            else if ((Op)instruction.Opcode == Op.TypePointer && instruction.Operands[1] == 12 && instruction.Operands[2] == scalar.Operands[0])
                instructions.Add(new(instruction.Opcode, [instruction.Operands[0], 12u, integer.Operands[0]]));
            else instructions.Add(instruction);
        }
        var back = SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = instructions }.ToBytes());
        ModuleValidator.Validate(back); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(back)));
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(back));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("reinterpretation", error.Message);
    }

    [Fact]
    public void ExplicitNoneMemoryOperandsDoNotChangeCooperativeOperations()
    {
        var input=SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(Source));
        var instructions=input.Instructions.Select(i=>(Op)i.Opcode is Op.CooperativeMatrixLoadKHR or Op.CooperativeMatrixStoreKHR
            ? new SpirvInstruction(i.Opcode,[..i.Operands,0u]) : i).ToArray();
        var binary=new SpirvBinary{Version=input.Version,Bound=input.Bound,Instructions=instructions};
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
        var aligned=instructions.Select(i=>(Op)i.Opcode==Op.CooperativeMatrixLoadKHR
            ? new SpirvInstruction(i.Opcode,[..i.Operands.Take(5),2u,4u]) : i).ToArray();
        var back=SpirvReader.Parse(new SpirvBinary{Version=input.Version,Bound=input.Bound,Instructions=aligned}.ToBytes());
        var emitted=SpirvBinary.Parse(SpirvWriter.Write(back));
        Assert.All(emitted.Instructions.Where(i=>(Op)i.Opcode==Op.CooperativeMatrixLoadKHR),i=>Assert.Equal(new uint[]{2,4},i.Operands[5..]));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(back)));
    }

    [Theory]
    [InlineData("coherent")]
    [InlineData("volatile")]
    public void VulkanMemoryModelDoesNotSilentlyDropBufferDecorations(string attribute)
    {
        var module=WgslReader.Parse(Source.Replace("@group(0) @binding(0)","@"+attribute+" @group(0) @binding(0)"));
        ModuleValidator.Validate(module); ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        if(attribute=="coherent")
        {
            var binary=SpirvBinary.Parse(SpirvWriter.Write(module));
            Assert.DoesNotContain(binary.Instructions,i=>(Op)i.Opcode==Op.Decorate&&i.Operands[1]==23);
            var constants=binary.Instructions.Where(i=>(Op)i.Opcode==Op.Constant).ToDictionary(i=>i.Operands[1],i=>i.Operands[2]);
            Assert.All(binary.Instructions.Where(i=>(Op)i.Opcode==Op.CooperativeMatrixLoadKHR),i=>
            { Assert.Equal(48u,i.Operands[5]); Assert.Equal(5u,constants[i.Operands[6]]); });
            string source=WgslWriter.Write(SpirvReader.Parse(binary.ToBytes())); Assert.Contains("@coherent",source);
            ModuleValidator.Validate(WgslReader.Parse(source));
        }
        else
        {
            var binary=SpirvBinary.Parse(SpirvWriter.Write(module));
            Assert.DoesNotContain(binary.Instructions,i=>(Op)i.Opcode==Op.Decorate&&i.Operands[1]==21);
            Assert.All(binary.Instructions.Where(i=>(Op)i.Opcode==Op.CooperativeMatrixLoadKHR),i=>Assert.Equal(1u,i.Operands[5]));
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
        }
    }
}
