using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvEntryMetadataTests
{
    internal const string Workgroup = "@id(7) override width=3u; @compute @workgroup_size(width+1u,2i,1u) fn main() {}";
    internal const string Different = "@id(7) override first=3u; @id(8) override second=3u; @compute @workgroup_size(first) fn a() {} @compute @workgroup_size(second) fn b() {}";
    internal const string Depth = "@fragment @early_depth_test(greater_equal) fn main()->@builtin(frag_depth) f32{return 0.25;}";
    internal const string DispatchShared = "@id(7) override width=3u; @group(0) @binding(0) var<storage,read_write> output:array<u32>; @compute @workgroup_size(width+1u) fn main(@builtin(local_invocation_index) i:u32){output[i]=width+i;}";
    internal const string DispatchDifferent = "@id(7) override width=3u; @id(8) override height=5u; @group(0) @binding(0) var<storage,read_write> output:array<u32>; @compute @workgroup_size(width) fn first(@builtin(local_invocation_index) i:u32){output[i]=width+i;} @compute @workgroup_size(height) fn second(@builtin(local_invocation_index) i:u32){output[i]=height+i;}";

    [Fact]
    public void OverrideDefaultsAndExternalIdsArePreparedOnce()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse("@id(0) override reserved=3u; override generated=4u; @compute @workgroup_size(generated) fn main(){}"), null, true, true, true);
        var overrides = prepared.PhysicalLayout.EntryAbi!.Overrides;
        Assert.Equal(0u, overrides["reserved"].SpecId); Assert.Equal(1u, overrides["generated"].SpecId);
        Assert.Equal(4u, overrides["generated"].Default.Value);
        byte[] before = SpirvWriter.Emit(prepared).ToBytes();
        int at = prepared.Module.Constants.FindIndex(c => c.Name == "generated");
        prepared.Module.Constants[at] = prepared.Module.Constants[at] with { OverrideId = 42, Value = Expression.U32(99) };
        Assert.Equal(before, SpirvWriter.Emit(prepared).ToBytes());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void PreparedWorkgroupPolicySurvivesSourceChanges(bool ids)
    {
        var module = WgslReader.Parse(Workgroup); string borrowed = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true, useLocalSizeId: ids, version: 0x10400);
        Assert.Equal(borrowed, WgslWriter.Write(module, SpirvCompilationTarget.Default));
        var metadata = prepared.PhysicalLayout.EntryAbi!.Entries["main"];
        var mode = Assert.Single(metadata.Modes);
        Assert.Equal(ids ? 38u : 17u, mode.Mode);
        if (!ids) Assert.Equal(new uint[] { 4, 2, 1 }, mode.Literals);
        byte[] before = SpirvWriter.Emit(prepared).ToBytes();
        var function = prepared.Module.Functions.Single(f => f.Name == "main");
        function.WorkgroupSize[0] = Expression.U32(0); function.Stage = null;
        byte[] after = SpirvWriter.Emit(prepared).ToBytes();
        Assert.Equal(before, after);
        var binary = SpirvBinary.Parse(after);
        Assert.Equal(0x10400u, binary.Version);
        Assert.Equal(ids, binary.Instructions.Any(i => (Op)i.Opcode == Op.ExecutionModeId));
        ModuleValidator.ValidateNative(SpirvReader.Parse(after));
    }

    [Fact]
    public void EqualDefaultsDoNotIdentifyDifferentOverrides()
    {
        var module = WgslReader.Parse(Different);
        var error = Assert.Throws<ShaderException>(() => ShaderTargetLowering.ForSpirv(module, null, true, true, true));
        Assert.Equal(DiagnosticStage.SpirvWrite, error.Diagnostic.Stage); Assert.Contains("UseLocalSizeId", error.Message);
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true, useLocalSizeId: true);
        var a = prepared.PhysicalLayout.EntryAbi!.Entries["a"].Modes.Single().Values![0];
        var b = prepared.PhysicalLayout.EntryAbi.Entries["b"].Modes.Single().Values![0];
        Assert.NotEqual(a.Identity, b.Identity);
        Assert.Equal("first", Assert.IsType<SpirvSpecializationValue.Reference>(a).Name);
        Assert.Equal("second", Assert.IsType<SpirvSpecializationValue.Reference>(b).Name);
    }

    [Fact]
    public void EquivalentDerivedConstantsShareTheWorkgroupBuiltinBeforeIdsExist()
    {
        var module = WgslReader.Parse("@id(7) override width=3u; @compute @workgroup_size(width) fn a() {} @compute @workgroup_size(width) fn b() {}");
        foreach (string name in new[] { "left", "right" }) module.Constants.Add(new ShaderConstant(name, ShaderType.U32,
            new Expression.Binary("+", new Expression.Reference("width", ShaderType.U32), Expression.U32(1), ShaderType.U32)) { IsSpecialization = true });
        module.Functions[0].WorkgroupSize[0] = new Expression.Reference("left", ShaderType.U32);
        module.Functions[1].WorkgroupSize[0] = new Expression.Reference("right", ShaderType.U32);
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true);
        var entries = prepared.PhysicalLayout.EntryAbi!.Entries;
        Assert.Same(entries["a"].WorkgroupBuiltin, entries["b"].WorkgroupBuiltin);
        var binary = SpirvWriter.Emit(prepared);
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 11, 25]);
        ModuleValidator.ValidateNative(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void FragmentModesArePreparedWithoutConsultingMutableSourceFlagsDuringEmission()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Depth), null, true, true, true);
        Assert.Equal(new uint[] { 7, 12, 9, 14 }, prepared.PhysicalLayout.EntryAbi!.Entries["main"].Modes.Select(m => m.Mode));
        byte[] before = SpirvWriter.Emit(prepared).ToBytes();
        var function = prepared.Module.Functions.Single(f => f.Name == "main");
        function.EarlyDepthTest = false; function.ConservativeDepth = "less_equal"; function.Stage = null;
        Assert.Equal(before, SpirvWriter.Emit(prepared).ToBytes());
    }

    [Theory]
    [InlineData("view_index", "u32", "", "fragment", 4439u, 4440u, "SPV_KHR_multiview")]
    [InlineData("draw_index", "u32", "enable draw_index;", "vertex", 4427u, 4426u, "SPV_KHR_shader_draw_parameters")]
    [InlineData("barycentric", "vec3f", "", "fragment", 5284u, 5286u, "SPV_KHR_fragment_shader_barycentric")]
    [InlineData("barycentric_no_perspective", "vec3f", "", "fragment", 5284u, 5287u, "SPV_KHR_fragment_shader_barycentric")]
    public void GraphicsInterfaceRequirementsExistBeforeSerialization(string builtin, string type, string enable, string stage, uint capability, uint decoration, string extension)
    {
        string result = stage == "vertex" ? "->@builtin(position) vec4f{return vec4f(f32(value),0.,0.,1.);}" : "{}";
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse($"{enable} @{stage} fn main(@builtin({builtin}) value:{type}) {result}"), null, true, true, true);
        var field = prepared.PhysicalLayout.EntryAbi!.Entries["main"].Interfaces.Single(p => p.Key.Input).Value;
        Assert.Contains(capability, field.Capabilities); Assert.Contains(extension, field.Extensions);
        Assert.Contains(field.Decorations, d => d.Decoration == 11 && d.Operands.SequenceEqual(new[] { decoration }));
    }

    [Theory] [InlineData("Triangles")] [InlineData("Specialized")]
    public void MeshVersionAndInterfaceRequirementsAreTargetFacts(string variant)
    {
        var prepared = ShaderTargetLowering.ForSpirv(SpirvMeshPublicationTests.PublicationFixture(variant), null, true, true, true);
        var abi = prepared.PhysicalLayout.EntryAbi!;
        Assert.Equal(0x10400u, abi.Version); Assert.Contains(5283u, abi.Capabilities); Assert.Contains("SPV_EXT_mesh_shader", abi.Extensions);
        Assert.Equal(3, abi.Entries.Count); Assert.All(abi.Entries.Values, entry => Assert.True(entry.IncludeGlobalInterfaces));
        Assert.Equal(5364u, abi.Entries["task_main"].Stage); Assert.Equal(5365u, abi.Entries["mesh_main"].Stage); Assert.Equal(4u, abi.Entries["fragment_main"].Stage);
        Assert.Contains(abi.Entries["mesh_main"].Modes, m => m.Mode == 26); Assert.Contains(abi.Entries["mesh_main"].Modes, m => m.Mode == 5270); Assert.NotEmpty(abi.MeshInterfaces);
        if (variant == "Specialized") Assert.Same(abi.Entries["task_main"].WorkgroupBuiltin, abi.Entries["mesh_main"].WorkgroupBuiltin);
        var error = Assert.Throws<ShaderException>(() => ShaderTargetLowering.ForSpirv(SpirvMeshPublicationTests.PublicationFixture(variant), null, true, true, true, version: 0x10300));
        Assert.Equal(DiagnosticStage.SpirvWrite, error.Diagnostic.Stage);
    }
}
