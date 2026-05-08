namespace Sia.WebGPU.Generators;

public sealed class WgpuGenerationOptions(
    string ns = "Sia.WebGPU",
    string className = "WgpuUnsafe",
    bool generateUnsafeBindings = true)
{
    public string Namespace { get; } = ns;

    public string ClassName { get; } = className;

    public bool GenerateUnsafeBindings { get; } = generateUnsafeBindings;
}
