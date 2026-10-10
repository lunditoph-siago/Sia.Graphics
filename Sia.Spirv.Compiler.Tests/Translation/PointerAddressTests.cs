using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PointerAddressTests
{
    internal const string ControlSource = """
        enable wgpu_ray_query;
        @group(0) @binding(0) var<storage,read> inputs:array<u32>;
        @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
        fn step(q:ptr<function,ray_query>,p:ptr<function,u32>)->u32{*p=1u-*p;return 91u;}
        @compute @workgroup_size(1) fn main(@builtin(global_invocation_id) id:vec3u){
            var query:ray_query;var index=inputs[id.x]&1u;var v=vec2u(11u,12u);
            v[index]=step(&query,&index);
            outputs[id.x*3u]=v.x;outputs[id.x*3u+1u]=v.y;outputs[id.x*3u+2u]=index;
        }
        """;

    [Theory]
    [InlineData("var v=vec2f();", "v[0]")]
    [InlineData("var v=vec2f();", "v.x")]
    [InlineData("var v=vec2f();", "v.r")]
    [InlineData("var v=vec2f();let n=1u;", "(v)[n]")]
    [InlineData("var v=array<vec2f,2>();", "v[1][0]")]
    [InlineData("var v=mat3x2f();", "v[1][0]")]
    [InlineData("var v=vec2f();let p=&v;", "(*p).x")]
    [InlineData("var v=vec2f();let p=&v;", "p.x")]
    [InlineData("var v=vec2f();let p=&v;", "p[1]")]
    [InlineData("var v=vec2f();let p=&v;", "(*p)[1]")]
    [InlineData("var v=S();", "v.value.y")]
    public void WgslRejectsTakingVectorComponentAddresses(string declarations, string operand)
    {
        string source = "struct S{value:vec2f,} @compute @workgroup_size(1) fn main(){" + declarations + "let q=&" + operand + ";}";
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("vector component", error.Message);
        Assert.True(error.Diagnostic.Span.Length > 0);
    }

    [Theory]
    [InlineData("var v=vec2f();", "v")]
    [InlineData("var v=mat3x2f();", "v[1]")]
    [InlineData("var v=array<f32,2>();", "v[1]")]
    [InlineData("var v=S();", "v.scalar")]
    [InlineData("var v=S();", "v.value")]
    [InlineData("var v=vec2f();let p=&v;", "*p")]
    public void WgslRetainsValidCompositeAndScalarAddresses(string declarations, string operand)
    {
        var module = WgslReader.Parse("struct S{value:vec2f,scalar:f32,} @compute @workgroup_size(1) fn main(){" + declarations + "let q=&" + operand + ";}");
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Fact]
    public void VectorComponentAssignmentsRemainValid()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){var v=vec2u();v.x=7u;v[1]+=v.x;v.y++;}");
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Fact]
    public void NativeVectorComponentPointersRemainValidIrButCannotEmitInvalidWgsl()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){var v=vec2u();}");
        var vector = new ShaderType.Pointer(new ShaderType.Vector(2, ShaderType.U32), AddressSpace.Function);
        var scalar = vector with { Base = ShaderType.U32 };
        var component = new Expression.Access(new Expression.Reference("v", vector), Expression.U32(0), scalar);
        module.Functions[0].Body.Statements.Add(new Statement.Declare("p", scalar, new Expression.Unary("&", component, scalar), false));
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("vector component", error.Message);
    }

    [Fact]
    public void HelperExpansionCapturesTheVectorAndIndexInsteadOfItsComponentAddress()
    {
        var module = WgslReader.Parse(ControlSource); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
    }
}
