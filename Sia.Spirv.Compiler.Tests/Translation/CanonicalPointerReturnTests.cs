using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalPointerReturnTests
{
    private static readonly ShaderType.Pointer Pointer = new(ShaderType.U32, AddressSpace.Function);
    internal static Module Create(bool nested = false, bool loop = false, bool earlyLoop = false, bool repeat = false, bool escapeLocal = false)
    {
        string prefix = escapeLocal ? "var a:u32;bump(&a);*p+=a;"
            : earlyLoop ? "var i=0u;loop{loop{*p+=1u;if condition{return *p;}break;}if i>=1u{break;}i+=1u;}"
            : loop ? "var i=0u;loop{if i>=2u{break;}*p+=1u;i+=1u;}" : "var a=1u;*p+=a;";
        string source = "fn pick(p:ptr<function,u32>,q:ptr<function,u32>,condition:bool)->u32{" + prefix + "if condition{*q+=2u;return *q;}return *p;}"
            + "fn forward(p:ptr<function,u32>,q:ptr<function,u32>,condition:bool)->u32{return pick(p,q,condition);}"
            + "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "@compute @workgroup_size(1) fn main(){var a=inputs[0];var b=10u;let chosen=" + (nested ? "forward" : "pick") + "(&a,&b,(a&1u)!=0u);outputs[0]=a;outputs[1]=b;}";
        if (escapeLocal) source = "fn bump(p:ptr<function,u32>){*p+=1u;}" + source;
        var module = WgslReader.Parse(source);
        if (!nested) module.Functions.RemoveAll(f => f.Name == "forward");
        void Returns(Block body, HashSet<string> pointers, bool helper) {
            for (int i = 0; i < body.Statements.Count; i++) {
                if (body.Statements[i] is Statement.Declare { Initializer: Expression.Call call } declaration && call.Function is "pick" or "forward") {
                    pointers.Add(declaration.Name);
                    body.Statements[i] = declaration with { Type = Pointer, Initializer = call with { Type = Pointer } };
                }
                else if (body.Statements[i] is Statement.Declare { Initializer: Expression.Reference reference } alias && pointers.Contains(reference.Name)) {
                    pointers.Add(alias.Name);
                    body.Statements[i] = alias with { Type = Pointer, Initializer = reference with { Type = Pointer } };
                }
                if (body.Statements[i] is Statement.Return { Value: Expression.Load load } ret)
                    body.Statements[i] = ret with { Value = load.Pointer is Expression.Unary { Operator: "*" } dereference ? dereference.Operand : load.Pointer };
                else if (helper && body.Statements[i] is Statement.Return { Value: Expression.Reference result } returned) {
                    int snapshot = body.Statements.FindIndex(s => s is Statement.Declare d && d.Name == result.Name);
                    if (snapshot >= 0 && body.Statements[snapshot] is Statement.Declare { Initializer: Expression.Load read } d) {
                        body.Statements[snapshot] = d with { Type = Pointer, Initializer = read.Pointer is Expression.Unary { Operator: "*" } dereference ? dereference.Operand : read.Pointer };
                        pointers.Add(result.Name);
                    }
                    if (pointers.Contains(result.Name)) body.Statements[i] = returned with { Value = result with { Type = Pointer } };
                }
                if (body.Statements[i] is Statement.If branch) { Returns(branch.Accept, pointers, helper); Returns(branch.Reject, pointers, helper); }
                if (body.Statements[i] is Statement.Loop l) { Returns(l.Body, pointers, helper); Returns(l.Continuing, pointers, helper); }
            }
        }
        foreach (var helper in module.Functions.Where(f => f.Name is "pick" or "forward")) { helper.ReturnType = Pointer; Returns(helper.Body, [], true); }
        var main = module.Functions.Single(f => f.Stage is not null);
        Returns(main.Body, [], false);
        int selected = main.Body.Statements.FindIndex(s => s is Statement.Declare { Name: "chosen" });
        var place = new Expression.Unary("*", new Expression.Reference("chosen", Pointer), Pointer);
        main.Body.Statements.Insert(selected + 1, new Statement.Store(place, new Expression.Binary("+", new Expression.Load(place), Expression.U32(7), ShaderType.U32)));
        if (repeat) {
            int begin = main.Body.Statements.FindIndex(s => s is Statement.Declare { Name: "b" }) + 1;
            var body = new Block(); var counter = new Expression.Reference("counter", new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function));
            var exit = new Block(); exit.Statements.Add(new Statement.Break());
            body.Statements.Add(new Statement.If(new Expression.Binary(">=", new Expression.Load(counter), Expression.U32(2), ShaderType.Bool), exit, new()));
            body.Statements.AddRange(main.Body.Statements.Skip(begin).Take(selected + 2 - begin));
            body.Statements.Add(new Statement.Store(counter, new Expression.Binary("+", new Expression.Load(counter), Expression.U32(1), ShaderType.U32)));
            main.Body.Statements.RemoveRange(begin, selected + 2 - begin);
            main.Body.Statements.Insert(begin, new Statement.Declare("counter", ShaderType.U32, Expression.U32(0)));
            main.Body.Statements.Insert(begin + 1, new Statement.Loop(body, new()));
        }
        ModuleValidator.Validate(module); return module;
    }

    [Fact]
    public void DefaultDiagnosticScopesDoNotIntroduceCompoundAttributes()
    {
        string wgsl = WgslWriter.Write(Create(nested: true), SpirvCompilationTarget.Default);
        Assert.DoesNotContain("@diagnostic(", wgsl);
    }

    [Theory]
    [InlineData(false, DiagnosticSeverity.Off, DiagnosticSeverity.Error)]
    [InlineData(true, DiagnosticSeverity.Off, DiagnosticSeverity.Error)]
    [InlineData(false, DiagnosticSeverity.Error, DiagnosticSeverity.Off)]
    [InlineData(true, DiagnosticSeverity.Error, DiagnosticSeverity.Off)]
    public void CalleeDiagnosticScopeOverridesTheCallerAfterExpansion(bool nested, DiagnosticSeverity caller, DiagnosticSeverity callee)
    {
        var module = Create(nested: nested);
        module.Functions.Single(f => f.Stage is not null).DiagnosticFilters.Add(new(caller, "derivative_uniformity"));
        module.Functions.Single(f => f.Name == "pick").DiagnosticFilters.Add(new(callee, "derivative_uniformity"));
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(f => f.Stage is not null), module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!);
        var expanded = CanonicalHelperInliner.Run(graph!, module);
        var local = expanded.Blocks.SelectMany(b => b.Instructions).Last(i => i.Operation is ValueOperation.Local { Name: "a" });
        Assert.Equal(new DiagnosticFilter(callee, "derivative_uniformity"), Assert.Single(local.DiagnosticFilters));
        Assert.Empty(graph!.Blocks.SelectMany(b => b.Instructions).SelectMany(i => i.DiagnosticFilters));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void InheritedCalleeRulesRestoreDefaultsAcrossCallerBlockScopes(bool nested, bool moduleOff)
    {
        var module = Create(nested: nested);
        if (moduleOff) module.DiagnosticFilters.Add(new(DiagnosticSeverity.Off, "derivative_uniformity"));
        var main = module.Functions.Single(f => f.Stage is not null);
        main.Body.DiagnosticFilters.Add(new(moduleOff ? DiagnosticSeverity.Error : DiagnosticSeverity.Off, "derivative_uniformity"));
        Assert.True(StructuredControlFlowReader.TryRead(main, module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); string before = ControlFlowPrinter.Write(graph!);
        var expanded = CanonicalHelperInliner.Run(graph!, module);
        var local = expanded.Blocks.SelectMany(b => b.Instructions).Last(i => i.Operation is ValueOperation.Local { Name: "a" });
        Assert.Equal(new DiagnosticFilter(moduleOff ? DiagnosticSeverity.Off : DiagnosticSeverity.Error, "derivative_uniformity"), Assert.Single(local.DiagnosticFilters));
        Assert.Equal(before, ControlFlowPrinter.Write(graph!));
    }

    [Fact]
    public void PointerReturnsAndCallsEnterCanonicalIr()
    {
        var module = Create(nested: true);
        foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
            if (function.Stage is null) Assert.Contains(graph!.Blocks, b => b.Terminator is ControlFlowTerminator.Return { Value.Type: ShaderType.Pointer });
        }
    }

    [Theory]
    [InlineData(false, false, 0u, 8u, 10u)] [InlineData(false, false, 5u, 6u, 19u)]
    [InlineData(true, false, 0u, 8u, 10u)] [InlineData(true, false, 5u, 6u, 19u)]
    [InlineData(true, true, 0u, 9u, 10u)] [InlineData(true, true, 5u, 7u, 19u)]
    public void ReturnedAddressRetainsWritesAndNestedCalls(bool nested, bool loop, uint input, uint first, uint second)
    {
        var module = Create(nested, loop); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default); var binary = SpirvWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(wgsl), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(binary), [input]).Run().Output);
    }

    [Theory]
    [InlineData(0u, 9u, 10u)] [InlineData(5u, 13u, 10u)]
    public void ReturnedAddressExitsTheHelpersLoopBeforeCallerContinues(uint input, uint first, uint second)
    {
        var module = Create(nested: true, earlyLoop: true); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default); var binary = SpirvWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(wgsl), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(binary), [input]).Run().Output);
    }

    [Theory]
    [InlineData(false, 0u, 16u, 10u)] [InlineData(false, 5u, 14u, 19u)]
    [InlineData(true, 0u, 17u, 10u)] [InlineData(true, 5u, 21u, 10u)]
    public void RepeatedCallsResetExitStateAndCaptureArgumentsBeforeMutation(bool earlyLoop, uint input, uint first, uint second)
    {
        var module = Create(nested: true, earlyLoop: earlyLoop, repeat: true); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Fact]
    public void CanonicalExpansionBorrowsTheGraphAndReportsItsPass()
    {
        var module = Create(nested: true);
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(f => f.Stage is not null), module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); string before = ControlFlowPrinter.Write(graph!);
        var expanded = CanonicalHelperInliner.Run(graph!, module);
        Assert.NotSame(graph, expanded); Assert.Equal(before, ControlFlowPrinter.Write(graph!));
        Assert.DoesNotContain(expanded.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call { ReturnType: ShaderType.Pointer });
        var traces = new List<CanonicalPassTrace>(); var output = CanonicalShaderPipeline.Run(module, traces);
        Assert.Contains(traces, t => t.Function == "main" && t.Pass == "pointer-return-helper-expansion");
        Assert.DoesNotContain(output.Functions, f => f.ReturnType is ShaderType.Pointer);
    }

    [Fact]
    public void ReturnedAddressRootsParticipateInTargetAliasChecks()
    {
        var module = Create(nested: true);
        module.Functions.Add(WgslReader.Parse("fn write(p:ptr<function,u32>,q:ptr<function,u32>){*p=1u;}").Functions.Single());
        var body = module.Functions.Single(f => f.Stage is not null).Body;
        int chosen = body.Statements.FindIndex(s => s is Statement.Declare { Name: "chosen" });
        var address = new Expression.Reference("chosen", Pointer);
        body.Statements.Insert(chosen + 1, new Statement.Evaluate(new Expression.Call("write", [address, address], new ShaderType.Void(), CallBinding.Function)));
        ModuleValidator.Validate(module);
        Assert.Contains("overlapping root", Assert.Throws<ShaderException>(() => PointerAliasAnalysis.Validate(module)).Message);
    }

    [Theory]
    [InlineData(0u, 16u, 10u)] [InlineData(5u, 14u, 19u)]
    public void EscapingCalleeLocalIsInitializedForEachDynamicCall(uint input, uint first, uint second)
    {
        var module = Create(nested: true, repeat: true, escapeLocal: true); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Fact]
    public void ExpansionDoesNotExtendTheLifetimeOfAReturnedCalleeLocal()
    {
        var module = Create(); var helper = module.Functions.Single(f => f.Name == "pick");
        void Replace(Block body) {
            for (int i = 0; i < body.Statements.Count; i++) {
                if (body.Statements[i] is Statement.Return r) body.Statements[i] = r with { Value = new Expression.Unary("&", new Expression.Reference("a", Pointer), Pointer) };
                if (body.Statements[i] is Statement.If branch) { Replace(branch.Accept); Replace(branch.Reject); }
            }
        }
        Replace(helper.Body); ModuleValidator.Validate(module);
        Assert.Contains("callee-local", Assert.Throws<ShaderException>(() => CanonicalShaderPipeline.Run(module)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PromotionRetainsAddressesUsedByTerminatorsAndEdges(bool merge)
    {
        var signature = new ShaderFunction("keep") { ReturnType = Pointer }; var module = new Module(); module.Functions.Add(signature);
        var graph = new ControlFlowFunction(signature); var entry = graph.Block(); graph.Entry = entry.Id;
        var local = graph.Value(Pointer); entry.Instructions.Add(new(local, new ValueOperation.Local("value")));
        if (merge) {
            var join = graph.Block(); var incoming = graph.Value(Pointer); join.Parameters.Add(incoming);
            entry.Terminator = new ControlFlowTerminator.Branch(new(join.Id, [local])); join.Terminator = new ControlFlowTerminator.Return(incoming);
        }
        else entry.Terminator = new ControlFlowTerminator.Return(local);
        ControlFlowVerifier.Validate(graph, module); LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
        Assert.Contains(graph.Blocks.SelectMany(b => b.Instructions), i => i.Result == local && i.Operation is ValueOperation.Local);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AllocationOnlyStorageDoesNotProveUniformZeroBeforeAnExplicitStore(bool initialized)
    {
        var module = WgslReader.Parse("@compute @workgroup_size(4) fn main(){var value:u32;if value==0u{workgroupBarrier();}}");
        var body = module.Functions.Single().Body;
        int index = body.Statements.FindIndex(s => s is Statement.Declare { Name: "value" });
        body.Statements[index] = ((Statement.Declare)body.Statements[index]) with { Initialize = false, Initializer = null };
        if (initialized) body.Statements.Insert(index + 1, new Statement.Store(new Expression.Reference("value", Pointer), Expression.U32(0)));
        var diagnostics = UniformityAnalysis.Analyze(module);
        if (initialized) Assert.Empty(diagnostics); else Assert.NotEmpty(diagnostics);
    }
}
