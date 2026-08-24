namespace Sia.Spirv.Runtime;

public sealed record SpirvPushConstant(string Name, string Type, int Offset, int Size);
