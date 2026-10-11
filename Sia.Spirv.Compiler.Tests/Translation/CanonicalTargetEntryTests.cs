using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalTargetEntryTests
{
    private const string Loop = "@group(0) @binding(1) var<storage,read_write> output:array<u32>; @compute @workgroup_size(1) fn main(){var sum=0u; for(var i=0u;i<4u;i++){sum+=i;} output[0]=sum;}";

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TargetDeferredPointerMergesAdaptTheGraphRatherThanTheBorrowedBody(bool chooseFirst)
    {
        var input = WgslReader.Parse("@group(0) @binding(1) var<storage,read_write> output:array<u32>; @compute @workgroup_size(1) fn main(){}");
        var main = Assert.Single(input.Functions); var graph = new ControlFlowFunction(main);
        var entry = graph.Block(); var accept = graph.Block(); var reject = graph.Block(); var merge = graph.Block(); graph.Entry = entry.Id;
        SsaValue Emit(ShaderType type, ValueOperation operation) {
            var created = graph.Value(type); entry.Instructions.Add(new(created, operation)); return created;
        }
        var global = Assert.Single(input.Globals); var pointerType = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage);
        var root = Emit(new ShaderType.Pointer(global.Type, global.Space), new ValueOperation.Symbol(global.Name));
        var first = Emit(pointerType, new ValueOperation.Access(root, Emit(ShaderType.U32, new ValueOperation.Literal(0u))));
        var second = Emit(pointerType, new ValueOperation.Access(root, Emit(ShaderType.U32, new ValueOperation.Literal(1u))));
        var value = Emit(ShaderType.U32, new ValueOperation.Literal(7u));
        entry.Terminator = new ControlFlowTerminator.Conditional(Emit(ShaderType.Bool, new ValueOperation.Literal(chooseFirst)), new(accept.Id), new(reject.Id));
        var selected = graph.Value(pointerType); merge.Parameters.Add(selected);
        accept.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [first])); reject.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [second]));
        merge.Instructions.Add(new(null, new ValueOperation.Store(selected, value, new(1)), new(31, 3)));
        merge.Terminator = new ControlFlowTerminator.Return(); graph.SelectionMerges.Add(entry.Id, merge.Id);
        var canonical = new CanonicalModule(input, new Dictionary<string, ControlFlowFunction> { [main.Name] = graph }, new Dictionary<string, string>(), new HashSet<string> { main.Name });
        ModuleValidator.Validate(canonical); string before = ControlFlowPrinter.Write(graph);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(canonical);
        Assert.Equal(before, ControlFlowPrinter.Write(graph)); Assert.Empty(main.Body.Statements);
        ModuleValidator.Validate(prepared.Canonical);
        var binary = SpirvWriter.Emit(prepared);
        Assert.Contains(binary.Instructions, i => i.Opcode == 62 && i.Operands.Length > 2 && (i.Operands[2] & 1) != 0);
        Assert.Equal(chooseFirst ? new uint[] { 7, 0 } : [0, 7], new CanonicalExecution(SpirvReader.Parse(binary.ToBytes()), []).Run().Output);
    }

    [Theory]
    [InlineData("loop")] [InlineData("workgroup")] [InlineData("uniform")]
    [InlineData("continuing")] [InlineData("vertex")] [InlineData("depth")] [InlineData("mesh")]
    public void EntryAndLayoutConsumeOwnedGraphsWithoutReadingObsoleteBodies(string family)
    {
        string source = family switch {
            "loop" => Loop, "workgroup" => SpirvWorkgroupConversionTests.NestedSource,
            "uniform" => UniformMemoryTests.NestedSource, "continuing" => SpirvUniformLegalizationTests.ContinuingSource,
            "vertex" => SpirvOutputPolicyTests.Vertex, "depth" => SpirvOutputPolicyTests.Depth, _ => MeshShaderTests.Source
        };
        var input = WgslReader.Parse(source); var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Empty(canonical.DeferredFunctions);
        var traces = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var bodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
        try {
            foreach (var graph in canonical.Functions.Values) {
                graph.Signature.Body = new();
                graph.Signature.Body.Statements.Add(new Statement.Return(Expression.U32(999)));
            }
            var prepared = SpirvEntryPointLowering.Run(canonical, true, true);
            ModuleValidator.Validate(prepared.PhysicalLayout.Canonical);
            foreach (var pair in canonical.Functions) {
                var actual = prepared.PhysicalLayout.ControlFlow[pair.Key].Graph;
                Assert.NotSame(pair.Value, actual);
                Assert.Equal(traces[pair.Key], ControlFlowPrinter.Write(pair.Value));
                Assert.Equal(pair.Value.Loops, actual.Loops);
                Assert.All(pair.Value.Blocks, b => Assert.Contains(actual.Blocks, target => target.Id == b.Id));
                Assert.DoesNotContain(actual.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Literal { Value: 999u });
            }
            foreach (var helper in prepared.PhysicalLayout.WorkgroupConversions.Values.Concat(prepared.OutputFunctions.Values)) {
                Assert.Empty(helper.Body.Statements);
                Assert.All(prepared.PhysicalLayout.ControlFlow[helper.Name].Graph.Blocks.SelectMany(b => b.Instructions), i => Assert.Equal(ShaderEffects.None, i.Effects));
            }
            var binary = SpirvWriter.Emit(prepared);
            Assert.Equal(binary.ToBytes(), SpirvWriter.Emit(prepared).ToBytes());
            ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
            if (family == "loop")
                Assert.Equal(new uint[] { 6, 0 }, new CanonicalExecution(SpirvReader.Parse(binary.ToBytes()), []).Run().Output);
        }
        finally { foreach (var function in input.Functions) function.Body = bodies[function.Name]; }
    }

    [Fact]
    public void DirectPureConversionsPreserveEveryNestedValueInBothDirections()
    {
        var layout = SpirvPhysicalLayoutLowering.Prepare(WgslReader.Parse(SpirvWorkgroupConversionTests.NestedSource));
        Assert.Equal(6, layout.WorkgroupConversions.Count);
        uint next = 17;
        object Sample(ShaderType type) => type switch {
            ShaderType.Scalar => next++,
            ShaderType.Vector v => Enumerable.Range(0, v.Size).Select(_ => Sample(v.Component)).ToArray(),
            ShaderType.Array { Length: uint length } a => Enumerable.Range(0, (int)length).Select(_ => Sample(a.Element)).ToArray(),
            ShaderType.Structure s => s.Members.ToDictionary(m => m.Name, m => Sample(m.Type)),
            _ => throw new InvalidOperationException("Unexpected fixture type")
        };
        IEnumerable<uint> Flatten(object value) => value switch {
            uint number => [number], Dictionary<string, object> fields => fields.Values.SelectMany(Flatten),
            object[] items => items.SelectMany(Flatten), _ => throw new InvalidOperationException("Unexpected fixture value")
        };
        object Execute(ShaderFunction helper, object argument) {
            Assert.Empty(helper.Body.Statements);
            var block = Assert.Single(layout.ControlFlow[helper.Name].Graph.Blocks);
            var values = new Dictionary<int, object>();
            object Value(SsaValue value) => values[value.Id];
            foreach (var instruction in block.Instructions) {
                Assert.Equal(ShaderEffects.None, instruction.Effects);
                var result = instruction.Result!.Value;
                values.Add(result.Id, instruction.Operation switch {
                    ValueOperation.Symbol { Name: "value" } => argument,
                    ValueOperation.Literal literal => literal.Value,
                    ValueOperation.Member member => ((Dictionary<string, object>)Value(member.Base))[member.Name],
                    ValueOperation.Access access => ((object[])Value(access.Base))[(uint)Value(access.Index)],
                    ValueOperation.Construct construct when result.Type is ShaderType.Structure structure
                        => structure.Members.Select((m, i) => (m.Name, Value: Value(construct.Components[i]))).ToDictionary(p => p.Name, p => p.Value),
                    ValueOperation.Construct construct => construct.Components.Select(Value).ToArray(),
                    _ => throw new InvalidOperationException("Pure fixture conversion contains an effect or unsupported operation")
                });
            }
            return Value(Assert.IsType<ControlFlowTerminator.Return>(block.Terminator).Value!.Value);
        }
        foreach (var logical in layout.WorkgroupConversions.Keys.Where(k => k.ToPhysical).Select(k => k.Logical)) {
            var original = Sample(logical);
            var mapped = Execute(layout.WorkgroupConversions[(logical, true)], original);
            var restored = Execute(layout.WorkgroupConversions[(logical, false)], mapped);
            Assert.Equal(Flatten(original), Flatten(mapped)); Assert.Equal(Flatten(original), Flatten(restored));
        }
    }
}
