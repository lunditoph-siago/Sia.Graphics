using Sia.Spirv.Runtime;

namespace Sia.Spirv.Examples;

internal static class Program
{
    public static int Main(string[] args)
    {
        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "spirv");
        var registry = new SpirvArtifactRegistry();
        registry.LoadDirectory(artifactDirectory);

        if (args.Length == 0) {
            Console.WriteLine("Available SPIR-V examples:");
            foreach (var artifact in registry.Artifacts.OrderBy(
                static artifact => artifact.Manifest.SourceMethod,
                StringComparer.Ordinal)) {
                Console.WriteLine($"  {artifact.Manifest.SourceMethod}");
            }
            return 0;
        }

        var requestedName = args[0];
        var selected = registry.Artifacts.FirstOrDefault(
            artifact => string.Equals(
                artifact.Manifest.SourceMethod,
                requestedName,
                StringComparison.OrdinalIgnoreCase) ||
              string.Equals(
                artifact.Manifest.EntryPoint,
                requestedName,
                StringComparison.OrdinalIgnoreCase));
        if (selected is null) {
            Console.Error.WriteLine($"SPIR-V example '{requestedName}' was not found.");
            return 1;
        }

        Console.WriteLine($"Loaded {selected.Manifest.SourceMethod}");
        Console.WriteLine($"  SPIR-V: {selected.SpirvPath}");
        Console.WriteLine(
            $"  Workgroup: {selected.Manifest.WorkgroupSize.X} x " +
            $"{selected.Manifest.WorkgroupSize.Y} x {selected.Manifest.WorkgroupSize.Z}");
        Console.WriteLine($"  Resources: {selected.Manifest.Resources.Count}");
        Console.WriteLine($"  Push constants: {selected.Manifest.PushConstants.Count}");
        return 0;
    }
}
