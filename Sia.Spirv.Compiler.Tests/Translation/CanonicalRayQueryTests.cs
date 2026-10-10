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

public class CanonicalRayQueryTests
{
    private static CanonicalModule Input(string body) => CanonicalShaderPipeline.Prepare(WgslReader.Parse(
        "enable wgpu_ray_query; @group(0) @binding(0) var scene:acceleration_structure;"
        + "fn run(desc:RayDesc)->u32 {var query:ray_query;" + body + "}"
        + "@compute @workgroup_size(1) fn main(){_=run(RayDesc(0u,255u,0.0,10.0,vec3f(0),vec3f(1)));}"));

    private static object[] Descriptor(uint flags = 0, float min = 0, float max = 10, float origin = 0, float direction = 1)
        => [flags, 255u, min, max, new object[] { origin, 0f, 0f }, new object[] { direction, 0f, 0f }];

    public static TheoryData<uint, float, float, float, float, bool> Descriptors => new() {
        {0,0,10,0,1,true}, {0,0,0,0,0,true}, {4|8,1,2,0,1,true},
        {256|512,0,10,0,1,false}, {256|16,0,10,0,1,false}, {256|32,0,10,0,1,false}, {16|32,0,10,0,1,false},
        {1|2,0,10,0,1,false}, {1|64,0,10,0,1,false}, {1|128,0,10,0,1,false},
        {2|64,0,10,0,1,false}, {2|128,0,10,0,1,false}, {64|128,0,10,0,1,false},
        {0,-1,10,0,1,false}, {0,11,10,0,1,false}, {0,float.NaN,10,0,1,false}, {0,0,float.NaN,0,1,false},
        {0,0,10,float.NaN,1,false}, {0,0,10,float.PositiveInfinity,1,false},
        {0,0,10,0,float.NaN,false}, {0,0,10,0,float.NegativeInfinity,false}
    };

    [Theory] [MemberData(nameof(Descriptors))]
    public void DescriptorGuardsPreventUndefinedInitialization(uint flags, float min, float max, float origin, float direction, bool valid)
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);return select(0u,1u,rayQueryProceed(&query));");
        var lowered = SpirvRayQueryLowering.Run(input);
        var execution = new QueryExecution(lowered.Functions["run"], Descriptor(flags, min, max, origin, direction));
        Assert.Equal(valid ? 1u : 0u, execution.Run());
        Assert.Equal(valid ? new[] { "Initialize", "Proceed" } : [], execution.Updates);
    }

    [Fact]
    public void UninitializedQueryOperationsDoNotTouchTheOpaqueHandle()
    {
        var input = Input("_=rayQueryProceed(&query);rayQueryTerminate(&query);rayQueryConfirmIntersection(&query);"
            + "rayQueryGenerateIntersection(&query,1.0);let hit=rayQueryGetCommittedIntersection(&query);return hit.kind;");
        var execution = new QueryExecution(SpirvRayQueryLowering.Run(input).Functions["run"], Descriptor());
        Assert.Equal(0u, execution.Run()); Assert.Empty(execution.RawCalls);
    }

    [Fact]
    public void ExhaustedTraversalCannotProceedOrMutateCandidatesAgain()
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);_=rayQueryProceed(&query);_=rayQueryProceed(&query);"
            + "rayQueryConfirmIntersection(&query);rayQueryGenerateIntersection(&query,1.0);rayQueryTerminate(&query);return select(0u,1u,rayQueryProceed(&query));");
        var execution = new QueryExecution(SpirvRayQueryLowering.Run(input).Functions["run"], Descriptor()) { Proceed = new([true, false]) };
        Assert.Equal(0u, execution.Run()); Assert.Equal(new[] { "Initialize", "Proceed", "Proceed" }, execution.Updates);
    }

    [Theory] [InlineData(0u, false)] [InlineData(1u, true)]
    public void GeneratedHitsRequireAnAabbCandidate(uint candidate, bool generated)
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);_=rayQueryProceed(&query);"
            + "rayQueryGenerateIntersection(&query,1.0);return 0u;");
        var execution = new QueryExecution(SpirvRayQueryLowering.Run(input).Functions["run"], Descriptor()) { Candidate = candidate };
        execution.Run(); Assert.Equal(generated, execution.Updates.Contains("GenerateIntersection"));
    }

    [Theory] [InlineData(-1f, false)] [InlineData(0f, true)] [InlineData(10f, true)] [InlineData(11f, false)]
    [InlineData(float.NaN, false)]
    public void GeneratedHitDistanceStaysWithinTheDescriptor(float distance, bool generated)
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);_=rayQueryProceed(&query);"
            + "rayQueryGenerateIntersection(&query,1.0);return 0u;");
        var graph = input.Functions["run"];
        var argument = graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Builtin>()
            .Single(b => b.Function == "rayQueryGenerateIntersection").Arguments[1];
        foreach (var block in graph.Blocks) for (int i = 0; i < block.Instructions.Count; i++)
            if (block.Instructions[i].Result == argument) block.Instructions[i] = block.Instructions[i] with { Operation = new ValueOperation.Literal(distance) };
        var execution = new QueryExecution(SpirvRayQueryLowering.Run(input).Functions["run"], Descriptor()) { Candidate = 1 };
        execution.Run(); Assert.Equal(generated, execution.Updates.Contains("GenerateIntersection"));
    }

    [Theory] [InlineData(0u, 1u)] [InlineData(1u, 3u)]
    public void CandidateKindsAreMappedToTheSourceIntersectionEnum(uint raw, uint expected)
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);_=rayQueryProceed(&query);return rayQueryGetCandidateIntersection(&query).kind;");
        var execution = new QueryExecution(SpirvRayQueryLowering.Run(input).Functions["run"], Descriptor()) { Candidate = raw };
        Assert.Equal(expected, execution.Run());
        Assert.Equal(raw == 0, execution.RawCalls.Contains("GetIntersectionBarycentrics"));
    }

    [Theory] [InlineData(0u, true)] [InlineData(1u, false)]
    public void ConfirmationRequiresATriangleCandidate(uint candidate, bool confirmed)
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);_=rayQueryProceed(&query);rayQueryConfirmIntersection(&query);return 0u;");
        var execution = new QueryExecution(SpirvRayQueryLowering.Run(input).Functions["run"], Descriptor()) { Candidate = candidate };
        execution.Run(); Assert.Equal(confirmed, execution.Updates.Contains("ConfirmIntersection"));
    }

    [Theory] [InlineData(0u)] [InlineData(1u)] [InlineData(2u)]
    public void CommittedKindsRemainNativeAndOnlyTrianglesReadBarycentrics(uint kind)
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);_=rayQueryProceed(&query);return rayQueryGetCommittedIntersection(&query).kind;");
        var execution = new QueryExecution(SpirvRayQueryLowering.Run(input).Functions["run"], Descriptor()) { Proceed = new([false]), Committed = kind };
        Assert.Equal(kind, execution.Run());
        Assert.Equal(kind == 1, execution.RawCalls.Contains("GetIntersectionBarycentrics"));
        Assert.Equal(kind != 0, execution.RawCalls.Contains("GetIntersectionT"));
    }

    [Fact]
    public void NativeQueryOperationsKeepTheirGraphAndHaveNoAdditionalGuards()
    {
        var input = CanonicalShaderPipeline.Prepare(SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(RayQueryTests.Source), SpirvCompilationTarget.Default)));
        var before = input.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var lowered = SpirvRayQueryLowering.Run(input);
        Assert.All(input.Functions, p => { Assert.Same(p.Value, lowered.Functions[p.Key]); Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)); });
    }

    [Fact]
    public void RawQueryInstructionsRetainCallOriginsAndLexicalDiagnosticFilters()
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);return select(0u,1u,rayQueryProceed(&query));");
        var graph = input.Functions["run"]; var filters = new[] { new DiagnosticFilter(DiagnosticSeverity.Warning, "derivative_uniformity") };
        foreach (var block in graph.Blocks) for (int i = 0; i < block.Instructions.Count; i++)
            if (block.Instructions[i].Operation is ValueOperation.Builtin { Function: "rayQueryProceed" })
                block.Instructions[i] = block.Instructions[i] with { Span = new(91, 6), DiagnosticFilters = filters };
        var lowered = SpirvRayQueryLowering.Run(input);
        var raw = Assert.Single(lowered.Functions["run"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Builtin { Function: "spirvRayQueryProceedKHR" });
        Assert.Equal(new SourceSpan(91, 6), raw.Span); Assert.Equal(filters, raw.DiagnosticFilters);
    }

    [Fact]
    public void OwnedGraphsRemainAuthoritativeAndUnchangedWithPoisonedBodies()
    {
        var input = Input("rayQueryInitialize(&query,scene,desc);return select(0u,1u,rayQueryProceed(&query));");
        var snapshots = input.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        foreach (var function in input.Declarations.Functions) {
            function.Body = new(); function.Body.Statements.Add(new Statement.Evaluate(new Expression.HelperInvocation()));
        }
        var lowered = ShaderTargetLowering.PrepareSpirv(input, null);
        Assert.All(input.Functions, p => Assert.Equal(snapshots[p.Key], ControlFlowPrinter.Write(p.Value)));
        Assert.DoesNotContain(lowered.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Builtin call && call.Function.StartsWith("rayQuery", StringComparison.Ordinal));
        var target = SpirvEntryPointLowering.Run(lowered, false, false);
        Assert.Empty(target.PhysicalLayout.DeferredControlFlow);
        Assert.Contains(SpirvWriter.Emit(target).Instructions, i => (Op)i.Opcode == Op.RayQueryInitializeKHR);
    }

    [Fact]
    public void CompanionStateResetsAtEveryDynamicAllocation()
    {
        var source = WgslReader.Parse("enable wgpu_ray_query;@compute @workgroup_size(1) fn main(){for(var i=0u;i<2u;i++){var q:ray_query;_=rayQueryProceed(&q);}}");
        var canonical = CanonicalShaderPipeline.Prepare(source);
        var input = canonical.Functions["main"];
        var allocation = Assert.Single(input.Blocks.SelectMany(b => b.Instructions), i => i.Result?.Type is ShaderType.Pointer { Base: ShaderType.RayQuery });
        var lowered = SpirvRayQueryLowering.Run(canonical).Functions["main"];
        Assert.Contains(lowered.Blocks.Single(b => b.Id == input.Blocks.Single(b => b.Instructions.Contains(allocation)).Id).Instructions, i => i == allocation);
        var execution = new QueryExecution(lowered, Descriptor()); execution.Run(); Assert.Empty(execution.RawCalls);
    }

    // A deterministic raw-query stub observes guard decisions, not ray traversal.
    // It supplies independently chosen candidate/committed types and proceed results.
    private sealed class QueryExecution(ControlFlowFunction graph, object[] descriptor)
    {
        private sealed record Address(int Slot, int? Member = null);
        private readonly Dictionary<int, object> values = [];
        private readonly Dictionary<int, object> memory = [];
        public List<string> RawCalls { get; } = [];
        public List<string> Updates { get; } = [];
        public Queue<bool> Proceed { get; init; } = new([true]);
        public uint Candidate { get; init; }
        public uint Committed { get; init; }
        private static object Zero(ShaderType type) => type switch {
            ShaderType.Scalar { Kind: ScalarKind.Bool } => false,
            ShaderType.Scalar { Kind: ScalarKind.Float } => 0f,
            ShaderType.Scalar => 0u,
            ShaderType.Vector v => Enumerable.Repeat(Zero(v.Component), v.Size).ToArray(),
            ShaderType.Matrix m => Enumerable.Range(0, m.Columns).Select(_ => Zero(new ShaderType.Vector(m.Rows, m.Component))).ToArray(),
            ShaderType.Array a => Enumerable.Range(0, checked((int)a.Length!)).Select(_ => Zero(a.Element)).ToArray(),
            ShaderType.Structure s => s.Members.Select(m => Zero(m.Type)).ToArray(),
            _ => throw new NotSupportedException(type.ToString())
        };
        private object V(SsaValue value) => values[value.Id];
        private object Read(Address address) => address.Member is int member ? ((object[])memory[address.Slot])[member] : memory[address.Slot];
        private void Write(Address address, object value) {
            if (address.Member is int member) ((object[])memory[address.Slot])[member] = value; else memory[address.Slot] = value;
        }
        private object Binary(ValueOperation.Binary binary) {
            var a = V(binary.Left); var b = V(binary.Right);
            if (a is bool boolean) return binary.Operator switch { "&&" => boolean && (bool)b, "||" => boolean || (bool)b, "==" => boolean == (bool)b, _ => throw new NotSupportedException() };
            if (a is float real) return binary.Operator switch { "<=" => real <= (float)b, ">=" => real >= (float)b, _ => throw new NotSupportedException() };
            return binary.Operator switch { "&" => (uint)a & (uint)b, "|" => (uint)a | (uint)b, "+" => (uint)a + (uint)b,
                "<" => (uint)a < (uint)b, "==" => (uint)a == (uint)b, "!=" => (uint)a != (uint)b, _ => throw new NotSupportedException(binary.Operator) };
        }
        private object Builtin(ValueOperation.Builtin call) {
            var args = call.Arguments.Select(V).ToArray();
            if (call.Function == "any") return ((object[])args[0]).Cast<bool>().Any(v => v);
            if (call.Function is "isNan" or "isInf") return ((object[])args[0]).Cast<float>()
                .Select(f => (object)(call.Function == "isNan" ? float.IsNaN(f) : float.IsInfinity(f))).ToArray();
            if (call.Function == "select") return (bool)args[2] ? args[1] : args[0];
            Assert.StartsWith("spirvRayQuery", call.Function);
            string name = call.Function[13..^3]; RawCalls.Add(name);
            if (name is "Initialize" or "Proceed" or "ConfirmIntersection" or "GenerateIntersection" or "Terminate") Updates.Add(name);
            return name switch {
                "Proceed" => Proceed.Count != 0 && Proceed.Dequeue(),
                "GetIntersectionType" => (uint)args[1] == 0 ? Candidate : Committed,
                _ => call.ReturnType is ShaderType.Void ? 0u : Zero(call.ReturnType)
            };
        }
        public object Run() {
            var blocks = graph.Blocks.ToDictionary(b => b.Id); var block = blocks[graph.Entry];
            for (int budget = 0; budget < 10000; budget++) {
                foreach (var instruction in block.Instructions) {
                    object value = instruction.Operation switch {
                        ValueOperation.Literal l => l.Value,
                        ValueOperation.Symbol s => s.Name == "desc" ? descriptor : new object(),
                        ValueOperation.Local => new Address(instruction.Result!.Value.Id),
                        ValueOperation.Construct c => c.Components.Count == 0 ? Zero(instruction.Result!.Value.Type) : c.Components.Select(V).ToArray(),
                        ValueOperation.Let l => V(l.Value),
                        ValueOperation.Load l => Read((Address)V(l.Pointer)),
                        ValueOperation.Store s => 0u,
                        ValueOperation.Binary b => Binary(b),
                        ValueOperation.Unary { Operator: "!" } u => !(bool)V(u.Operand),
                        ValueOperation.Select s => (bool)V(s.Condition) ? V(s.Accept) : V(s.Reject),
                        ValueOperation.Member m => V(m.Base) is Address address
                            ? address with { Member = ((ShaderType.Structure)((ShaderType.Pointer)m.Base.Type).Base).Members.ToList().FindIndex(f => f.Name == m.Name) }
                            : ((object[])V(m.Base))[((ShaderType.Structure)m.Base.Type).Members.ToList().FindIndex(f => f.Name == m.Name)],
                        ValueOperation.Builtin b => Builtin(b),
                        _ => throw new NotSupportedException(instruction.Operation.ToString())
                    };
                    if (instruction.Operation is ValueOperation.Store store) Write((Address)V(store.Pointer), V(store.Value));
                    if (instruction.Result is { } result) values[result.Id] = value;
                }
                if (block.Terminator is ControlFlowTerminator.Return returned) return returned.Value is { } result ? V(result) : 0u;
                var edge = block.Terminator switch {
                    ControlFlowTerminator.Branch branch => branch.Edge,
                    ControlFlowTerminator.Conditional branch => (bool)V(branch.Condition) ? branch.Accept : branch.Reject,
                    _ => throw new NotSupportedException()
                };
                var arguments = edge.Arguments.Select(V).ToArray(); block = blocks[edge.Target];
                for (int i = 0; i < arguments.Length; i++) values[block.Parameters[i].Id] = arguments[i];
            }
            throw new InvalidOperationException("Query guard test exceeded step limit.");
        }
    }
}
