using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvWorkgroupAccessTests
{
    [Fact]
    public void RawCompatibilityCallsRetainUserFunctionIdentity()
    {
        var module = WgslReader.Parse("struct Data{v:vec2u,} var<workgroup> group_data:Data; @group(0) @binding(0) var<storage,read_write> output:Data; fn workgroupUniformLoad(p:ptr<workgroup,Data>)->Data{return *p;} @compute @workgroup_size(1) fn main(){output=workgroupUniformLoad(&group_data);}");
        var main = module.Functions.Single(f => f.Stage == ShaderStage.Compute);
        var declaration = Assert.Single(main.Body.Statements.OfType<Statement.Declare>());
        int position = main.Body.Statements.IndexOf(declaration);
        main.Body.Statements[position] = declaration with { Initializer = Assert.IsType<Expression.Call>(declaration.Initializer) with { Binding = CallBinding.Unresolved } };
        ModuleValidator.ValidateNative(module);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(module);
        ModuleValidator.ValidateNative(prepared.Module);
        var call = Assert.IsType<Expression.Call>(Assert.Single(prepared.Module.Functions.Single(f => f.Stage == ShaderStage.Compute).Body.Statements.OfType<Statement.Declare>()).Initializer);
        Assert.Equal("workgroupUniformLoad", call.Function);
        Assert.Equal(module.Structures.Single(), call.Type);
        Assert.Equal(CallBinding.Unresolved, call.Binding);
    }

    [Fact]
    public void RawImplicitSwizzleReadsThePhysicalVectorAsAValue()
    {
        var module = WgslReader.Parse("struct Data{v:vec2u,} var<workgroup> group_data:Data; @group(0) @binding(0) var<storage,read_write> output:Data; @compute @workgroup_size(1) fn main(){}");
        var data = module.Structures.Single(); var vector = new ShaderType.Vector(2, ShaderType.U32);
        var source = new Expression.Member(new Expression.Reference("group_data", data), "v", vector);
        var target = new Expression.Member(new Expression.Reference("output", new ShaderType.Pointer(data, AddressSpace.Storage)), "v", new ShaderType.Pointer(vector, AddressSpace.Storage));
        module.Functions.Single().Body.Statements.Add(new Statement.Store(target, new Expression.Swizzle(source, "yx", vector)));
        var prepared = SpirvPhysicalLayoutLowering.Prepare(module);
        ModuleValidator.Validate(prepared.Module);
        var store = Assert.IsType<Statement.Store>(Assert.Single(prepared.Module.Functions.Single(f => f.Stage == ShaderStage.Compute).Body.Statements));
        var swizzle = Assert.IsType<Expression.Swizzle>(store.Value);
        Assert.Equal(vector, swizzle.Type);
        Assert.IsType<Expression.Load>(swizzle.Vector);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void InitializerMappingIdentifiesTheFinalPreparedFunction()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(SpirvWorkgroupConversionTests.NestedSource), null, true, true, true);
        var initializer = prepared.WorkgroupInitializers["main"];
        Assert.Same(prepared.Module.Functions.Single(f => f.Name == initializer.Name), initializer);
    }

    internal const string AliasSource = """
        struct Data{values:array<u32,2>,tail:u32,}
        @group(0) @binding(0) var<storage,read_write> output:Data;
        @group(0) @binding(1) var<storage,read_write> counter:array<u32>;
        var<workgroup> group_data:Data;
        fn get_index()->u32{counter[0]+=1u;return counter[1];}
        @compute @workgroup_size(1) fn main(){
            group_data=Data(array<u32,2>(11u,22u),33u);
            let p=&group_data.values[get_index()];
            let selected=*p; *p=selected+100u;
            {let group_data=Data(array<u32,2>(3u,4u),5u);output=group_data;}
            output=group_data;
        }
        """;
    internal const string UniformSource = """
        struct Element{value:u32,pair:vec2u,}
        struct Data{elements:array<Element,2>,tail:vec4u,}
        @group(0) @binding(0) var<storage,read_write> output:Data;
        @group(0) @binding(1) var<storage,read_write> counter:array<u32>;
        var<workgroup> group_data:Data;
        fn make_data()->Data{counter[0]+=1u;return Data(array<Element,2>(Element(11u,vec2u(12u,13u)),Element(21u,vec2u(22u,23u))),vec4u(31u,32u,33u,34u));}
        @compute @workgroup_size(1) fn main(){group_data=make_data();workgroupBarrier();output=workgroupUniformLoad(&group_data);}
        """;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UniformLoadHasPhysicalReadAndExplicitLogicalConversion(bool vulkan)
    {
        var input = WgslReader.Parse(UniformSource); input.VulkanMemoryModel = vulkan;
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var logical = input.Globals.Single(g => g.Name == "group_data").Type;
        var physical = prepared.PhysicalLayout.WorkgroupTypes[logical];
        var main = prepared.Module.Functions.Single(f => f.Stage == ShaderStage.Compute);
        Assert.True(StructuredControlFlowReader.TryRead(main, prepared.Module, out var graph, out var reason), reason);
        ControlFlowVerifier.Validate(graph!, prepared.Module);
        var instructions = graph!.Blocks.SelectMany(b => b.Instructions).ToArray();
        Assert.DoesNotContain(instructions, i => i.Operation is ValueOperation.Builtin { Function: "workgroupUniformLoad" });
        var uniform = Assert.Single(instructions, i => i.Operation is ValueOperation.Load && i.Result?.Type == physical);
        Assert.Equal(physical, uniform.Result!.Value.Type);
        var captured = new HashSet<SsaValue> { uniform.Result.Value };
        foreach (var instruction in instructions)
            if (instruction.Operation is ValueOperation.Let l && captured.Contains(l.Value) && instruction.Result is { } value) captured.Add(value);
        Assert.Contains(instructions, i => i.Operation is ValueOperation.Call c && c.Arguments.Any(captured.Contains)
            && c.ReturnType == logical && c.Function.StartsWith("sia_spv_workgroup_from_", StringComparison.Ordinal));
        int loadIndex = Array.IndexOf(instructions, uniform);
        Assert.Contains(instructions.Take(loadIndex), i => i.Operation is ValueOperation.Barrier { NativeMemory: { ExecutionScope: 2 } });
        Assert.Contains(instructions.Skip(loadIndex + 1), i => i.Operation is ValueOperation.Barrier { NativeMemory: { ExecutionScope: 2 } });
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void CapturedAliasEvaluatesItsIndexOnceAndPreservesLexicalShadowing()
    {
        var input = WgslReader.Parse(AliasSource); string before = WgslWriter.Write(input);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var main = prepared.Module.Functions.Single(f => f.Stage == ShaderStage.Compute);
        Assert.True(StructuredControlFlowReader.TryRead(main, prepared.Module, out var graph, out var reason), reason);
        ControlFlowVerifier.Validate(graph!, prepared.Module);
        Assert.Single(graph!.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call { Function: "get_index" });
        Assert.Equal(before, WgslWriter.Write(input));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void WorkgroupGlobalAndOrderedAccessesHavePhysicalTypesBeforeSerialization()
    {
        var input = WgslReader.Parse(SpirvWorkgroupConversionTests.NestedSource);
        string before = WgslWriter.Write(input);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var logical = input.Globals.Single(g => g.Name == "group_data").Type;
        var physical = prepared.PhysicalLayout.WorkgroupTypes[logical];
        Assert.NotEqual(logical, physical);
        Assert.Equal(physical, prepared.Module.Globals.Single(g => g.Name == "group_data").Type);
        var main = prepared.Module.Functions.Single(f => f.Name == "main");
        Assert.True(StructuredControlFlowReader.TryRead(main, prepared.Module, out var graph, out var reason), reason);
        ControlFlowVerifier.Validate(graph!, prepared.Module);
        var instructions = graph!.Blocks.SelectMany(b => b.Instructions).ToArray();
        var calls = instructions.Where(i => i.Operation is ValueOperation.Call c && c.Function.StartsWith("sia_spv_workgroup_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains(instructions, i => i.Operation is ValueOperation.Store s && s.Pointer.Type is ShaderType.Pointer { Space: AddressSpace.Workgroup } p && p.Base == physical && s.Value.Type == physical);
        Assert.Contains(instructions, i => i.Operation is ValueOperation.Load l && l.Pointer.Type is ShaderType.Pointer { Space: AddressSpace.Workgroup } p && p.Base == physical && i.Result!.Value.Type == physical);
        Assert.Equal(before, WgslWriter.Write(input));
        Assert.Equal(logical, input.Globals.Single(g => g.Name == "group_data").Type);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void PhysicalWorkgroupReadsAreConvertedInThePreparedBody()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(SpirvPhysicalLayoutTests.SharedSource), null, true, true, true);
        var main = prepared.Module.Functions.Single(f => f.Stage == ShaderStage.Compute);
        Assert.True(StructuredControlFlowReader.TryRead(main, prepared.Module, out var graph, out var reason), reason);
        var calls = graph!.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Call c
            && c.Function.StartsWith("sia_spv_workgroup_from_", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(calls);
    }
}
