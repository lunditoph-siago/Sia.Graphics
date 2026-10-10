using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Compilation;

public sealed partial class SpirvCompiler
{
    /// <summary>Lower one CIL shader to typed shader IR, using only explicit PE/metadata bytes.
    /// Call either shader writer to emit SPIR-V or WGSL without host tools.
    /// This legacy adapter uses only KernelAbi and TargetProfile from the offline options.</summary>
    public Module CompileModule(ReadOnlyMemory<byte> assemblyImage, int metadataToken,
        ReadOnlyMemory<byte> intrinsicImage = default, SpirvCompilationOptions? options = null)
        => CompileModule(new SpirvModuleCompilationRequest(assemblyImage, metadataToken, intrinsicImage) {
            KernelAbi = options?.KernelAbi ?? SpirvKernelAbi.WebGpu,
            TargetProfile = options is null ? SpirvTargetProfile.Default : options.TargetProfile
        });

    /// <summary>Lower an explicit memory request to typed shader IR without file or process access.</summary>
    public Module CompileModule(SpirvModuleCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        request.Target.Validate();
        var frontend = new SpirvFrontend().Analyze(request.AssemblyImage, request.IntrinsicImage);
        var errors = frontend.Diagnostics.Where(d => d.Severity == SpirvDiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new SpirvCompilationException(errors);
        var kernel = frontend.Kernels.SingleOrDefault(k => k.MetadataToken == request.MetadataToken)
            ?? throw new ArgumentException("Token does not identify a shader entry point in the supplied assembly.", nameof(request.MetadataToken));
        Translation.Legalization.ShaderTargetValidator.ValidateStage(request.Target, kernel.Stage.ToString());
        var legalized = new SpirvLegalizationPlanner().Resolve(kernel, request.TargetProfile, request.KernelAbi);
        return RuntimeShaderLowering.Run(request.AssemblyImage, request.IntrinsicImage, legalized.Kernel, request.KernelAbi);
    }
}
