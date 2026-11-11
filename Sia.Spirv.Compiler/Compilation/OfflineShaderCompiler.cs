using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation.Legalization;

namespace Sia.Spirv.Compiler.Compilation;

internal static class OfflineShaderCompiler
{
    public static IReadOnlyList<SpirvArtifact> Compile(
        SpirvFileCompilationRequest request,
        string? targetName,
        Func<SpirvFileCompilationRequest, IOfflineShaderBackend> openBackend)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        var target = request.Target;
        target.Validate(offline: true, wgsl: request.EmitWgsl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AssemblyPath, "assemblyPath");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory, "outputDirectory");

        var frontend = new SpirvFrontend().Analyze(request.AssemblyPath);
        var errors = frontend.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == SpirvDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0) {
            throw new SpirvCompilationException(errors);
        }
        if (frontend.Kernels.Count == 0) {
            return [];
        }
        foreach (var kernel in frontend.Kernels) {
            ShaderTargetValidator.ValidateStage(target, kernel.Stage.ToString());
        }

        var backend = openBackend(request);
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.AssemblyPath)));
        Directory.CreateDirectory(request.OutputDirectory);

        var store = new ShaderArtifactStore(request, targetName);
        var artifacts = new List<SpirvArtifact>(frontend.Kernels.Count);
        foreach (var kernel in frontend.Kernels) {
            artifacts.Add(CompileKernel(request, kernel, backend, store, assemblyHash));
        }
        return artifacts;
    }

    private static SpirvArtifact CompileKernel(
        SpirvFileCompilationRequest request,
        SpirvKernel sourceKernel,
        IOfflineShaderBackend backend,
        ShaderArtifactStore store,
        string assemblyHash)
    {
        var target = request.Target;
        var plan = new SpirvLegalizationPlanner().Resolve(sourceKernel, target.ResourceLimits, target.KernelAbi);
        var paths = store.GetPaths(plan.Kernel);

        if (!request.EmitWgsl) {
            File.Delete(paths.Wgsl);
        }
        var sourceHash = ComputeSourceHash(assemblyHash, plan.Kernel, request, plan, backend.Versions, target.Identity);
        if (store.ReadCached(plan.Kernel, paths, sourceHash) is { } artifact) {
            return artifact;
        }
        if (request.EmitWgsl) {
            File.Delete(paths.Wgsl);
        }

        backend.Compile(request, plan.Kernel, paths);
        return store.Publish(plan, paths, backend.Versions, sourceHash);
    }

    private static string ComputeSourceHash(
        string assemblyHash,
        SpirvKernel kernel,
        SpirvFileCompilationRequest request,
        SpirvLegalizationPlan legalizationPlan,
        SpirvManifestToolchain versions,
        string targetIdentity)
    {
        var compilerVersion = typeof(SpirvCompiler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        var compilerHash = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(SpirvCompiler).Assembly.Location)));
        var input = string.Join(
            '|',
            assemblyHash,
            kernel.MetadataToken,
            kernel.Stage,
            compilerVersion,
            compilerHash,
            request.Target.Environment,
            targetIdentity,
            request.Target.KernelAbi,
            request.EmitWgsl,
            request.OptimizationLevel,
            request.LlvmPasses,
            request.EmitLlvmIr,
            request.Target.ResourceLimits.SupportsStorageBuffers,
            request.Target.ResourceLimits.PreferUniformForBoundedReadOnlyBuffers,
            request.Target.ResourceLimits.MaxStorageBuffersPerShaderStage,
            request.Target.ResourceLimits.MaxStorageBuffersInVertexStage,
            request.Target.ResourceLimits.MaxStorageBuffersInFragmentStage,
            request.Target.ResourceLimits.MaxStorageBufferBindingSize,
            request.Target.ResourceLimits.MaxUniformBuffersPerShaderStage,
            request.Target.ResourceLimits.MaxUniformBufferBindingSize,
            string.Join(',', legalizationPlan.StrategyIds),
            versions.Llvm,
            versions.SpirvTools,
            versions.Translator,
            versions.TranslatorSha256);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

}
