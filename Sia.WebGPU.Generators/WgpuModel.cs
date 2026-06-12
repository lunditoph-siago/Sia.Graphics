namespace Sia.WebGPU.Generators;

internal sealed class WgpuHeader(
    WgpuEnum[] enums,
    WgpuHandle[] handles,
    WgpuStruct[] structs,
    WgpuCallback[] callbacks,
    WgpuFunction[] functions,
    WgpuConstant[] constants,
    WgpuStructInitializer[] initializers)
{
    public static readonly WgpuHeader Empty = new([], [], [], [], [], [], []);

    public WgpuEnum[] Enums { get; } = enums;

    public WgpuHandle[] Handles { get; } = handles;

    public WgpuStruct[] Structs { get; } = structs;

    public WgpuCallback[] Callbacks { get; } = callbacks;

    public WgpuFunction[] Functions { get; } = functions;

    public WgpuConstant[] Constants { get; } = constants;

    public WgpuStructInitializer[] Initializers { get; } = initializers;
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

internal sealed class WgpuConstant(
    string nativeName,
    string name,
    string type,
    string value,
    bool isCompileTimeConstant)
{
    public string NativeName { get; } = nativeName;

    public string Name { get; } = name;

    public string Type { get; } = type;

    public string Value { get; } = value;

    public bool IsCompileTimeConstant { get; } = isCompileTimeConstant;
}

internal sealed class WgpuStructInitializer(string macroName, string structName, WgpuInitializerField[] fields)
{
    public string MacroName { get; } = macroName;

    public string StructName { get; } = structName;

    public WgpuInitializerField[] Fields { get; } = fields;
}

internal sealed class WgpuInitializerField(string name, WgpuInitializerValue value)
{
    public string Name { get; } = name;

    public WgpuInitializerValue Value { get; } = value;
}

internal abstract class WgpuInitializerValue
{
    private protected WgpuInitializerValue()
    {
    }
}

internal sealed class WgpuScalarInitializerValue(string expression) : WgpuInitializerValue
{
    public string Expression { get; } = expression;
}

internal sealed class WgpuNestedInitializerValue(
    string structName,
    WgpuInitializerField[] fields) : WgpuInitializerValue
{
    public string StructName { get; } = structName;

    public WgpuInitializerField[] Fields { get; } = fields;
}
