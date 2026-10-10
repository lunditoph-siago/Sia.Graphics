using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PointerAliasTests
{
    [Fact]
    public void CollectiveLoadsReadTheirPointerOperandsWithoutInventingWrites()
    {
        // Tests the shared access summary; reference-version workgroup pointer
        // parameter legalization remains a separate target restriction.
        var module = WgslReader.Parse("var<workgroup> value:u32;fn read(a:ptr<workgroup,u32>,b:ptr<workgroup,u32>)->u32{return workgroupUniformLoad(a)+workgroupUniformLoad(b);}@compute @workgroup_size(4) fn main(){_=read(&value,&value);}");
        PointerAliasAnalysis.Validate(module);
    }
    [Theory]
    [InlineData(CanonicalPointerTests.PointerAliased)]
    [InlineData("fn copy(a:ptr<function,u32>,b:ptr<function,u32>){*a=*b;}@compute @workgroup_size(1) fn main(){var v=0u;copy(&v,&v);}")]
    [InlineData("fn write(a:ptr<function,u32>,unused:ptr<function,u32>){*a=1u;}@compute @workgroup_size(1) fn main(){var v=0u;write(&v,&v);}")]
    [InlineData("fn copy(a:ptr<function,u32>,b:ptr<function,u32>){*a=*b;}fn nested(a:ptr<function,u32>,b:ptr<function,u32>){copy(a,b);}@compute @workgroup_size(1) fn main(){var v=0u;nested(&v,&v);}")]
    [InlineData("fn copy(a:ptr<function,u32>,b:ptr<function,u32>){*a=*b;}@compute @workgroup_size(1) fn main(){var v=array<u32,2>();copy(&v[0],&v[1]);}")]
    [InlineData("struct S{a:u32,b:u32,}fn copy(a:ptr<function,u32>,b:ptr<function,u32>){*a=*b;}@compute @workgroup_size(1) fn main(){var v=S();copy(&v.a,&v.b);}")]
    [InlineData("fn copy(a:ptr<function,u32>,b:ptr<function,u32>){*a=*b;}@compute @workgroup_size(1) fn main(){var v=0u;let p=&v;let q=p;copy(p,q);}")]
    [InlineData("var<private> slot:u32;fn mutate(p:ptr<private,u32>){*p=slot;}@compute @workgroup_size(1) fn main(){mutate(&slot);}")]
    [InlineData("var<private> slot:u32;fn mutate(p:ptr<private,u32>){slot=*p;}@compute @workgroup_size(1) fn main(){mutate(&slot);}")]
    [InlineData("var<private> slot:u32;fn mutate(p:ptr<private,u32>){slot=1u;*p=2u;}@compute @workgroup_size(1) fn main(){mutate(&slot);}")]
    [InlineData("var<private> slot:u32;fn read()->u32{return slot;}fn mutate(p:ptr<private,u32>){*p=read();}@compute @workgroup_size(1) fn main(){mutate(&slot);}")]
    [InlineData("var<private> slot:u32;fn write(){slot=1u;}fn read(p:ptr<private,u32>)->u32{write();return *p;}@compute @workgroup_size(1) fn main(){_=read(&slot);}")]
    public void SourceRejectsWrittenAliasesIncludingTransitiveGlobalAccess(string source)
    {
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage); Assert.Contains("Pointer alias violation", error.Message);
        Assert.True(error.Diagnostic.Span.Length > 0);
    }

    [Theory]
    [InlineData("fn read(a:ptr<function,u32>,b:ptr<function,u32>)->u32{return *a+*b;}@compute @workgroup_size(1) fn main(){var v=3u;_=read(&v,&v);}")]
    [InlineData("fn unused(a:ptr<function,u32>,b:ptr<function,u32>){}@compute @workgroup_size(1) fn main(){var v=3u;unused(&v,&v);}")]
    [InlineData("fn write(a:ptr<function,u32>,b:ptr<function,u32>){*a=1u;*b=2u;}@compute @workgroup_size(1) fn main(){var a=0u;var b=0u;write(&a,&b);}")]
    [InlineData("var<private> slot:u32;fn mutate(p:ptr<function,u32>){*p=slot;}@compute @workgroup_size(1) fn main(){var slot=0u;mutate(&slot);}")]
    [InlineData("fn read(a:ptr<function,u32>,b:ptr<function,u32>)->u32{return *a+*b;}@compute @workgroup_size(1) fn main(){var v=3u;loop{let p=&v;continuing{_=read(p,p);break if true;}}}")]
    public void ReadonlyAliasesDistinctRootsAndLoopScopesRemainValid(string source)
    {
        var input = WgslReader.Parse(source); PointerAliasAnalysis.Validate(input);
        var output = CanonicalShaderPipeline.Run(input); PointerAliasAnalysis.Validate(output);
        _ = WgslReader.Parse(WgslWriter.Write(output, SpirvCompilationTarget.Default)); _ = SpirvReader.Parse(SpirvWriter.Write(output, SpirvCompilationTarget.Default));
    }
}
