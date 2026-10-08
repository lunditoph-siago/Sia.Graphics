using System.Security.Cryptography;
using Sia.Spirv.Compiler.Translation;

namespace Sia.Spirv.Compiler.Compilation;

public sealed partial class SpirvCompiler
{
    private static void ConvertToWgsl(string spirvPath, string wgslPath)
    {
        var temporaryPath = $"{wgslPath}.tmp.wgsl";
        try {
            var wgsl = ShaderTranslator.SpirvToWgsl(File.ReadAllBytes(spirvPath));
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
