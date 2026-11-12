using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    public static byte[] Write(Module module, SpirvCompilationTarget target, SpirvWriteOptions? options = null) => WriteModule(module, target, options).ToBytes();

    public static uint[] WriteWords(Module module, SpirvCompilationTarget target, SpirvWriteOptions? options = null) => WriteModule(module, target, options).ToWords();

    private static SpirvBinary WriteModule(Module module, SpirvCompilationTarget target, SpirvWriteOptions? options)
        => ShaderOutputPipeline.Write(module, target, options is null ? SpirvBackend.Default : new SpirvBackend(options));
}
