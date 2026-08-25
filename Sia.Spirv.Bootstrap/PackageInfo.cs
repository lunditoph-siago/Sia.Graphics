using System.Reflection;

namespace Sia.Spirv.Bootstrap;

internal static class PackageInfo
{
    public static string Version { get; } = typeof(PackageInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "SiaSpirvPackageVersion")
        .Value!;
}
