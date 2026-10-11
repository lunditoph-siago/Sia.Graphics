using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class OverrideArrayTests
{
    [Theory]
    [InlineData("n", 3u)]
    [InlineData("n + 1u", 4u)]
    [InlineData("max(n, 5u)", 5u)]
    [InlineData("n * 2u", 6u)]
    public void ResolutionRewritesArrayTypesAndLeavesInputUnchanged(string length, uint expected)
    {
        var module = WgslReader.Parse($"override n=4u; var<workgroup> data:array<u32,{length}>; @compute @workgroup_size(n) fn main() {{ data[n-1u]=9u; }}");
        ModuleValidator.Validate(module);
        var original = Assert.IsType<ShaderType.Array>(module.Globals[0].Type);
        Assert.Null(original.Length); Assert.NotNull(original.OverrideLength);
        Assert.Throws<ShaderException>(() => TypeLayout.Of(original));
        var back = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)); ModuleValidator.Validate(back);
        var resolved = PipelineConstantResolver.Resolve(back, new Dictionary<string, double> { ["n"] = 3 });
        var array = Assert.IsType<ShaderType.Array>(resolved.Globals[0].Type);
        Assert.Equal(expected, array.Length); Assert.Null(array.OverrideLength);
        Assert.Equal(expected * 4, TypeLayout.Of(array).Size);
        Assert.NotEmpty(SpirvWriter.Write(resolved, SpirvCompilationTarget.Default));
        Assert.NotEmpty(ShaderTranslator.WgslToSpirv(WgslWriter.Write(resolved, SpirvCompilationTarget.Default), SpirvCompilationTarget.Default));
        Assert.NotNull(original.OverrideLength); Assert.Null(original.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterAndExpressionTypesResolveTogether(bool pointer)
    {
        string argument = pointer ? "ptr<private,array<u32,n>>" : "array<u32,n>";
        string access = pointer ? "(*p)[0]" : "p[0]";
        string body = pointer ? "data[0]=1u;" : "_=fetch(data);";
        var module = WgslReader.Parse($"override n=4u; var<workgroup> data:array<u32,n>; fn fetch(p:{argument})->u32 {{ return {access}; }} @compute @workgroup_size(1) fn main() {{ {body} }}");
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["n"] = 3 });
        ShaderType parameter = resolved.Functions[0].Arguments[0].Type;
        if (parameter is ShaderType.Pointer p) parameter = p.Base;
        Assert.Equal(3u, Assert.IsType<ShaderType.Array>(parameter).Length);
        Assert.NotEmpty(SpirvWriter.Write(resolved, SpirvCompilationTarget.Default));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(resolved, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("texture_2d<f32>", "textureLoad(data[0],vec2i(0),0)")]
    [InlineData("sampler", "0u")]
    public void BindingArrayLengthsResolve(string element, string expression)
    {
        var module = WgslReader.Parse($"enable wgpu_binding_array; override n:u32; @group(0) @binding(0) var data:binding_array<{element},n>; @compute @workgroup_size(1) fn main() {{ _={expression}; }}");
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["n"] = 5 });
        var array = Assert.IsType<ShaderType.BindingArray>(resolved.Globals[0].Type);
        Assert.Equal(5u, array.Length); Assert.Null(array.OverrideLength);
        Assert.NotEmpty(SpirvWriter.Write(resolved, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void ExplicitIdsMissingDefaultsAndGlobalNamesSurviveLocalShadowing()
    {
        var module = WgslReader.Parse("@id(7) override n:u32; var<workgroup> data:array<u32,n>; @compute @workgroup_size(1) fn main() { let n=9u; data[0]=n; }");
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["7"] = 3 });
        Assert.Equal(3u, Assert.IsType<ShaderType.Array>(resolved.Globals[0].Type).Length);
        Assert.NotEmpty(SpirvWriter.Write(module, SpirvCompilationTarget.Default, new() { PipelineConstants = new Dictionary<string, double> { ["7"] = 3 } }));
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(module, new Dictionary<string, double>()));
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData("0u", 0)]
    [InlineData("-1i", -1)]
    public void NonpositiveDefaultsAreDeferredUntilPipelineCreation(string defaultValue, double invalid)
    {
        var module = WgslReader.Parse($"override n={defaultValue}; var<workgroup> data:array<u32,n>; @compute @workgroup_size(1) fn main() {{}} ");
        ModuleValidator.Validate(module);
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(module, new Dictionary<string, double>()));
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["n"] = invalid }));
        Assert.Equal(3u, Assert.IsType<ShaderType.Array>(PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["n"] = 3 }).Globals[0].Type).Length);
    }

    [Theory]
    [InlineData("override n=4u; var<workgroup> data:array<array<u32,n>,2>;")]
    [InlineData("override n=4u; struct S { data:array<u32,n> }")]
    [InlineData("override n=4u; var<private> data:array<u32,n>;")]
    [InlineData("override n=4u; @group(0) @binding(0) var<storage,read_write> data:array<u32,n>;")]
    [InlineData("override n=4u; fn helper() { var data:array<u32,n>; }")]
    [InlineData("override n=4u; var<workgroup> data:array<u32,n>; fn helper() { let value=data; }")]
    [InlineData("override n=4u; var<workgroup> data:array<u32,n>; fn helper()->array<u32,n> { return data; }")]
    [InlineData("override n=4u; var<workgroup> data:array<u32,n>; fn helper() { data=array<u32,n>(); }")]
    [InlineData("override n=4.0f; var<workgroup> data:array<u32,n>;")]
    [InlineData("var<private> n:u32; var<workgroup> data:array<u32,n>;")]
    [InlineData("override n=4u; fn helper(p:ptr<workgroup,array<u32,n>>) {}")]
    public void InvalidArrayLengthUsesAreRejectedBeforeResolution(string source)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default, new() { PipelineConstants = new Dictionary<string, double> { ["n"] = 3 } }));
    }

    [Fact]
    public void SpirvSpecializedArrayLengthIsNotCollapsedIntoRuntimeOrDefaultLength()
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv("var<workgroup> data:array<u32,3>; @compute @workgroup_size(1) fn main() { data[0]=1u; }", SpirvCompilationTarget.Default));
        uint lengthId = binary.Instructions.First(i => (Op)i.Opcode == Op.TypeArray).Operands[2];
        var instructions = binary.Instructions.Select(i => (Op)i.Opcode == Op.Constant && i.Operands[1] == lengthId ? i with { Opcode = (ushort)Op.SpecConstant } : i).ToList();
        int firstType = instructions.FindIndex(i => (Op)i.Opcode == Op.TypeVoid || (Op)i.Opcode == Op.TypeInt);
        instructions.Insert(firstType, new((ushort)Op.Decorate, [lengthId, 1, 7]));
        var source = new SpirvBinary { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = instructions }.ToBytes();
        var module = SpirvReader.Parse(source); ModuleValidator.Validate(module);
        var array = Assert.IsType<ShaderType.Array>(module.Globals.Single(g => g.Space == AddressSpace.Workgroup).Type);
        Assert.Null(array.Length); Assert.NotNull(array.OverrideLength);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["7"] = 5 });
        Assert.Equal(5u, Assert.IsType<ShaderType.Array>(resolved.Globals.Single(g => g.Space == AddressSpace.Workgroup).Type).Length);
        Assert.NotEmpty(SpirvWriter.Write(resolved, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void PendingSnapshotsAreNotMovedAcrossStores()
    {
        var module = WgslReader.Parse("override n=3u; var<workgroup> data:array<u32,n>; fn fetch(p:array<u32,n>)->u32 { return p[0]; } @compute @workgroup_size(1) fn main() {} ");
        var array = module.Globals[0].Type;
        var place = new Expression.Reference("data", new ShaderType.Pointer(array, AddressSpace.Workgroup));
        var body = module.Functions[1].Body.Statements;
        body.Add(new Statement.Declare("saved", array, new Expression.Load(place), false));
        body.Add(new Statement.Store(new Expression.Access(place, Expression.U32(0), new ShaderType.Pointer(ShaderType.U32, AddressSpace.Workgroup)), Expression.U32(7)));
        body.Add(new Statement.Declare("result", ShaderType.U32, new Expression.Call("fetch", [new Expression.Reference("saved", array)], ShaderType.U32, CallBinding.Function), false));
        ModuleValidator.Validate(module);
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.NotEmpty(SpirvWriter.Write(PipelineConstantResolver.Resolve(module, new Dictionary<string, double>()), SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData(2u, "n")]
    public void InvalidIrArrayLengthMetadataIsRejected(uint? fixedLength, string name)
    {
        var module = new Module(); module.Constants.Add(new("n", ShaderType.U32, Expression.U32(3), true));
        module.Globals.Add(new("data", new ShaderType.Array(ShaderType.U32, fixedLength) { OverrideLength = name }, AddressSpace.Workgroup));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }

    [Fact]
    public void ResolvedLayoutOverflowIsReportedAsAShaderError()
    {
        var module = WgslReader.Parse("override n=3u; var<workgroup> data:array<u32,n>;");
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["n"] = uint.MaxValue }));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnresolvedWorkgroupArrayLengthsSurviveSpirvWriting(bool atomic)
    {
        string element = atomic ? "atomic<u32>" : "u32";
        string store = atomic ? "atomicStore(&data[0],1u);" : "data[0]=1u;";
        var module = WgslReader.Parse($"@id(7) override n=3u; var<workgroup> data:array<{element},n+1u>; @compute @workgroup_size(1) fn main() {{ {store} }}");
        var length = Assert.Single(module.Constants, c => c.IsSpecialization);
        Assert.Throws<ShaderException>(() => PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { [length.Name] = 9 }));
        byte[] bytes = SpirvWriter.Write(module, SpirvCompilationTarget.Default);
        var binary = SpirvBinary.Parse(bytes);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.SpecConstantOp);
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 1);
        if (atomic) Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.LoopMerge);
        var reread = SpirvReader.Parse(bytes); ModuleValidator.Validate(reread);
        var resolved = PipelineConstantResolver.Resolve(reread, new Dictionary<string, double> { ["7"] = 8 });
        Assert.Equal(9u, Assert.IsType<ShaderType.Array>(resolved.Globals.Single(g => g.Space == AddressSpace.Workgroup).Type).Length);
        Assert.NotEmpty(SpirvWriter.Write(resolved, SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData("texture_2d<f32>")]
    [InlineData("sampler")]
    public void UnresolvedBindingArrayCountUsesItsSpecializationId(string element)
    {
        var module = WgslReader.Parse($"enable wgpu_binding_array; @id(7) override n=3u; @group(0) @binding(0) var data:binding_array<{element},n>; @compute @workgroup_size(1) fn main() {{}} ");
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        uint countId = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.TypeArray).Operands[2];
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.SpecConstant && i.Operands[1] == countId);
        var reread = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(reread);
        var resolved = PipelineConstantResolver.Resolve(reread, new Dictionary<string, double> { ["7"] = 5 });
        Assert.Equal(5u, Assert.IsType<ShaderType.BindingArray>(resolved.Globals[0].Type).Length);
    }

    [Fact]
    public void SpirvPendingArrayTemporariesRoundtripButWgslLocalsStillRequireResolution()
    {
        var module = WgslReader.Parse("@id(7) override n=3u; var<workgroup> data:array<u32,n>; fn fetch(p:array<u32,n>)->u32 { return p[0]; } @compute @workgroup_size(1) fn main() { _=fetch(data); }");
        var reread = SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)); ModuleValidator.Validate(reread);
        Assert.NotEmpty(SpirvWriter.Write(reread, SpirvCompilationTarget.Default));
        Assert.Throws<ShaderException>(() => WgslWriter.Write(reread, SpirvCompilationTarget.Default));
        var resolved = PipelineConstantResolver.Resolve(reread, new Dictionary<string, double> { ["7"] = 5 });
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(resolved, SpirvCompilationTarget.Default)));
        Assert.Throws<ShaderException>(() => WgslReader.Parse("override n=3u; @compute @workgroup_size(1) fn main() { var local:array<u32,n>; }"));
    }
}
