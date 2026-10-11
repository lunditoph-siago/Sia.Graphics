using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Translation.Spirv;
using Xunit;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class KeywordTests
{
    [Theory]
    [InlineData("alias")]
    [InlineData("class")]
    [InlineData("coherent")]
    [InlineData("volatile")]
    [InlineData("diagnostic")]
    [InlineData("NULL")]
    [InlineData("Self")]
    public void ReservedWordsAreRejectedInEveryIdentifierPosition(string name)
    {
        string[] sources = [$"const {name}=1;", $"alias {name}=u32;", $"fn {name}(){{}}",
            $"fn f({name}:u32){{}}", $"fn f(){{let {name}=1u;}}", $"struct S{{{name}:u32}}"];
        foreach(string source in sources) Assert.Contains("Reserved keyword",Assert.Throws<ShaderException>(()=>WgslReader.Parse(source)).Message);
    }

    [Fact]
    public void AttributeKeywordsRemainValidInTheirOwnGrammar()
    {
        const string source="diagnostic(off,derivative_uniformity); @coherent @volatile @group(0) @binding(0) var<storage,read_write> values:array<u32>; @compute @workgroup_size(1) @diagnostic(off,derivative_uniformity) fn main(){values[0]=1u;}";
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("class")]
    public void SpirvEntryNamesAreMadeLegalForWgsl(string name)
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("@compute @workgroup_size(1) fn main(){}", SpirvCompilationTarget.Default));
        var instructions = binary.Instructions.Select(entry => (Op)entry.Opcode == Op.EntryPoint
            ? new SpirvInstruction(entry.Opcode, entry.Operands.Take(2).Concat(SpirvBinary.StringWords(name)).ToArray()) : entry).ToArray();
        var renamed = new SpirvBinary { Bound = binary.Bound, Version = binary.Version, Instructions = instructions };
        var module = SpirvReader.Parse(renamed.ToBytes());
        Assert.Equal("n_" + name, module.Functions.Single(f => f.Stage is not null).Name);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }
}
