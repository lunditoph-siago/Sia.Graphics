namespace Sia.Spirv.Compiler.Model;

public enum SpirvKernelParameterKind
{
    SampledTexture2D,
    SampledTexture2DArray,
    Sampler,
    ReadOnlyStorageBuffer,
    StorageBuffer,
    WorkgroupMemory,
    PushConstant
}
