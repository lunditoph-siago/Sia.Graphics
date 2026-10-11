using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalControlFlowRegionTests
{
    [Theory] [InlineData(0u, 6u)] [InlineData(1u, 4u)]
    public void NaturalLoopRegionsPreserveContinueBreakAndOrderedCalls(uint input, uint expected)
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;var<private> count:u32;"
            + "fn enter(){count+=1u;}@compute @workgroup_size(1) fn main(){var result=0u;for(var i=0u;i<4u;i++){enter();if((inputs[0]&1u)==1u&&i==1u){continue;}if(i==3u){break;}result+=i+1u;}outputs[0]=result;outputs[1]=count;}");
        var source = CanonicalShaderPipeline.Prepare(module);
        var before = source.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var target = CanonicalControlFlowRegions.Run(source);
        Module rebuilt;
        try { rebuilt = StructuredControlFlowLowering.Run(target); }
        catch (ShaderException error) { throw new InvalidOperationException(ControlFlowPrinter.Write(target.Functions["main"]), error); }
        Assert.Equal(new uint[] { expected, 4 }, new CanonicalExecution(rebuilt, [input]).Run().Output);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(rebuilt, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(rebuilt, SpirvCompilationTarget.Default)));
        Assert.All(source.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void ReconvergedBarrierStaysOutsideTheVaryingSelection()
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> outputs:array<u32>;@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) id:u32){if(id==0u){outputs[0]=1u;}workgroupBarrier();outputs[1]=2u;}");
        var source = CanonicalShaderPipeline.Prepare(module);
        var target = CanonicalControlFlowRegions.Run(source);
        var text = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(target));
        ModuleValidator.Validate(WgslReader.Parse(text));
        Assert.Single(target.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => (i.Effects & ShaderEffects.Synchronization) != 0);
    }

    [Theory] [InlineData(0u, 12u)] [InlineData(1u, 21u)] [InlineData(2u, 12u)]
    public void IrreducibleDataEdgesInitializeParametersAndSwapInParallel(uint input, uint expected)
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;fn swap(n:u32)->u32{return n;}@compute @workgroup_size(1) fn main(){outputs[0]=swap(inputs[0]);}");
        var source = CanonicalShaderPipeline.Prepare(module);
        var graph = new ControlFlowFunction(module.Functions.Single(f => f.Name == "swap"));
        var entry = graph.Block(); var gate = graph.Block(); var first = graph.Block(); var second = graph.Block(); var done = graph.Block();
        SsaValue Emit(ControlFlowBlock block, ShaderType type, ValueOperation operation) {
            var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value;
        }
        SsaValue Literal(ControlFlowBlock block, uint value) => Emit(block, ShaderType.U32, new ValueOperation.Literal(value));
        void Parameters(ControlFlowBlock block) { for (int i = 0; i < 3; i++) block.Parameters.Add(graph.Value(ShaderType.U32)); }
        Parameters(gate); Parameters(first); Parameters(second); done.Parameters.Add(graph.Value(ShaderType.U32));
        var n = Emit(entry, ShaderType.U32, new ValueOperation.Symbol("n"));
        entry.Terminator = new ControlFlowTerminator.Branch(new(gate.Id, [Literal(entry, 1), Literal(entry, 2), n]));
        var choose = Emit(gate, ShaderType.Bool, new ValueOperation.Binary("==", gate.Parameters[2], Literal(gate, 0)));
        gate.Terminator = new ControlFlowTerminator.Conditional(choose, new(first.Id, gate.Parameters), new(second.Id, gate.Parameters));
        foreach (var block in new[] { first, second }) {
            var parameters = block.Parameters;
            var again = Emit(block, ShaderType.Bool, new ValueOperation.Binary("<", parameters[2], Literal(block, 2)));
            var next = Emit(block, ShaderType.U32, new ValueOperation.Binary("+", parameters[2], Literal(block, 1)));
            var tens = Emit(block, ShaderType.U32, new ValueOperation.Binary("*", parameters[0], Literal(block, 10)));
            var result = Emit(block, ShaderType.U32, new ValueOperation.Binary("+", tens, parameters[1]));
            block.Terminator = new ControlFlowTerminator.Conditional(again,
                new(block == first ? second.Id : first.Id, [parameters[1], parameters[0], next]), new(done.Id, [result]));
        }
        done.Terminator = new ControlFlowTerminator.Return(done.Parameters[0]);
        var functions = source.Functions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        functions["swap"] = graph; source = source with { Functions = functions };
        ModuleValidator.Validate(source, native: true);
        string before = ControlFlowPrinter.Write(graph);
        var target = CanonicalControlFlowRegions.Run(source); var rebuilt = StructuredControlFlowLowering.Run(target);
        Assert.Equal(new uint[] { expected, 0 }, new CanonicalExecution(rebuilt, [input]).Run().Output);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(rebuilt, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(rebuilt, SpirvCompilationTarget.Default)));
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
    }
}
