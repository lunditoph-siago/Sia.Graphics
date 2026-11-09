namespace Sia.Spirv.Compiler.Tests.Support;

internal sealed class TestDirectory : IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("sia-spirv-tests-").FullName;

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
