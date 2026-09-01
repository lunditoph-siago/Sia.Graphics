namespace Sia.Spirv.Compiler.Model;

public sealed record ShaderStructType(
    string Name,
    IReadOnlyList<ShaderStructField> Fields);
