using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Tests;

public class RuntimeCompilerTests
{
    private static byte[] AssemblyImage() => File.ReadAllBytes(SpirvTestAssembly.Path);
    private static byte[] IntrinsicImage() => File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location);

    [Fact]
    public void MemoryFrontendMatchesFileFrontendWithoutExecutingInput()
    {
        var fromFile = SpirvTestAssembly.Analyze();
        var fromMemory = new SpirvFrontend().Analyze(AssemblyImage(), IntrinsicImage());
        Assert.Equal(fromFile.Kernels.Select(k => (k.MetadataToken, k.QualifiedName)),
            fromMemory.Kernels.Select(k => (k.MetadataToken, k.QualifiedName)));
        Assert.Equal(fromFile.Diagnostics, fromMemory.Diagnostics);
    }

    [Theory]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.Synchronize))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.CopyVectors))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.UseHelpers))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.AtomicWorkgroup))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.CopyStructs))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.CopyPackedStructs))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.CopyAlignedStructs))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.CopyLogicalStructs))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.CopyBoundedStructs))]
    [InlineData(typeof(FullscreenVertexShaders), nameof(FullscreenVertexShaders.Vertex))]
    [InlineData(typeof(ExplicitFragmentShaders), nameof(ExplicitFragmentShaders.Fragment))]
    [InlineData(typeof(ShortCircuitShaders), nameof(ShortCircuitShaders.Fragment))]
    [InlineData(typeof(TextureShaders), nameof(TextureShaders.SampleAndLoad))]
    [InlineData(typeof(MathShaders), nameof(MathShaders.IntegerAndBooleanVectors))]
    [InlineData(typeof(MathShaders), nameof(MathShaders.VectorBitcasts))]
    [InlineData(typeof(MathShaders), nameof(MathShaders.VectorHalfConversion))]
    [InlineData(typeof(MathShaders), nameof(MathShaders.VectorSelect))]
    [InlineData(typeof(MathShaders), nameof(MathShaders.Vectors))]
    [InlineData(typeof(MathShaders), nameof(MathShaders.SquareMatrices))]
    [InlineData(typeof(MathShaders), nameof(MathShaders.RectangularMatrices))]
    [InlineData(typeof(ControlFlowShaders), nameof(ControlFlowShaders.IntegerControlFlow))]
    [InlineData(typeof(ControlFlowShaders), nameof(ControlFlowShaders.SpeculativeSelection))]
    public void DirectCilProducesBothShaderFormats(Type declaringType, string name)
    {
        var token = SpirvTestAssembly.GetKernel(declaringType, name).MetadataToken;
        var request = new SpirvModuleCompilationRequest(AssemblyImage(), token, IntrinsicImage());
        var module = new SpirvCompiler().CompileModule(request);
        ModuleValidator.Validate(module);
        var wgsl = WgslWriter.Write(module);
        Assert.Contains("@" + SpirvTestAssembly.GetKernel(declaringType, name).Stage.ToString().ToLowerInvariant(), wgsl);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
        var binary = SpirvWriter.Write(module);
        ModuleValidator.Validate(SpirvReader.Parse(binary));
        var legacy = new SpirvCompiler().CompileModule(request.AssemblyImage, token, request.IntrinsicImage);
        Assert.Equal(wgsl, WgslWriter.Write(legacy));
        Assert.Equal(binary, SpirvWriter.Write(legacy));
    }

    [Theory]
    [InlineData(SpirvKernelAbi.WebGpu)]
    [InlineData(SpirvKernelAbi.Vulkan)]
    public void MemoryRequestAndLegacyAdapterUseTheSameExplicitAbi(SpirvKernelAbi abi)
    {
        var token = SpirvTestAssembly.GetKernel(typeof(ComputeShaders), nameof(ComputeShaders.UseHelpers)).MetadataToken;
        var request = new SpirvModuleCompilationRequest(AssemblyImage(), token, IntrinsicImage()) { KernelAbi = abi };
        var compiler = new SpirvCompiler();
        var module = compiler.CompileModule(request);
        var legacy = compiler.CompileModule(request.AssemblyImage, token, request.IntrinsicImage,
            new SpirvCompilationOptions { KernelAbi = abi });
        Assert.Equal(SpirvWriter.Write(legacy), SpirvWriter.Write(module));
    }

    [Fact]
    public void RuntimeCompilerRejectsMissingEntryAndEmptyImage()
    {
        Assert.Throws<ArgumentException>(() => new SpirvCompiler().CompileModule(AssemblyImage(), 0, IntrinsicImage()));
        Assert.Throws<ArgumentException>(() => new SpirvFrontend().Analyze(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void MemoryRequestRejectsInvalidTargetBeforeParsingThePe()
    {
        var compiler = new SpirvCompiler();
        var request = new SpirvModuleCompilationRequest(ReadOnlyMemory<byte>.Empty, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.CompileModule(request with { KernelAbi = (SpirvKernelAbi)99 }));
        Assert.Throws<InvalidDataException>(() => compiler.CompileModule(request with {
            TargetProfile = new SpirvTargetProfile { MaxStorageBuffersPerShaderStage = -1 }
        }));
        Assert.Throws<ArgumentNullException>(() => compiler.CompileModule(request with { TargetProfile = null! }));
    }

    [Fact]
    public void RuntimeCompilerDiagnosesBackedgeIntoPrologue()
    {
        var image = AssemblyImage();
        var token = SpirvTestAssembly.GetKernel(typeof(ComputeShaders), nameof(ComputeShaders.Synchronize)).MetadataToken;
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image));
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var method = reader.GetMethodDefinition((System.Reflection.Metadata.MethodDefinitionHandle)
            System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(token));
        var section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress
            && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
        int offset = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
        // A tiny method containing only br.s -2, without executing the test PE.
        image[offset] = (2 << 2) | 2;
        image[offset + 1] = 0x2b;
        image[offset + 2] = 0xfe;
        var error = Assert.Throws<InvalidDataException>(() =>
            new SpirvCompiler().CompileModule(image, token, IntrinsicImage()));
        Assert.Contains("backedge", error.Message);
    }
}
