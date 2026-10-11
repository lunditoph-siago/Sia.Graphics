using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Compilation;

public sealed partial class SpirvCompiler
{
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
        var legalized = new SpirvLegalizationPlanner().Resolve(kernel, request.Target.ResourceLimits, request.Target.KernelAbi);
        return RuntimeShaderLowering.Run(request.AssemblyImage, request.IntrinsicImage, legalized.Kernel, request.Target.KernelAbi);
    }
}
