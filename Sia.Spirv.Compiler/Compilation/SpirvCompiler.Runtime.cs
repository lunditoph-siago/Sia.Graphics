using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Compilation;

public sealed partial class SpirvCompiler
{
    /// <summary>Lower one CIL shader to typed shader IR, using only explicit PE/metadata bytes.
    /// Call either shader writer to emit SPIR-V or WGSL without host tools.</summary>
    public Module CompileModule(ReadOnlyMemory<byte> assemblyImage, int metadataToken,
        ReadOnlyMemory<byte> intrinsicImage = default, SpirvCompilationOptions? options = null)
    {
        options ??= new SpirvCompilationOptions { KernelAbi = SpirvKernelAbi.WebGpu };
        var frontend = new SpirvFrontend().Analyze(assemblyImage, intrinsicImage);
        var errors = frontend.Diagnostics.Where(d => d.Severity == SpirvDiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new SpirvCompilationException(errors);
        var kernel = frontend.Kernels.SingleOrDefault(k => k.MetadataToken == metadataToken)
            ?? throw new ArgumentException("Token does not identify a shader entry point in the supplied assembly.", nameof(metadataToken));
        var legalized = new SpirvLegalizationPlanner().Resolve(kernel, options.TargetProfile, options.KernelAbi);
        return RuntimeShaderLowering.Run(assemblyImage, intrinsicImage, legalized.Kernel, options.KernelAbi);
    }
}
