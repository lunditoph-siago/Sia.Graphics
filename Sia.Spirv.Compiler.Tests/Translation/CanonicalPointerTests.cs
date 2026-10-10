using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalPointerTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    internal const string PointerScalar = Resources + "fn edit(p:ptr<function,u32>,delta:u32)->u32{let old=*p;*p=old+delta;return old;}@compute @workgroup_size(1) fn main(){var value=inputs[0];let old=edit(&value,3u);outputs[0]=old;outputs[1]=value;}";
    internal const string PointerNested = Resources + "fn add(p:ptr<function,vec2u>){(*p).x=(*p).x+7u;(*p).y=(*p).y+2u;}fn nested(p:ptr<function,vec2u>)->u32{add(p);return (*p).x*10u+(*p).y;}@compute @workgroup_size(1) fn main(){var pair=vec2u(inputs[0],5u);let result=nested(&pair);outputs[0]=result;outputs[1]=pair.y;}";
    internal const string PointerIndexCapture = Resources + "var<private> index:u32;fn step(p:ptr<function,u32>)->u32{index=1u;*p=*p+9u;return 99u;}@compute @workgroup_size(1) fn main(){var values=array<u32,2>(inputs[0],20u);let p=&values[index];let result=step(p);outputs[0]=values[0]*100u+values[1];outputs[1]=result+index;}";
    internal const string PointerPrivate = Resources + "var<private> slot:u32;fn bump(p:ptr<private,u32>,delta:u32){*p=*p+delta;}fn nested(p:ptr<private,u32>)->u32{bump(p,7u);return *p;}@compute @workgroup_size(1) fn main(){slot=inputs[0];let result=nested(&slot);outputs[0]=result;outputs[1]=slot;}";
    internal const string PointerAliased = Resources + "fn writes(a:ptr<function,u32>,b:ptr<function,u32>)->u32{*a=7u;let value=*b;*b=value+9u;return value;}@compute @workgroup_size(1) fn main(){var value=inputs[0];let result=writes(&value,&value);outputs[0]=result;outputs[1]=value;}";
    internal const string PointerDistinct = Resources + "fn writes(a:ptr<function,u32>,b:ptr<function,u32>)->u32{*a=7u;let value=*b;*b=value+9u;return value;}@compute @workgroup_size(1) fn main(){var value=inputs[0];var other=inputs[0]+1u;let result=writes(&value,&other);outputs[0]=result;outputs[1]=value*100u+other;}";
    internal const string PointerLoop = Resources + "fn advance(p:ptr<function,u32>)->u32{var steps=0u;loop{if *p>=inputs[0]+3u{break;}*p=*p+1u;steps=steps+1u;}return steps;}@compute @workgroup_size(1) fn main(){var value=inputs[0];let steps=advance(&value);outputs[0]=value;outputs[1]=steps;}";

    [Theory]
    [InlineData(PointerScalar)] [InlineData(PointerNested)] [InlineData(PointerIndexCapture)]
    [InlineData(PointerPrivate)] [InlineData(PointerDistinct)] [InlineData(PointerLoop)]
    public void PointerHelpersActuallyMigrateThroughBothFrontendRoutes(string source)
    {
        var input = WgslReader.Parse(source);
        foreach (var module in new[] { input, SpirvReader.Parse(SpirvWriter.Write(input, SpirvCompilationTarget.Default)) }) {
            string before = WgslWriter.Emit(module);
            var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
            var output = CanonicalShaderPipeline.Run(module, traces, deferrals);
            Assert.Empty(deferrals); Assert.Equal(module.Functions.Count * 3, traces.Count);
            Assert.All(module.Functions, f => Assert.NotSame(f, output.Functions.Single(p => p.Name == f.Name)));
            ModuleValidator.Validate(output);
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(output, SpirvCompilationTarget.Default)));
            ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(output, SpirvCompilationTarget.Default)));
            Assert.Equal(before, WgslWriter.Emit(module));
        }
    }

    [Fact]
    public void EscapingSlotsRemainMemoryAndSameAddressArgumentsRemainIdentical()
    {
        var module = WgslReader.Parse(PointerDistinct); var entry = module.Functions.Single(f => f.Stage is not null);
        int resultAt = entry.Body.Statements.FindIndex(s => s is Statement.Declare { Initializer: Expression.Call });
        var result = (Statement.Declare)entry.Body.Statements[resultAt]; var original = (Expression.Call)result.Initializer!;
        entry.Body.Statements[resultAt] = result with { Initializer = original with { Arguments = [original.Arguments[0], original.Arguments[0]] } };
        Assert.True(StructuredControlFlowReader.TryRead(entry, module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, module);
        var instructions = graph!.Blocks.SelectMany(b => b.Instructions).ToArray();
        var call = Assert.Single(instructions.Select(i => i.Operation).OfType<ValueOperation.Call>());
        Assert.Equal(call.Arguments[0], call.Arguments[1]);
        Assert.Contains(instructions, i => i.Result == call.Arguments[0] && i.Operation is ValueOperation.Local);
        Assert.Contains(instructions, i => i.Operation is ValueOperation.Store s && s.Pointer == call.Arguments[0]);
        Assert.Contains(instructions, i => i.Operation is ValueOperation.Load l && l.Pointer == call.Arguments[0]);
        var deferrals = new List<CanonicalDeferral>(); var output = CanonicalShaderPipeline.Run(module, deferrals: deferrals);
        Assert.Empty(deferrals); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(output, SpirvCompilationTarget.Default)));
        var wgsl = WgslReader.Parse(WgslWriter.Write(output, SpirvCompilationTarget.Default)); ModuleValidator.Validate(wgsl);
        Assert.DoesNotContain(wgsl.Functions, f => f.Arguments.Any(a => a.Type is ShaderType.Pointer));
    }

    [Fact]
    public void VerifierRejectsPointerCallAddressSpaceAndAccessChanges()
    {
        var module = WgslReader.Parse(PointerPrivate); var entry = module.Functions.Single(f => f.Stage is not null);
        Assert.True(StructuredControlFlowReader.TryRead(entry, module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
        var block = graph!.Blocks.First(b => b.Instructions.Any(i => i.Operation is ValueOperation.Call));
        int at = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Call);
        var call = (ValueOperation.Call)block.Instructions[at].Operation;
        var callee = module.Functions.Single(f => f.Name == call.Function); var original = callee.Arguments[0];
        foreach (var type in new[] { new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function),
            new ShaderType.Pointer(ShaderType.U32, AddressSpace.Private, StorageAccess.Read) }) {
            callee.Arguments[0] = original with { Type = type };
            Assert.Contains("illegal Call", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
            callee.Arguments[0] = original;
        }
    }
}
