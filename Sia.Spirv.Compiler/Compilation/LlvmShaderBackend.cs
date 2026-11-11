using System.Security.Cryptography;
using System.Text;
using Sia.Spirv.Compiler.LLVM;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Compilation;

internal sealed class LlvmShaderBackend(LlvmToolchain toolchain, SpirvManifestToolchain versions) : IOfflineShaderBackend
{
    public SpirvManifestToolchain Versions { get; } = versions;

    public static IOfflineShaderBackend Open(SpirvFileCompilationRequest request)
    {
        var toolchain = LlvmToolchain.Locate(request.ToolchainDirectory);
        var versions = new SpirvManifestToolchain(
            toolchain.GetLlvmVersion(),
            toolchain.GetSpirvToolsVersion(),
            request.EmitWgsl ? GetShaderTranslatorVersion() : null,
            request.EmitWgsl ? GetShaderTranslatorSha256() : null);
        return new LlvmShaderBackend(toolchain, versions);
    }

    public void Compile(SpirvFileCompilationRequest request, SpirvKernel kernel, ShaderArtifactPaths paths)
    {
        var target = request.Target;
        var module = new LlvmIrEmitter().Emit(request.AssemblyPath, kernel, target.KernelAbi);
        File.WriteAllText(paths.RawLlvm, module.Text, new UTF8Encoding(false));
        try {
            toolchain.Optimize(
                paths.RawLlvm,
                paths.Llvm,
                request.OptimizationLevel,
                request.LlvmPasses);
            toolchain.Compile(
                paths.Llvm,
                paths.Spirv,
                request.OptimizationLevel,
                target,
                kernel.Stage);
            toolchain.Validate(paths.Spirv, target.Environment);
            if (target.KernelAbi == SpirvKernelAbi.WebGpu) {
                toolchain.OptimizeForWebGpu(paths.Spirv);
            }
            SpirvResourceAccessLowering.Rewrite(paths.Spirv, kernel);
            ShaderTargetValidator.ValidateBinary(SpirvBinary.Parse(File.ReadAllBytes(paths.Spirv)), target);
            toolchain.Validate(paths.Spirv, target.Environment);
            if (request.EmitWgsl) {
                ConvertToWgsl(paths.Spirv, paths.Wgsl, target);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException) {
            throw new SpirvCompilationException(
                $"Failed to compile '{kernel.QualifiedName}':{Environment.NewLine}{exception.Message}");
        }
        finally {
            File.Delete(paths.RawLlvm);
        }
    }

    private static void ConvertToWgsl(string spirvPath, string wgslPath, SpirvCompilationTarget target)
    {
        var temporaryPath = $"{wgslPath}.tmp.wgsl";
        try {
            var module = SpirvReader.Parse(File.ReadAllBytes(spirvPath));
            var wgsl = WgslWriter.Write(module, target);
            File.WriteAllText(temporaryPath, wgsl);
            File.Move(temporaryPath, wgslPath, true);
        }
        catch (ShaderException exception) {
            throw new InvalidDataException($"Managed WGSL translation failed: {exception.Message}", exception);
        }
        finally {
            File.Delete(temporaryPath);
        }
    }

    private static string GetShaderTranslatorVersion() =>
        $"Sia.Spirv.Compiler/{typeof(ShaderTranslator).Assembly.GetName().Version}";

    private static string GetShaderTranslatorSha256()
    {
        using var stream = File.OpenRead(typeof(ShaderTranslator).Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
