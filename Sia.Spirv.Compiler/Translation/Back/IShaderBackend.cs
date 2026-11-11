using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Back;

/// <summary>Defines the target-specific steps from canonical IR to a validated artifact.</summary>
internal interface IShaderBackend<TPrepared, TArtifact>
{
    void ValidateInput(Module module, SpirvCompilationTarget target);

    TPrepared Prepare(CanonicalModule module, SpirvCompilationTarget target);

    void ValidatePrepared(TPrepared module, SpirvCompilationTarget target);

    TArtifact Emit(TPrepared module);

    void ValidateArtifact(TArtifact artifact, SpirvCompilationTarget target);
}

internal static class ShaderOutputPipeline
{
    public static TArtifact Write<TPrepared, TArtifact>(
        Module input,
        SpirvCompilationTarget target,
        IShaderBackend<TPrepared, TArtifact> backend)
    {
        ArgumentNullException.ThrowIfNull(target);
        backend.ValidateInput(input, target);

        var canonical = CanonicalShaderPipeline.Prepare(input);
        var prepared = backend.Prepare(canonical, target);
        backend.ValidatePrepared(prepared, target);

        var artifact = backend.Emit(prepared);
        backend.ValidateArtifact(artifact, target);
        return artifact;
    }
}
