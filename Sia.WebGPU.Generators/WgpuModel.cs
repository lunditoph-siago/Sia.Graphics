namespace Sia.WebGPU.Generators;

internal sealed class WgpuHeader(
    WgpuEnum[] enums,
    WgpuHandle[] handles,
    WgpuStruct[] structs,
    WgpuCallback[] callbacks,
    WgpuFunction[] functions)
{
    public static readonly WgpuHeader Empty = new([], [], [], [], []);

    public WgpuEnum[] Enums { get; } = enums;

    public WgpuHandle[] Handles { get; } = handles;

    public WgpuStruct[] Structs { get; } = structs;

    public WgpuCallback[] Callbacks { get; } = callbacks;

    public WgpuFunction[] Functions { get; } = functions;
}

internal sealed class WgpuEnum(string name, string underlyingType, bool isFlags, WgpuEnumValue[] values)
{
    public string Name { get; } = name;

    public string UnderlyingType { get; } = underlyingType;

    public bool IsFlags { get; } = isFlags;

    public WgpuEnumValue[] Values { get; } = values;
}

internal sealed class WgpuEnumValue(string name, string value)
{
    public string Name { get; } = name;

    public string Value { get; } = value;
}

internal sealed class WgpuHandle(string name)
{
    public string Name { get; } = name;
}

internal sealed class WgpuStruct(string name, WgpuField[] fields)
{
    public string Name { get; } = name;

    public WgpuField[] Fields { get; } = fields;
}

internal sealed class WgpuField(string name, string type)
{
    public string Name { get; } = name;

    public string Type { get; } = type;
}

internal sealed class WgpuCallback(string name, string returnType, WgpuParameter[] parameters)
{
    public string Name { get; } = name;

    public string ReturnType { get; } = returnType;

    public WgpuParameter[] Parameters { get; } = parameters;
}

internal sealed class WgpuFunction(string name, string returnType, WgpuParameter[] parameters)
{
    public string Name { get; } = name;

    public string ReturnType { get; } = returnType;

    public WgpuParameter[] Parameters { get; } = parameters;
}

internal sealed class WgpuParameter(string name, string type)
{
    public string Name { get; } = name;

    public string Type { get; } = type;
}
