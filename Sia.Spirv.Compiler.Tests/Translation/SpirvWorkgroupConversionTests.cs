using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvWorkgroupConversionTests
{
    internal const string NestedSource = """
        struct Element{value:u32,pair:vec2u,}
        struct Data{elements:array<Element,2>,tail:vec4u,}
        @group(0) @binding(0) var<storage,read_write> output:Data;
        @group(0) @binding(1) var<storage,read_write> counter:array<u32>;
        var<workgroup> group_data:Data;
        fn make_data()->Data{counter[0]+=1u;return Data(array<Element,2>(Element(11u,vec2u(12u,13u)),Element(21u,vec2u(22u,23u))),vec4u(31u,32u,33u,34u));}
        @compute @workgroup_size(1) fn main(){group_data=make_data();workgroupBarrier();output=group_data;}
        """;

    [Fact]
    public void PhysicalAggregateConversionBodiesArePreparedAndCanonicallyVerifiedBeforeEmission()
    {
        var input = WgslReader.Parse(SpirvPhysicalLayoutTests.SharedSource);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var logical = input.Globals.Single(g => g.Name == "group_data").Type;
        var physical = prepared.PhysicalLayout.WorkgroupTypes[logical];
        var helpers = prepared.Module.Functions.Where(f => f.Name.StartsWith("sia_spv_workgroup_", StringComparison.Ordinal)
            && f.Arguments.Count == 1 && f.Arguments[0].Type is not ShaderType.Scalar).ToArray();
        Assert.Equal(2, helpers.Length);
        var to = Assert.Single(helpers, f => f.ReturnType == physical);
        var from = Assert.Single(helpers, f => f.ReturnType == logical);
        Assert.Equal(logical, Assert.Single(to.Arguments).Type);
        Assert.Equal(physical, Assert.Single(from.Arguments).Type);
        foreach (var helper in helpers) {
            Assert.Null(helper.Stage);
            Assert.Empty(helper.Body.Statements);
            var graph = prepared.PhysicalLayout.ControlFlow[helper.Name].Graph;
            ControlFlowVerifier.Validate(graph, prepared.Module);
            Assert.All(graph.Blocks.SelectMany(b => b.Instructions), i => Assert.Equal(ShaderEffects.None, i.Effects));
        }
        var binary = SpirvWriter.Emit(prepared);
        Assert.True(binary.Instructions.Count(i => (Op)i.Opcode == Op.FunctionCall) >= 4);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void RawPreparationOwnsConversionFunctionsAndPreservesBorrowedModule()
    {
        var input = WgslReader.Parse(SpirvPhysicalLayoutTests.SharedSource);
        var functions = input.Functions.ToArray(); string before = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        Assert.NotSame(input, prepared.Module);
        Assert.Equal(functions, input.Functions);
        Assert.Equal(before, WgslWriter.Write(input, SpirvCompilationTarget.Default));
        // Uniform legalization can also add reader functions to this module.
        Assert.Equal(2, prepared.WorkgroupConversions.Count);
        Assert.All(prepared.WorkgroupConversions.Values, helper => Assert.Contains(prepared.Module.Functions,
            f => f.Name == helper.Name && f.ReturnType == helper.ReturnType));
        byte[] bytes = SpirvWriter.Emit(prepared).ToBytes();
        Assert.Equal(bytes, SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(input)).ToBytes());
        ModuleValidator.Validate(SpirvReader.Parse(bytes));
    }

    [Fact]
    public void NestedArrayConversionRetainsOneUserCallAndTypedMemberConstruction()
    {
        var input = WgslReader.Parse(NestedSource);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        Assert.Equal(6, prepared.PhysicalLayout.WorkgroupConversions.Count);
        foreach (var pair in prepared.PhysicalLayout.WorkgroupConversions) {
            ShaderType physical = prepared.PhysicalLayout.WorkgroupTypes[pair.Key.Logical];
            Assert.Equal(pair.Key.ToPhysical ? physical : pair.Key.Logical, pair.Value.ReturnType);
            Assert.Equal(pair.Key.ToPhysical ? pair.Key.Logical : physical, Assert.Single(pair.Value.Arguments).Type);
        }
        var binary = SpirvWriter.Emit(prepared);
        var make = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Name
            && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "make_data").Operands[0];
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.FunctionCall && i.Operands[2] == make);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void ConversionHelperNamesCannotCaptureUserFunctions()
    {
        var input = WgslReader.Parse(SpirvPhysicalLayoutTests.SharedSource);
        var user = new ShaderFunction("sia_spv_workgroup_to_0") { ReturnType = ShaderType.U32 };
        user.Body.Statements.Add(new Statement.Return(Expression.U32(77))); input.Functions.Add(user);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        Assert.Same(user, prepared.Module.Functions.Single(f => f.Name == user.Name));
        Assert.DoesNotContain(prepared.WorkgroupConversions.Values, f => f.Name == user.Name);
        Assert.Contains(prepared.WorkgroupConversions.Values, f => f.Name == user.Name + "_1");
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }
}
