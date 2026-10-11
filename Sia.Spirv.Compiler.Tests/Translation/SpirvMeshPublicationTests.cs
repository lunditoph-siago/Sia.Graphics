using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvMeshPublicationTests
{
    internal static Module PublicationFixture(string variant)
    {
        string source = MeshShaderTests.Source;
        if (variant == "Lines") source = source.Replace("@builtin(triangle_indices) indices:vec3u", "@builtin(line_indices) indices:vec2u")
            .Replace("output.p[0].indices=vec3u(0,1,2)", "output.p[0].indices=vec2u(0,1)");
        if (variant == "Points") source = source.Replace("@builtin(triangle_indices) indices:vec3u", "@builtin(point_index) indices:u32")
            .Replace("output.p[0].indices=vec3u(0,1,2)", "output.p[0].indices=0u");
        if (variant == "Specialized") source = source.Replace("struct Payload", "@id(3) override size=2u; struct Payload")
            .Replace("@workgroup_size(2)", "@workgroup_size(size,3,2)");
        if (variant == "Half") source = "enable f16;\n" + source.Replace("@location(0) color:vec4f", "@location(0) color:vec4h");
        var module = WgslReader.Parse(source);
        if (variant == "VulkanMemory") module.VulkanMemoryModel = true;
        return module;
    }

    [Theory]
    [InlineData("Triangles", 5298u)] [InlineData("Lines", 5269u)] [InlineData("Points", 27u)]
    [InlineData("Specialized", 5298u)] [InlineData("Half", 5298u)] [InlineData("VulkanMemory", 5298u)]
    public void PublicationPreservesTopologyAndTargetFields(string variant, uint topology)
    {
        var input = PublicationFixture(variant);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var publication = prepared.MeshPublications["mesh_main"];
        Assert.Equal(topology, publication.Topology);
        Assert.Equal(3u, publication.MaxVertices); Assert.Equal(1u, publication.MaxPrimitives);
        var binary = SpirvWriter.Emit(prepared);
        Assert.Contains(binary.Instructions, i => i.Opcode == 16 && i.Operands is [_, var mode] && mode == topology);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        if (variant == "Half") {
            var color = Assert.Single(publication.Function.Body.Statements.OfType<Statement.Loop>().First().Body.Statements.OfType<Statement.MeshStore>(),
                s => s.Field.Binding.Location == 0);
            Assert.Equal(new ShaderType.Vector(4, ShaderType.F16), color.Field.Type);
        }
        if (variant == "Specialized") {
            var step = Assert.Single(publication.Function.Body.Statements.OfType<Statement.Declare>(), d => d.Name.StartsWith("step", StringComparison.Ordinal));
            var multiply = Assert.IsType<Expression.Binary>(step.Initializer);
            var inner = Assert.IsType<Expression.Binary>(multiply.Left);
            Assert.Equal("size", Assert.IsType<Expression.Reference>(Assert.IsType<Expression.Convert>(inner.Left).Operand).Name);
            Assert.Equal(3, Assert.IsType<Expression.Literal>(Assert.IsType<Expression.Convert>(inner.Right).Operand).Value);
            Assert.Equal(2, Assert.IsType<Expression.Literal>(Assert.IsType<Expression.Convert>(multiply.Right).Operand).Value);
        }
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public void PublicationRequiresUniformControl(bool task, bool canonical, bool uniform)
    {
        var module = WgslReader.Parse(MeshShaderTests.Source);
        var entry = module.Functions.Single(f => f.Stage == (task ? ShaderStage.Task : ShaderStage.Mesh));
        entry.Body.Statements.Clear();
        var dimensions = new ShaderType.Vector(3, ShaderType.U32);
        var selected = new Block();
        selected.Statements.Add(task
            ? new Statement.TaskDispatch(new Expression.Construct(dimensions,
                [Expression.U32(1), Expression.U32(1), Expression.U32(1)]), entry.TaskPayload!)
            : new Statement.MeshSetOutputs(Expression.U32(1), Expression.U32(1)));
        Expression condition = uniform ? new Expression.Literal(true, ShaderType.Bool)
            : new Expression.Binary("==", new Expression.Reference(entry.Arguments[0].Name, ShaderType.U32), Expression.U32(0), ShaderType.Bool);
        entry.Body.Statements.Add(new Statement.If(condition, selected, new()));
        if (task) entry.Body.Statements.Add(new Statement.Return(new Expression.Construct(dimensions,
            [Expression.U32(0), Expression.U32(0), Expression.U32(0)])));
        void Check() {
            if (canonical) UniformityAnalysis.Validate(module);
            else UniformityAnalysis.Validate(module, new Dictionary<string, ControlFlowFunction>());
        }
        if (uniform) Check();
        else Assert.Contains("requires uniform control", Assert.Throws<ShaderException>(Check).Message);
    }

    [Fact]
    public void MeshPublicationBarrierAndDistributedCopiesExistBeforeSerialization()
    {
        var input = WgslReader.Parse(MeshShaderTests.Source);
        string before = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var finish = Assert.Single(prepared.Module.Functions,
            f => f.Name.StartsWith("sia_spv_mesh_finish_mesh_main", StringComparison.Ordinal));
        Assert.IsType<Statement.Barrier>(finish.Body.Statements[0]);
        Assert.Equal(2, finish.Body.Statements.OfType<Statement.Loop>().Count());
        Assert.True(StructuredControlFlowReader.TryRead(finish, prepared.Module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, prepared.Module);
        var writes = graph!.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.MeshStore).ToArray();
        Assert.Equal(5, writes.Length); Assert.All(writes, i => Assert.Equal(ShaderEffects.WriteMemory, i.Effects));
        Assert.Equal(ShaderEffects.WriteMemory | ShaderEffects.Convergent,
            Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.MeshSetOutputs).Effects);
        LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, prepared.Module);
        Assert.Equal(before, WgslWriter.Write(input, SpirvCompilationTarget.Default));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void TaskDispatchHasAnExplicitPreparedTerminationBody()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(MeshShaderTests.Source), null, true, true, true);
        var finish = Assert.Single(prepared.Module.Functions,
            f => f.Name.StartsWith("sia_spv_mesh_finish_task_main", StringComparison.Ordinal));
        Assert.IsType<ShaderType.Void>(finish.ReturnType);
        Assert.Equal(new ShaderType.Vector(3, ShaderType.U32), Assert.Single(finish.Arguments).Type);
        Assert.IsType<Statement.TaskDispatch>(finish.Body.Statements.Last());
        Assert.True(StructuredControlFlowReader.TryRead(finish, prepared.Module, out var graph, out var reason), reason);
        ControlFlowVerifier.Validate(graph!, prepared.Module);
        var dispatch = Assert.IsType<ControlFlowTerminator.TaskDispatch>(Assert.Single(graph!.Blocks).Terminator);
        Assert.Equal(ShaderBuiltinEffects.Barrier(true) | ShaderEffects.InvocationTermination, dispatch.Effects);
        Assert.Contains("task-dispatch", ControlFlowPrinter.Write(graph));
    }

    [Fact]
    public void PublicationNamesDoNotShadowTheBorrowedOutputGlobal()
    {
        string source = MeshShaderTests.Source.Replace("var<workgroup> output:Output", "var<workgroup> vertex_count:Output")
            .Replace("@mesh(output)", "@mesh(vertex_count)").Replace("output.", "vertex_count.");
        var input = WgslReader.Parse(source);
        input.Functions.Add(new ShaderFunction("sia_spv_mesh_finish_mesh_main"));
        string before = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var finish = prepared.MeshPublications["mesh_main"].Function;
        Assert.NotEqual("sia_spv_mesh_finish_mesh_main", finish.Name);
        Assert.DoesNotContain(finish.Body.Statements.OfType<Statement.Declare>(), d => d.Name == "vertex_count");
        Assert.Equal(before, WgslWriter.Write(input, SpirvCompilationTarget.Default));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedVerifierRejectsMalformedTaskDispatch(bool missingPayload)
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(MeshShaderTests.Source), null, true, true, true);
        var finish = prepared.MeshPublications["task_main"].Function;
        Assert.True(StructuredControlFlowReader.TryRead(finish, prepared.Module, out var graph, out var reason), reason);
        var block = Assert.Single(graph!.Blocks);
        var dispatch = Assert.IsType<ControlFlowTerminator.TaskDispatch>(block.Terminator);
        block.Terminator = missingPayload ? dispatch with { Payload = "missing" }
            : dispatch with { Dimensions = dispatch.Dimensions with { Type = ShaderType.U32 } };
        Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, prepared.Module));
    }

    [Fact]
    public void TaskPublicationCannotBeCalledFromAComputeEntry()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(MeshShaderTests.Source), null, true, true, true);
        var invalid = new ShaderFunction("invalid") { Stage = ShaderStage.Compute };
        invalid.Body.Statements.Add(new Statement.Evaluate(new Expression.Call(prepared.MeshPublications["task_main"].Function.Name,
            [new Expression.Construct(new ShaderType.Vector(3, ShaderType.U32), [Expression.U32(1), Expression.U32(1), Expression.U32(1)])], new ShaderType.Void(), CallBinding.Function) ));
        prepared.Module.Functions.Add(invalid);
        var canonical = prepared.PhysicalLayout.Canonical;
        var deferred = canonical.DeferredFunctions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        deferred.Add(invalid.Name, "legacy negative stage fixture");
        Assert.Contains("unavailable in its stage", Assert.Throws<ShaderException>(() => ModuleValidator.Validate(canonical with {
            DeferredFunctions = deferred, EntryFunctions = canonical.EntryFunctions.Append(invalid.Name).ToHashSet(StringComparer.Ordinal)
        })).Message);
    }
}
