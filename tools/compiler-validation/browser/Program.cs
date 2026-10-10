using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;

namespace Sia.Spirv.Compiler.Validation;

// This is a consumer test application. The compiler runs as an ordinary managed
// library within this application's one runtime; there is no compiler loader.
[SupportedOSPlatform("browser")]
public static partial class CompilerHost
{
    public static void Main() { }

    [JSExport]
    public static int Run(string fixturesJson)
    {
        using var fixtures = JsonDocument.Parse(fixturesJson);
        var root = fixtures.RootElement;
        var assembly = root.GetProperty("assembly").GetBytesFromBase64();
        var intrinsics = root.GetProperty("intrinsics").GetBytesFromBase64();
        var compiler = new SpirvCompiler();
        int passed = 0;
        foreach (var entry in root.GetProperty("entries").EnumerateArray()) {
            var request = new SpirvModuleCompilationRequest(assembly, entry.GetProperty("token").GetInt32(), intrinsics);
            var module = compiler.CompileModule(request);
            var wgsl = WgslWriter.Write(module, request.Target);
            var spirv = SpirvWriter.Write(module, new() { Target = request.Target });
            if (wgsl != entry.GetProperty("wgsl").GetString()
                || !spirv.AsSpan().SequenceEqual(entry.GetProperty("spirv").GetBytesFromBase64()))
                throw new InvalidOperationException(entry.GetProperty("name").GetString() + ": host/native mismatch");
            if (ShaderTranslator.WgslToSpirv(ShaderTranslator.SpirvToWgsl(spirv)).Length == 0)
                throw new InvalidOperationException("Empty shader translation roundtrip.");
            passed++;
        }
        if (passed == 0) throw new InvalidOperationException("No shader fixtures supplied.");
        return passed;
    }
}
