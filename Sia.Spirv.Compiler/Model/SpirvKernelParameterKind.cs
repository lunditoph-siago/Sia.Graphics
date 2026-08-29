namespace Sia.Spirv.Compiler.Model;

public enum SpirvKernelParameterKind
{
    StageInput,
    SampledTexture2D,
    SampledTexture2DArray,
    Sampler,
    ReadOnlyStorageBuffer,
    StorageBuffer,
    WorkgroupMemory,
    PushConstant
}
