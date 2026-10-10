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

public class CanonicalPipelineConstantTests
{
    private const string Resources = "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";

    [Fact]
    public void TargetResourceValidationUsesGraphCallClosureAfterConstantResolution()
    {
        var input = WgslReader.Parse("override n=2u;" + Resources + "fn helper(){outputs[0]=n;} @compute @workgroup_size(1) fn main(){helper();}");
        var lowered = ShaderTargetLowering.PrepareSpirv(CanonicalShaderPipeline.Prepare(input), new Dictionary<string, double> { ["n"] = 3 });
        Assert.All(lowered.Declarations.Functions, f => Assert.Empty(f.Body.Statements));
        var prepared = SpirvEntryPointLowering.Run(lowered, true, true);
        var target = SpirvCompilationTarget.Default with { ResourceLimits = SpirvTargetProfile.Default with { SupportsStorageBuffers = false } };
        Assert.Contains("binding limit", Assert.Throws<ShaderException>(() => ShaderTargetValidator.ValidateModule(prepared.PhysicalLayout.Canonical, target)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DependentValuesAndLocalShadowingResolveWithoutReadingBorrowedBodies(bool shadowing)
    {
        string source = shadowing
            ? "@id(7) override value=2u;" + Resources + "fn helper(value:u32)->u32{return value;} @compute @workgroup_size(1) fn main(){{let value=9u; outputs[0]=helper(value);}outputs[1]=value;}"
            : "@id(7) override count=2u; override scale=count*2u; var<private> initial:u32=count+1u;" + Resources + "@compute @workgroup_size(count) fn main(){var sum=0u; for(var i=0u;i<count;i++){sum+=i;}outputs[0]=sum;outputs[1]=scale;}";
        var input = WgslReader.Parse(source); var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Empty(canonical.DeferredFunctions);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var bodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
        try {
            foreach (var function in input.Functions) { function.Body = new(); function.Body.Statements.Add(new Statement.Return(Expression.U32(999))); }
            var lowered = ShaderTargetLowering.PrepareSpirv(canonical, new Dictionary<string, double> { ["7"] = 4.9 });
            ModuleValidator.Validate(lowered);
            Assert.All(lowered.Declarations.Constants, c => { Assert.False(c.IsOverride); Assert.False(c.IsSpecialization); Assert.Null(c.OverrideId); });
            foreach (var pair in canonical.Functions) {
                Assert.Equal(before[pair.Key], ControlFlowPrinter.Write(pair.Value));
                var actual = lowered.Functions[pair.Key]; Assert.NotSame(pair.Value, actual);
                Assert.Equal(pair.Value.Loops, actual.Loops);
                Assert.Equal(pair.Value.Blocks.Select(b => b.Id), actual.Blocks.Select(b => b.Id));
            }
            if (shadowing) Assert.Contains(lowered.Functions["helper"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Symbol { Name: "value" });
            else {
                Assert.Equal(5u, Assert.IsType<Expression.Literal>(Assert.Single(lowered.Declarations.Globals, g => g.Space == AddressSpace.Private).Initializer).Value);
                Assert.Equal(4u, Assert.IsType<Expression.Literal>(lowered.Declarations.Functions.Single(f => f.Stage is not null).WorkgroupSize[0]).Value);
            }
            var prepared = SpirvEntryPointLowering.Run(lowered, true, true);
            var binary = SpirvWriter.Emit(prepared);
            Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode is Op.SpecConstant or Op.SpecConstantComposite or Op.SpecConstantOp);
            Assert.Equal(shadowing ? new uint[] { 9, 4 } : [6, 8], new CanonicalExecution(SpirvReader.Parse(binary.ToBytes()), []).Run().Output);
        }
        finally { foreach (var function in input.Functions) function.Body = bodies[function.Name]; }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeInlineSpecializationAndConcreteCompositeRetainTypedSsaChildren(bool namedComposite)
    {
        var bytes = SpirvWriter.Write(WgslReader.Parse("@id(7) override first=1u; @id(8) override second=2u;" + Resources
            + "@compute @workgroup_size(1) fn main(){let pair=vec2u(first,second);outputs[0]=pair.x;outputs[1]=pair.y;}"));
        var binary = SpirvBinary.Parse(bytes); var composite = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.CompositeConstruct);
        var instructions = binary.Instructions.ToList(); instructions.Remove(composite);
        instructions.Insert(instructions.FindIndex(i => (Op)i.Opcode == Op.Function), new((ushort)Op.SpecConstantComposite, composite.Operands));
        var input = SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = instructions }.ToBytes());
        var canonical = CanonicalShaderPipeline.Prepare(input);
        var graph = Assert.Single(canonical.Functions.Values, g => g.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Construct && i.Result?.Type is ShaderType.Vector));
        // The reader may duplicate an inline specialization at each use.
        var old = graph.Blocks.SelectMany(b => b.Instructions).First(i => i.Operation is ValueOperation.Construct && i.Result?.Type is ShaderType.Vector);
        if (namedComposite) {
            // The native reader imports specialization composites inline.
            // Also exercise a valid named, concrete composite in typed IR.
            var type = old.Result!.Value.Type;
            input.Constants.Add(new ShaderConstant("sia_test_pair", type, new Expression.Construct(type,
                [Expression.U32(7), Expression.U32(9)])));
            var block = graph.Blocks.Single(b => b.Instructions.Contains(old));
            old = old with { Operation = new ValueOperation.Symbol("sia_test_pair"), Span = new(31, 3) };
            block.Instructions[block.Instructions.FindIndex(i => i.Result == old.Result)] = old;
        }
        ModuleValidator.Validate(canonical); string before = ControlFlowPrinter.Write(graph);
        var lowered = PipelineConstantResolver.Resolve(canonical, new Dictionary<string, double> { ["7"] = 7, ["8"] = 9 });
        var actual = Assert.Single(lowered.Functions[graph.Signature.Name].Blocks.SelectMany(b => b.Instructions), i => i.Result == old.Result);
        var construct = Assert.IsType<ValueOperation.Construct>(actual.Operation); Assert.Equal(old.Span, actual.Span); Assert.Equal(old.DiagnosticFilters, actual.DiagnosticFilters);
        var definitions = lowered.Functions[graph.Signature.Name].Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        Assert.Equal(new object[] { 7u, 9u }, construct.Components.Select(v => Assert.IsType<ValueOperation.Literal>(definitions[v.Id].Operation).Value));
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
        var prepared = SpirvEntryPointLowering.Run(lowered, true, true);
        Assert.Equal(new uint[] { 7, 9 }, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()), []).Run().Output);
    }

    [Theory]
    [InlineData("n", 3u)] [InlineData("n+1u", 4u)] [InlineData("max(n,5u)", 5u)] [InlineData("n*2u", 6u)]
    public void OverrideArrayTypesMapCapturedAddressesAndLeaveNativeMemoryOperandsIntact(string length, uint expected)
    {
        var input = WgslReader.Parse("@id(7) override n=2u; var<workgroup> data:array<u32," + length + ">; @compute @workgroup_size(n) fn main(){data[0]=n;}");
        var canonical = CanonicalShaderPipeline.Prepare(input); Assert.Empty(canonical.DeferredFunctions);
        var graph = canonical.Functions["main"];
        foreach (var block in graph.Blocks)
            for (int i = 0; i < block.Instructions.Count; i++)
                if (block.Instructions[i].Operation is ValueOperation.Store store)
                    block.Instructions[i] = block.Instructions[i] with { Operation = store with { MemoryAccess = new(1) }, Span = new(31, 3) };
        string before = ControlFlowPrinter.Write(graph);
        var lowered = PipelineConstantResolver.Resolve(canonical, new Dictionary<string, double> { ["7"] = 3 });
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
        Assert.Equal(expected, Assert.IsType<ShaderType.Array>(Assert.Single(lowered.Declarations.Globals).Type).Length);
        Assert.All(lowered.Functions["main"].Blocks.SelectMany(b => b.Instructions).Where(i => i.Result?.Type is ShaderType.Pointer { Base: ShaderType.Array }),
            i => Assert.Equal(expected, ((ShaderType.Array)((ShaderType.Pointer)i.Result!.Value.Type).Base).Length));
        var actual = Assert.Single(lowered.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Store);
        Assert.Equal(new SourceSpan(31, 3), actual.Span); Assert.Equal(1u, Assert.IsType<ValueOperation.Store>(actual.Operation).MemoryAccess!.Flags);
        var prepared = SpirvEntryPointLowering.Run(lowered, true, true, false);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData("size", 2d)] [InlineData("unknown", 2d)] [InlineData("7", double.NaN)]
    [InlineData("7", double.PositiveInfinity)] [InlineData("7", -1d)] [InlineData("7", 4294967296d)] [InlineData("7", 0d)]
    public void FailedResolutionRetainsBorrowedGraphsAndExactPublicDiagnostics(string key, double value)
    {
        var input = WgslReader.Parse("@id(7) override size:u32; @compute @workgroup_size(size) fn main(){}");
        var canonical = CanonicalShaderPipeline.Prepare(input); string before = ControlFlowPrinter.Write(canonical.Functions["main"]);
        var values = new Dictionary<string, double> { [key] = value };
        var old = Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(input, values));
        var actual = Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(canonical, values));
        Assert.Equal(old.Diagnostic, actual.Diagnostic); Assert.Equal(before, ControlFlowPrinter.Write(canonical.Functions["main"]));
        Assert.True(Assert.Single(input.Constants).IsOverride); Assert.Null(input.Constants[0].Value);
    }

    [Theory]
    [InlineData(double.NaN, false)] [InlineData(0d, false)] [InlineData(double.PositiveInfinity, true)] [InlineData(-2d, true)]
    public void BooleanConversionsRetainTheirTruthTableInCanonicalBranches(double number, bool expected)
    {
        var input = WgslReader.Parse("override enabled:bool;" + Resources
            + "@compute @workgroup_size(1) fn main(){if(enabled){outputs[0]=1u;}else{outputs[0]=2u;}outputs[1]=3u;}");
        var canonical = CanonicalShaderPipeline.Prepare(input);
        var lowered = ShaderTargetLowering.PrepareSpirv(canonical, new Dictionary<string, double> { ["enabled"] = number });
        var prepared = SpirvEntryPointLowering.Run(lowered, true, true);
        Assert.Equal(new uint[] { expected ? 1u : 2u, 3 }, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()), []).Run().Output);
    }

    [Theory]
    [InlineData("i16", typeof(short))] [InlineData("u16", typeof(ushort))]
    [InlineData("i32", typeof(int))] [InlineData("u32", typeof(uint))]
    [InlineData("i64", typeof(long))] [InlineData("u64", typeof(ulong))]
    public void ConcreteScalarWidthsArePreservedInResolvedSsaPayloads(string scalar, Type payloadType)
    {
        string enable = scalar.EndsWith("16", StringComparison.Ordinal) ? "enable wgpu_int16;" : "";
        bool wide = scalar.EndsWith("64", StringComparison.Ordinal);
        var input = WgslReader.Parse(enable + "override value:" + (wide ? "u32" : scalar) + "; @compute @workgroup_size(1) fn main(){let local=value;}");
        if (wide) {
            // WGSL forbids 64-bit overrides; native typed IR supports them.
            var type = new ShaderType.Scalar(scalar == "i64" ? ScalarKind.Sint : ScalarKind.Uint, 8);
            input.Constants[0] = new ShaderConstant("value", type, null) { IsOverride = true };
            input.Functions[0].Body = new();
            input.Functions[0].Body.Statements.Add(new Statement.Declare("local", type, new Expression.Reference("value", type), false));
        }
        var canonical = CanonicalShaderPipeline.Prepare(input);
        var old = Assert.Single(canonical.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Symbol { Name: "value" });
        var lowered = PipelineConstantResolver.Resolve(canonical, new Dictionary<string, double> { ["value"] = 17.9 });
        var actual = Assert.Single(lowered.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Result == old.Result);
        var literal = Assert.IsType<ValueOperation.Literal>(actual.Operation);
        Assert.Equal(payloadType, literal.Value.GetType()); Assert.Equal(17, Convert.ToInt32(literal.Value));
        var prepared = SpirvEntryPointLowering.Run(lowered, true, true);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData("sampler")] [InlineData("texture_2d<f32>")]
    public void BindingArrayLengthMapsDeclarationsAndCapturedHandleValues(string element)
    {
        var input = WgslReader.Parse("enable wgpu_binding_array; @id(7) override n=2u; @group(0) @binding(0) var data:binding_array<"
            + element + ",n>; @compute @workgroup_size(1) fn main(){}");
        var main = input.Functions[0]; var graph = new ControlFlowFunction(main); var block = graph.Block(); graph.Entry = block.Id;
        var type = Assert.IsType<ShaderType.BindingArray>(input.Globals[0].Type);
        var rootValue = graph.Value(type); var index = graph.Value(ShaderType.U32); var handle = graph.Value(type.Element);
        block.Instructions.Add(new(rootValue, new ValueOperation.Symbol("data")));
        block.Instructions.Add(new(index, new ValueOperation.Literal(1u)));
        block.Instructions.Add(new(handle, new ValueOperation.Access(rootValue, index))); block.Terminator = new ControlFlowTerminator.Return();
        var canonical = new CanonicalModule(input, new Dictionary<string, ControlFlowFunction> { [main.Name] = graph }, new Dictionary<string, string>(), new HashSet<string> { main.Name });
        ModuleValidator.Validate(canonical); string before = ControlFlowPrinter.Write(graph);
        var lowered = PipelineConstantResolver.Resolve(canonical, new Dictionary<string, double> { ["7"] = 4 });
        Assert.Equal(4u, Assert.IsType<ShaderType.BindingArray>(Assert.Single(lowered.Declarations.Globals).Type).Length);
        var root = Assert.Single(lowered.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Symbol { Name: "data" });
        Assert.Equal(4u, Assert.IsType<ShaderType.BindingArray>(root.Result!.Value.Type).Length);
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(SpirvEntryPointLowering.Run(lowered, true, true)).ToBytes()));
    }

    [Theory]
    [InlineData("sampler")] [InlineData("texture_2d<f32>")]
    public void OpaqueLocalFrontendDeferralRemainsExplicitWhileResolvingBindingArrayDeclarations(string element)
    {
        var input = WgslReader.Parse("enable wgpu_binding_array; @id(7) override n=2u; @group(0) @binding(0) var data:binding_array<"
            + element + ",n>; @compute @workgroup_size(1) fn main(){_=data[1];}");
        var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Equal("Declare", canonical.DeferredFunctions["main"]);
        var lowered = PipelineConstantResolver.Resolve(canonical, new Dictionary<string, double> { ["7"] = 4 });
        Assert.Equal(canonical.DeferredFunctions, lowered.DeferredFunctions);
        Assert.Equal(4u, Assert.IsType<ShaderType.BindingArray>(lowered.Declarations.Globals[0].Type).Length);
        Assert.NotEmpty(lowered.Declarations.Functions[0].Body.Statements);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PointerBlockArgumentsAndIncomingEdgesMapTheSameResolvedAggregateType(bool chooseFirst)
    {
        var input = WgslReader.Parse("@id(7) override n=2u; var<workgroup> first:array<u32,n>; var<workgroup> second:array<u32,n>; @compute @workgroup_size(n) fn main(){}");
        var main = Assert.Single(input.Functions); var graph = new ControlFlowFunction(main);
        var entry = graph.Block(); var accept = graph.Block(); var reject = graph.Block(); var merge = graph.Block(); graph.Entry = entry.Id;
        SsaValue Emit(ShaderType type, ValueOperation operation) { var value = graph.Value(type); entry.Instructions.Add(new(value, operation)); return value; }
        var arrayPointer = new ShaderType.Pointer(input.Globals[0].Type, AddressSpace.Workgroup);
        var first = Emit(arrayPointer, new ValueOperation.Symbol("first")); var second = Emit(arrayPointer, new ValueOperation.Symbol("second"));
        var value = Emit(ShaderType.U32, new ValueOperation.Symbol("n")); var index = Emit(ShaderType.U32, new ValueOperation.Literal(0u));
        entry.Terminator = new ControlFlowTerminator.Conditional(Emit(ShaderType.Bool, new ValueOperation.Literal(chooseFirst)), new(accept.Id), new(reject.Id));
        var selected = graph.Value(arrayPointer); merge.Parameters.Add(selected);
        accept.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [first])); reject.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [second]));
        var address = graph.Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Workgroup));
        merge.Instructions.Add(new(address, new ValueOperation.Access(selected, index))); merge.Instructions.Add(new(null, new ValueOperation.Store(address, value, new(1)), new(31, 3)));
        merge.Terminator = new ControlFlowTerminator.Return(); graph.SelectionMerges.Add(entry.Id, merge.Id);
        var canonical = new CanonicalModule(input, new Dictionary<string, ControlFlowFunction> { [main.Name] = graph }, new Dictionary<string, string>(), new HashSet<string> { main.Name });
        ModuleValidator.Validate(canonical); string before = ControlFlowPrinter.Write(graph);
        var lowered = PipelineConstantResolver.Resolve(canonical, new Dictionary<string, double> { ["7"] = 3 });
        Assert.Equal(before, ControlFlowPrinter.Write(graph)); Assert.Empty(main.Body.Statements);
        var mapped = lowered.Functions[main.Name]; var parameter = Assert.Single(mapped.Blocks.Single(b => b.Id == merge.Id).Parameters);
        Assert.Equal(3u, Assert.IsType<ShaderType.Array>(Assert.IsType<ShaderType.Pointer>(parameter.Type).Base).Length);
        Assert.All(mapped.Blocks.SelectMany(b => b.Terminator!.Edges).Where(e => e.Target == merge.Id), e => Assert.Equal(parameter.Type, Assert.Single(e.Arguments).Type));
        var binary = SpirvWriter.Emit(SpirvEntryPointLowering.Run(lowered, true, true, false));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && (i.Operands[2] & 1) != 0);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }
}
