using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Translation.Legalization;

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
        var wgsl = WgslWriter.Write(module, request.Target);
        Assert.Contains("@" + SpirvTestAssembly.GetKernel(declaringType, name).Stage.ToString().ToLowerInvariant(), wgsl);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
        var binary = SpirvWriter.Write(module, request.Target);
        ModuleValidator.Validate(SpirvReader.Parse(binary));
        Assert.Equal(request.Target.Version, SpirvBinary.Parse(binary).Version);
    }

    [Theory]
    [InlineData(SpirvKernelAbi.WebGpu)]
    [InlineData(SpirvKernelAbi.Vulkan)]
    public void MemoryRequestUsesItsExplicitAbiForParameterStorage(SpirvKernelAbi abi)
    {
        var token = SpirvTestAssembly.GetKernel(typeof(ComputeShaders), nameof(ComputeShaders.Synchronize)).MetadataToken;
        var request = new SpirvModuleCompilationRequest(AssemblyImage(), token, IntrinsicImage()) {
            Target = SpirvCompilationTarget.Default with { KernelAbi = abi }
        };
        var compiler = new SpirvCompiler();
        var module = compiler.CompileModule(request);
        var parameters = Assert.Single(module.Globals, g => g.Name == "sia_parameters");
        Assert.Equal(abi == SpirvKernelAbi.WebGpu ? AddressSpace.Uniform : AddressSpace.Immediate, parameters.Space);
        Assert.Equal(abi == SpirvKernelAbi.WebGpu, parameters.Binding is not null);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, request.Target)));
    }

    [Fact]
    public void RuntimeCompilerRejectsMissingEntryAndEmptyImage()
    {
        Assert.Throws<ArgumentException>(() => new SpirvCompiler().CompileModule(new SpirvModuleCompilationRequest(AssemblyImage(), 0, IntrinsicImage())));
        Assert.Throws<ArgumentException>(() => new SpirvFrontend().Analyze(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void MemoryRequestRejectsInvalidTargetBeforeParsingThePe()
    {
        var compiler = new SpirvCompiler();
        var request = new SpirvModuleCompilationRequest(ReadOnlyMemory<byte>.Empty, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.CompileModule(request with { Target = request.Target with { KernelAbi = (SpirvKernelAbi)99 } }));
        Assert.Throws<InvalidDataException>(() => compiler.CompileModule(request with {
            Target = request.Target with { ResourceLimits = new SpirvTargetProfile { MaxStorageBuffersPerShaderStage = -1 } }
        }));
        Assert.Throws<ArgumentNullException>(() => compiler.CompileModule(request with { Target = request.Target with { ResourceLimits = null! } }));
    }

    [Fact]
    public void RuntimeCompilerKeepsLeadingBackedgeSeparateFromInitialization()
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
        var intrinsics = IntrinsicImage();
        var kernel = new SpirvFrontend().Analyze(image, intrinsics).Kernels.Single(k => k.MetadataToken == token);
        var raw = RuntimeShaderLowering.ReadCanonical(image, intrinsics, kernel, SpirvKernelAbi.WebGpu);
        var graph = raw.Functions[kernel.Name];
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Terminator!.Edges), e => e.Target == graph.Entry);
        var regions = CanonicalControlFlowRegions.Run(raw);
        Assert.Single(regions.Functions[kernel.Name].Loops);
        var module = new SpirvCompiler().CompileModule(new SpirvModuleCompilationRequest(image, token, intrinsics));
        Assert.Contains(module.Functions.Single(f => f.Stage is not null).Body.Statements, s => s is Statement.Loop);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
    }
}
