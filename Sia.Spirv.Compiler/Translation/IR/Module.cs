namespace Sia.Spirv.Compiler.Translation.IR;

/// <summary>Common, language-independent shader representation.</summary>
public sealed class Module
{
    /// <summary>Retain the native Vulkan memory model independently of shader features.</summary>
    public bool VulkanMemoryModel { get; set; }
    /// <summary>Generate implicit WGSL workgroup initialization. Native inputs already express their memory operations explicitly.</summary>
    public bool WorkgroupInitializationRequired { get; set; } = true;
    public List<ShaderType.Structure> Structures { get; } = [];
    public List<GlobalVariable> Globals { get; } = [];
    public List<ShaderConstant> Constants { get; } = [];
    public List<ShaderFunction> Functions { get; } = [];
    public HashSet<string> Enables { get; } = new(StringComparer.Ordinal);
    public List<DiagnosticFilter> DiagnosticFilters { get; } = [];
}

public enum DiagnosticSeverity { Off, Info, Warning, Error }
/// <summary>A WGSL diagnostic setting. A null namespace denotes a standard or unknown rule.</summary>
public sealed record DiagnosticFilter(DiagnosticSeverity Severity, string Rule, string? Namespace = null);

public sealed record ShaderConstant(string Name, ShaderType Type, Expression? Value,
    bool IsOverride = false, uint? OverrideId = null)
{
    /// <summary>A derived specialization value; depends on overrides but cannot be independently supplied through the pipeline API.</summary>
    public bool IsSpecialization { get; init; }
}
public sealed record GlobalVariable(string Name, ShaderType Type, AddressSpace Space,
    StorageAccess Access = StorageAccess.ReadWrite, ResourceBinding? Binding = null, Expression? Initializer = null)
{
    public MemoryDecorations MemoryDecorations { get; init; }
}
public sealed record FunctionArgument(string Name, ShaderType Type, IoBinding? Binding = null);

public sealed class ShaderFunction(string name)
{
    public string Name { get; set; } = name;
    public List<FunctionArgument> Arguments { get; } = [];
    public ShaderType ReturnType { get; set; } = new ShaderType.Void();
    public IoBinding? ReturnBinding { get; set; }
    public ShaderStage? Stage { get; set; }
    public string? TaskPayload { get; set; }
    public string? MeshOutput { get; set; }
    public Expression[] WorkgroupSize { get; set; } = [Expression.U32(1), Expression.U32(1), Expression.U32(1)];
    public Block Body { get; set; } = new();
    public bool EarlyDepthTest { get; set; }
    public string? ConservativeDepth { get; set; }
    public List<DiagnosticFilter> DiagnosticFilters { get; } = [];
}

public abstract record Expression(ShaderType Type)
{
    public SourceSpan Span { get; init; }
    public sealed record Literal(object Value, ShaderType ValueType) : Expression(ValueType);
    /// <summary>Variable references denote places; loads are explicit.</summary>
    public sealed record Reference(string Name, ShaderType ValueType) : Expression(ValueType);
    public sealed record Load(Expression Pointer) : Expression(Pointer.Type is ShaderType.Pointer p ? p.Base : Pointer.Type)
    {
        public SpirvMemoryAccess? MemoryAccess { get; init; }
    }
    public sealed record Unary(string Operator, Expression Operand, ShaderType ValueType) : Expression(ValueType);
    public sealed record Binary(string Operator, Expression Left, Expression Right, ShaderType ValueType) : Expression(ValueType);
    public sealed record Call(string Function, IReadOnlyList<Expression> Arguments, ShaderType ValueType) : Expression(ValueType)
    {
        /// <summary>Native atomic requirements. Null selects the WGSL builtin's default memory behavior.</summary>
        public SpirvAtomicMemory? AtomicMemory { get; init; }
        /// <summary>Native ordinary/cooperative memory access, including accesses to upgraded atomic places.</summary>
        public SpirvMemoryAccess? MemoryAccess { get; init; }
    }
    public sealed record Construct(ShaderType ValueType, IReadOnlyList<Expression> Components) : Expression(ValueType);
    public sealed record Convert(ShaderType ValueType, Expression Operand, bool Bitcast = false) : Expression(ValueType);
    public sealed record Access(Expression Base, Expression Index, ShaderType ValueType) : Expression(ValueType);
    public sealed record Member(Expression Base, string Name, ShaderType ValueType) : Expression(ValueType);
    public sealed record Swizzle(Expression Vector, string Components, ShaderType ValueType) : Expression(ValueType);
    public sealed record Select(Expression Condition, Expression Accept, Expression Reject) : Expression(Accept.Type);
    public static Literal U32(uint value) => new(value, ShaderType.U32);
    public static Literal I32(int value) => new(value, ShaderType.I32);
    public static Literal Bool(bool value) => new(value, ShaderType.Bool);
}

public sealed class Block
{
    public List<Statement> Statements { get; } = [];
}

public abstract record Statement
{
    public SourceSpan Span { get; init; }
    public sealed record Nested(Block Body) : Statement;
    public sealed record Declare(string Name, ShaderType Type, Expression? Initializer, bool Mutable = true) : Statement;
    public sealed record Store(Expression Target, Expression Value) : Statement
    {
        public SpirvMemoryAccess? MemoryAccess { get; init; }
    }
    public sealed record Evaluate(Expression Value) : Statement;
    public sealed record If(Expression Condition, Block Accept, Block Reject) : Statement;
    public sealed record Loop(Block Body, Block Continuing, Expression? BreakIf = null) : Statement;
    public sealed record Switch(Expression Selector, IReadOnlyList<SwitchCase> Cases) : Statement;
    public sealed record Return(Expression? Value = null) : Statement;
    public sealed record Break : Statement;
    public sealed record Continue : Statement;
    public sealed record Kill : Statement;
    public sealed record Barrier(bool Storage, bool Workgroup, bool Texture = false, bool Subgroup = false) : Statement
    {
        public SpirvBarrierMemory? NativeMemory { get; init; }
    }
    public sealed record MemoryBarrier(bool Storage, bool Workgroup, bool Texture = false, bool Subgroup = false) : Statement
    {
        public SpirvBarrierMemory? NativeMemory { get; init; }
    }
}

public sealed record SwitchCase(IReadOnlyList<Expression.Literal> Values, bool IsDefault, Block Body);
