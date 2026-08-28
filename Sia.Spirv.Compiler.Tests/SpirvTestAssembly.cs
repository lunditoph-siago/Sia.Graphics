using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Tests;

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
}
