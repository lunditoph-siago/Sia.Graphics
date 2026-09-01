using Sia.WebGPU;

namespace Sia.Graphics.Compatibility;

public sealed record GpuBufferRequirement(
    string Name,
    WGPUShaderStage Visibility,
    GpuBufferAccess Access,
    bool RuntimeSized,
    ulong MinimumStorageBindingSize,
    ulong? UniformBindingSize = null);
