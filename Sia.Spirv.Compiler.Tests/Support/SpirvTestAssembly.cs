using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class SpirvTestAssembly
{
    public static string Path => typeof(SpirvTestAssembly).Assembly.Location;

    public static SpirvFrontendResult Analyze() => new SpirvFrontend().Analyze(Path);

    public static SpirvKernel GetKernel(Type declaringType, string methodName)
    {
        var qualifiedName = $"{declaringType.FullName}.{methodName}";
        return Assert.Single(
            Analyze().Kernels,
            kernel => kernel.QualifiedName == qualifiedName);
    }

    internal static byte[] CompileRuntime(Type declaringType, string methodName, SpirvCompilationTarget? target = null)
    {
        var method = declaringType.GetMethod(methodName)
            ?? throw new ArgumentException($"Missing shader method '{methodName}'.", nameof(methodName));
        var module = new SpirvCompiler().CompileModule(new(
            File.ReadAllBytes(Path), method.MetadataToken, File.ReadAllBytes(typeof(Gpu).Assembly.Location)));
        return SpirvWriter.Write(module, target ?? SpirvCompilationTarget.Default);
    }
}
