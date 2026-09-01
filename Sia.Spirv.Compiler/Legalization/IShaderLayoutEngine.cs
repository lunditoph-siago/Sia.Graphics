using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

public interface IShaderLayoutEngine
{
    PhysicalStructLayout Legalize(
        ShaderStructType type,
        ShaderAddressSpace addressSpace);
}
