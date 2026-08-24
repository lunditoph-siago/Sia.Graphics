using Sia.Spirv.Runtime;

namespace Sia.Spirv.Tests;

public sealed class SpirvArtifactRegistryTests
{
    [Fact]
    public void LoadsArtifactBySourceMethod()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sia-spirv-registry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try {
            var basePath = Path.Combine(directory, "kernel");
            File.WriteAllBytes(
                basePath + ".spv",
                [0x03, 0x02, 0x23, 0x07, 0, 4, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]);
            File.WriteAllText(
                basePath + ".spv.json",
                """
                {
                  "schemaVersion": 1,
                  "entryPoint": "Kernel",
                  "sourceMethod": "Example.Kernel",
                  "metadataToken": 1,
                  "workgroupSize": { "x": 1, "y": 1, "z": 1 },
                  "targetEnvironment": "vulkan1.2",
                  "spirvVersion": "1.4",
                  "resources": [],
                  "pushConstants": [],
                  "toolchain": { "llvm": "23", "spirvTools": "2026.2" },
                  "sourceHash": "00"
                }
                """);

            var registry = new SpirvArtifactRegistry();
            registry.LoadDirectory(directory);

            var artifact = registry.Get("Example.Kernel");
            Assert.Equal("Kernel", artifact.Manifest.EntryPoint);
            Assert.Equal(20, artifact.Bytecode.Length);
        }
        finally {
            Directory.Delete(directory, true);
        }
    }
}
