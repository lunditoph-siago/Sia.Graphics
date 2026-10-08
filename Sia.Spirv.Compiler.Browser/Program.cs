using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;

namespace Sia.Spirv.Compiler.Browser;

[SupportedOSPlatform("browser")]
public static partial class ShaderCompiler
{
    public static void Main() { }

    [JSExport]
    public static string SpirvToWgsl(string spirvBase64) =>
        ShaderTranslator.SpirvToWgsl(Convert.FromBase64String(spirvBase64));

    [JSExport]
    public static string WgslToSpirv(string wgsl) =>
        Convert.ToBase64String(ShaderTranslator.WgslToSpirv(wgsl));

    [JSExport]
    public static string IlToWgsl(string assemblyBase64, int metadataToken, string intrinsicBase64) =>
        WgslWriter.Write(new SpirvCompiler().CompileModule(Convert.FromBase64String(assemblyBase64), metadataToken,
            Convert.FromBase64String(intrinsicBase64)));

    [JSExport]
    public static string IlToSpirv(string assemblyBase64, int metadataToken, string intrinsicBase64) =>
        Convert.ToBase64String(SpirvWriter.Write(new SpirvCompiler().CompileModule(Convert.FromBase64String(assemblyBase64), metadataToken,
            Convert.FromBase64String(intrinsicBase64))));
}
