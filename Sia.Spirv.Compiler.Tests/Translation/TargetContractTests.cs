using System.Collections.Immutable;
using System.Globalization;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Tests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class TargetContractTests
{
    internal const string DescriptorArray = "enable wgpu_binding_array; @id(7) override n:u32; struct Data { value:u32 } @group(0) @binding(0) var<storage,read_write> data:binding_array<Data,n>; @compute @workgroup_size(1) fn main(){data[0].value=9u;}";
    [Fact]
    public void FileAndMemoryRequestsShareOneImmutableTargetDefault()
    {
        var memory = new SpirvModuleCompilationRequest(default, 0); var file = new SpirvFileCompilationRequest("shader.dll", "output");
        Assert.Same(memory.Target, file.Target); Assert.Equal(SpirvKernelAbi.WebGpu, file.Target.KernelAbi);
        Assert.Equal("vulkan1.2", file.Target.Environment); Assert.Equal(0x00010500u, file.Target.Version);
        var changed = memory with { Target = memory.Target with { KernelAbi = SpirvKernelAbi.Vulkan } };
        Assert.Equal(SpirvKernelAbi.WebGpu, memory.Target.KernelAbi); Assert.Equal(SpirvKernelAbi.Vulkan, changed.Target.KernelAbi);
    }

    [Fact]
    public void PublicCompilationSurfaceHasNoLegacyOverloadsOptionsOrForwardingProperties()
    {
        var methods = typeof(SpirvCompiler).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var memory = Assert.Single(methods, m => m.Name == nameof(SpirvCompiler.CompileModule));
        Assert.Equal(new[] { typeof(SpirvModuleCompilationRequest) }, memory.GetParameters().Select(p => p.ParameterType));
        var file = Assert.Single(methods, m => m.Name == nameof(SpirvCompiler.CompileAssembly));
        Assert.Equal(new[] { typeof(SpirvFileCompilationRequest) }, file.GetParameters().Select(p => p.ParameterType));
        var variants = Assert.Single(methods, m => m.Name == nameof(SpirvCompiler.CompileVariants));
        Assert.Equal(new[] { typeof(SpirvFileCompilationRequest), typeof(IReadOnlyDictionary<string, SpirvTargetProfile>) }, variants.GetParameters().Select(p => p.ParameterType));
        Assert.Null(typeof(SpirvCompiler).Assembly.GetType("Sia.Spirv.Compiler.Compilation.SpirvCompilationOptions"));
        Assert.Null(typeof(SpirvModuleCompilationRequest).GetProperty("KernelAbi"));
        Assert.Null(typeof(SpirvModuleCompilationRequest).GetProperty("TargetProfile"));
    }

    [Theory] [InlineData("target")] [InlineData("abi")] [InlineData("empty")] [InlineData("name")] [InlineData("limits")]
    public void VariantsRejectInvalidRequestsAndProfilesBeforeFileAccess(string defect)
    {
        var request = new SpirvFileCompilationRequest("missing.dll", "missing-output");
        IReadOnlyDictionary<string, SpirvTargetProfile> profiles = new Dictionary<string, SpirvTargetProfile> { ["mobile"] = SpirvTargetProfile.Default };
        if (defect == "target") request = request with { Target = null! };
        if (defect == "abi") request = request with { Target = request.Target with { KernelAbi = (SpirvKernelAbi)99 } };
        if (defect == "empty") profiles = new Dictionary<string, SpirvTargetProfile>();
        if (defect == "name") profiles = new Dictionary<string, SpirvTargetProfile> { ["bad--name"] = SpirvTargetProfile.Default };
        if (defect == "limits") profiles = new Dictionary<string, SpirvTargetProfile> { ["mobile"] = SpirvTargetProfile.Default with { MaxUniformBuffersPerShaderStage = -1 } };
        var error = Record.Exception(() => new SpirvCompiler().CompileVariants(request, profiles));
        Assert.NotNull(error); Assert.IsNotType<FileNotFoundException>(error);
        Assert.True(error is ArgumentException or InvalidDataException, error.ToString());
    }

    [Theory]
    [InlineData("environment")] [InlineData("version")] [InlineData("combination")]
    [InlineData("abi")] [InlineData("limits")] [InlineData("extension")] [InlineData("stage")]
    public void InvalidTargetsFailBeforeAnyPeOrFileAccess(string defect)
    {
        var target = SpirvCompilationTarget.Default;
        target = defect switch {
            "environment" => target with { Environment = "unknown" }, "version" => target with { Version = 0x00020000 },
            "combination" => target with { Version = 0x00010600 }, "abi" => target with { KernelAbi = (SpirvKernelAbi)99 },
            "limits" => target with { ResourceLimits = SpirvTargetProfile.Default with { MaxUniformBuffersPerShaderStage = -1 } },
            "extension" => target with { AllowedExtensions = ["invalid"] }, _ => target with { AllowedStages = [(ShaderStage)99] }
        };
        var compiler = new SpirvCompiler();
        Exception memory = Assert.ThrowsAny<Exception>(() => compiler.CompileModule(new SpirvModuleCompilationRequest(default, 0) { Target = target }));
        Exception file = Assert.ThrowsAny<Exception>(() => compiler.CompileAssembly(new SpirvFileCompilationRequest("missing.dll", "missing-output") { Target = target }));
        Assert.Equal(memory.GetType(), file.GetType()); Assert.Equal(memory.Message, file.Message);
        Assert.True(memory is ArgumentException or InvalidDataException);
    }

    [Theory]
    [InlineData(0x00010300u)] [InlineData(0x00010400u)] [InlineData(0x00010500u)] [InlineData(0x00010600u)]
    public void SelectedVersionControlsHeaderAndResourceInterfaces(uint version)
    {
        var module = WgslReader.Parse(CanonicalControlFlowTests.CapturedIndex);
        var target = SpirvCompilationTarget.Default with { Version = version, Environment = "vulkan1.3" };
        var options = new SpirvWriteOptions { Target = target };
        var bytes = SpirvWriter.Write(module, options); Assert.Equal(SpirvWriter.WriteWords(module, options), SpirvBinary.Parse(bytes).ToWords());
        var binary = SpirvBinary.Parse(bytes); Assert.Equal(version, binary.Version);
        var storage = binary.Instructions.Where(i => (Op)i.Opcode == Op.Variable && i.Operands[2] == 12).Select(i => i.Operands[1]).ToArray();
        var entry = binary.Instructions.Single(i => (Op)i.Opcode == Op.EntryPoint);
        _ = SpirvBinary.ReadString(entry.Operands.AsSpan(2), out int nameWords);
        var interfaces = entry.Operands.Skip(2 + nameWords).ToArray();
        Assert.Equal(2, storage.Length);
        if (version >= 0x00010400) Assert.All(storage, variable => Assert.Contains(variable, interfaces));
        else Assert.All(storage, variable => Assert.DoesNotContain(variable, interfaces));
    }

    [Fact]
    public void EmptyCapabilityPolicyIsDifferentFromUnrestrictedAndCannotSilentlyEmitShader()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}");
        var target = SpirvCompilationTarget.Default with { AllowedCapabilities = [] };
        Assert.Contains("capability 1", Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, new() { Target = target })).Message);
        Assert.NotEqual(target.Identity, SpirvCompilationTarget.Default.Identity);
        _ = SpirvWriter.Write(module, new() { Target = target with { AllowedCapabilities = [1] } });
    }

    [Fact]
    public void ExtensionsAndGeneratedWgslEnablesAreConstrainedInTheirOwnFormat()
    {
        var native = WgslReader.Parse("enable f16; @compute @workgroup_size(1) fn main(){let x=f16(1.0); _=x;}");
        var target = SpirvCompilationTarget.Default with { AllowedWgslEnables = [] };
        Assert.Throws<ShaderException>(() => WgslWriter.Write(native, target));
        var generated = new Module();
        var fragment = new ShaderFunction("fragment") { Stage = ShaderStage.Fragment };
        fragment.Arguments.Add(new("primitive", ShaderType.U32, new(Builtin: "primitive_index"))); generated.Functions.Add(fragment);
        Assert.Contains("primitive_index", Assert.Throws<ShaderException>(() => WgslWriter.Write(generated, target)).Message);
        var declared = new Module { VulkanMemoryModel = true }; declared.Functions.Add(new("main") { Stage = ShaderStage.Compute });
        Assert.Contains("SPV_KHR_vulkan_memory_model", Assert.Throws<ShaderException>(() => SpirvWriter.Write(declared,
            new() { Target = target with { AllowedExtensions = [] } })).Message);
        _ = SpirvWriter.Write(declared, new() { Target = target with { AllowedExtensions = ["SPV_KHR_vulkan_memory_model"] } });
    }

    [Fact]
    public void LocalSizeIdAndOfflineWgslRejectIncompatibleTargetsBeforeLowering()
    {
        var module = WgslReader.Parse("override n:u32; @compute @workgroup_size(n) fn main(){}");
        Assert.Contains("LocalSizeId", Assert.Throws<ShaderException>(() => SpirvWriter.Write(module,
            new() { Target = SpirvCompilationTarget.Default, UseLocalSizeId = true })).Message);
        var compiler = new SpirvCompiler();
        Assert.Throws<ArgumentException>(() => compiler.CompileAssembly(new SpirvFileCompilationRequest("missing.dll", "missing-output") {
            Target = SpirvCompilationTarget.Default with { KernelAbi = SpirvKernelAbi.Vulkan }, EmitWgsl = true
        }));
        Assert.Throws<ArgumentException>(() => compiler.CompileAssembly(new SpirvFileCompilationRequest("missing.dll", "missing-output") {
            Target = SpirvCompilationTarget.Default with { Environment = "universal" }
        }));
    }

    [Theory]
    [InlineData("stage")] [InlineData("bindings")] [InlineData("size")] [InlineData("storage")]
    public void BothManagedTargetsRejectDeniedStageAndUsedResources(string defect)
    {
        var module = WgslReader.Parse(CanonicalControlFlowTests.CapturedIndex);
        var target = SpirvCompilationTarget.Default;
        target = defect switch {
            "stage" => target with { AllowedStages = [ShaderStage.Vertex] },
            "bindings" => target with { ResourceLimits = target.ResourceLimits with { MaxStorageBuffersPerShaderStage = 1 } },
            "size" => target with { ResourceLimits = target.ResourceLimits with { MaxStorageBufferBindingSize = 3 } },
            _ => target with { ResourceLimits = target.ResourceLimits with { SupportsStorageBuffers = false } }
        };
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, new() { Target = target }));
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module, target));
    }

    [Fact]
    public void LimitsFollowHelperUsesAndRespectLocalShadowingAndUnusedResources()
    {
        const string source = "@group(0) @binding(0) var<storage,read> unused:array<u32>; @group(0) @binding(1) var<storage,read_write> used:array<u32>; fn helper(){used[0]=9u;} @compute @workgroup_size(1) fn main(){let unused=1u; _=unused; helper();}";
        var module = WgslReader.Parse(source);
        var target = SpirvCompilationTarget.Default with { ResourceLimits = SpirvTargetProfile.Default with { MaxStorageBuffersPerShaderStage = 1 } };
        _ = WgslWriter.Write(module, target); _ = SpirvWriter.Write(module, new() { Target = target });
        target = target with { ResourceLimits = target.ResourceLimits with { MaxStorageBuffersPerShaderStage = 0 } };
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module, target));
    }

    [Fact]
    public void ContinuingLocalUsedByBreakIfDoesNotBecomeAGlobalResourceUse()
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read> unused:array<u32>; @compute @workgroup_size(1) fn main(){loop{continuing{let unused=true;break if unused;}}}");
        var target = SpirvCompilationTarget.Default with { ResourceLimits = SpirvTargetProfile.Default with { SupportsStorageBuffers = false } };
        _ = WgslWriter.Write(module, target); _ = SpirvWriter.Write(module, new() { Target = target });
    }

    [Fact]
    public void DescriptorLimitsApplyAfterPipelineConstantResolutionWithoutChangingInput()
    {
        var module = WgslReader.Parse(DescriptorArray);
        var original = Assert.IsType<ShaderType.BindingArray>(module.Globals[0].Type);
        var target = SpirvCompilationTarget.Default with { ResourceLimits = SpirvTargetProfile.Default with { MaxStorageBuffersPerShaderStage = 2 } };
        _ = SpirvWriter.Write(module, new() { Target = target, PipelineConstants = new Dictionary<string, double> { ["7"] = 2 } });
        Assert.Contains("binding limit", Assert.Throws<ShaderException>(() => SpirvWriter.Write(module,
            new() { Target = target, PipelineConstants = new Dictionary<string, double> { ["7"] = 3 } })).Message);
        Assert.Null(original.Length); Assert.NotNull(original.OverrideLength); Assert.True(module.Constants[0].IsOverride);
    }

    [Fact]
    public void TargetIdentityIsOrderAndCultureIndependentAndIncludesEveryDimension()
    {
        var target = SpirvCompilationTarget.Default with { AllowedCapabilities = [1, 9], AllowedExtensions = ["SPV_Z", "SPV_A"], AllowedStages = [ShaderStage.Compute, ShaderStage.Vertex], AllowedWgslEnables = ["f16", "wgpu_int64"] };
        var reversed = target with { AllowedCapabilities = [9, 1], AllowedExtensions = ["SPV_A", "SPV_Z"], AllowedStages = [ShaderStage.Vertex, ShaderStage.Compute], AllowedWgslEnables = ["wgpu_int64", "f16"] };
        Assert.Equal(target.Identity, reversed.Identity);
        var original = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG"); Assert.Equal(target.Identity, reversed.Identity); }
        finally { CultureInfo.CurrentCulture = original; }
        foreach (var changed in new[] { target with { Version = 0x00010400 }, target with { Environment = "vulkan1.3" },
            target with { KernelAbi = SpirvKernelAbi.Vulkan }, target with { AllowedCapabilities = [] }, target with { AllowedExtensions = [] },
            target with { AllowedStages = [] }, target with { AllowedWgslEnables = [] }, target with { ResourceLimits = target.ResourceLimits with { MaxStorageBufferBindingSize = 8 } } })
            Assert.NotEqual(target.Identity, changed.Identity);
    }

    [Fact]
    public void EqualPolicyIdentitiesCannotHaveDifferentMeaningBecauseOfHostSetComparers()
    {
        var native = WgslReader.Parse("enable f16; @compute @workgroup_size(1) fn main(){let x=f16(1.0); _=x;}");
        var insensitive = SpirvCompilationTarget.Default with { AllowedWgslEnables = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "F16") };
        var ordinal = insensitive with { AllowedWgslEnables = ImmutableHashSet.Create(StringComparer.Ordinal, "F16") };
        Assert.Equal(ordinal.Identity, insensitive.Identity);
        Assert.Throws<ShaderException>(() => WgslWriter.Write(native, ordinal));
        Assert.Throws<ShaderException>(() => WgslWriter.Write(native, insensitive));
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}");
        var custom = SpirvCompilationTarget.Default with { AllowedCapabilities = ImmutableHashSet.Create(new EqualNumbers(), 42u) };
        var standard = custom with { AllowedCapabilities = [42] };
        Assert.Equal(standard.Identity, custom.Identity);
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, new() { Target = standard }));
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module, new() { Target = custom }));
    }

    private sealed class EqualNumbers : IEqualityComparer<uint>
    {
        public bool Equals(uint left, uint right) => true;
        public int GetHashCode(uint value) => 0;
    }
}
