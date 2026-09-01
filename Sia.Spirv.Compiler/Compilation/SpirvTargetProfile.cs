namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvTargetProfile
{
    public static SpirvTargetProfile Default { get; } = new();

    public bool SupportsStorageBuffers { get; init; } = true;

    public bool PreferUniformForBoundedReadOnlyBuffers { get; init; }

    public int MaxStorageBuffersPerShaderStage { get; init; } = int.MaxValue;

    public ulong MaxStorageBufferBindingSize { get; init; } = ulong.MaxValue;

    public int MaxUniformBuffersPerShaderStage { get; init; } = int.MaxValue;

    public ulong MaxUniformBufferBindingSize { get; init; } = ulong.MaxValue;
}
