namespace Sia.Spirv.Compiler.Translation.IR;

public enum ScalarKind { Bool, Sint, Uint, Float, AbstractInt, AbstractFloat }
public enum AddressSpace { Function, Private, Workgroup, Uniform, Storage, Handle, Immediate, TaskPayload }
[Flags]
public enum StorageAccess { None = 0, Read = 1, Write = 2, ReadWrite = Read | Write, Atomic = 4 }
[Flags]
public enum MemoryDecorations { None = 0, Coherent = 1, Volatile = 2 }
public enum ShaderStage { Vertex, Fragment, Compute, Task, Mesh }
public enum ImageDimension { D1, D2, D3, Cube }
public enum BuiltinResultKind { Modf, Frexp, AtomicCompareExchange, RayDesc, RayIntersection }
public enum CooperativeRole { A, B, C }

public abstract record ShaderType
{
    public sealed record Void : ShaderType;
    public sealed record Scalar(ScalarKind Kind, int Width = 4) : ShaderType;
    public sealed record Vector(int Size, Scalar Component) : ShaderType;
    public sealed record Matrix(int Columns, int Rows, Scalar Component) : ShaderType;
    /// <summary>A distributed matrix; Scope uses SPIR-V scope values (3 is Subgroup).</summary>
    public sealed record CooperativeMatrix(int Columns, int Rows, Scalar Component, CooperativeRole Role, uint Scope = 3) : ShaderType;
    public sealed record Atomic(Scalar Component) : ShaderType;
    public sealed record Pointer(ShaderType Base, AddressSpace Space, StorageAccess Access = StorageAccess.ReadWrite) : ShaderType;
    public sealed record Array(ShaderType Element, uint? Length, uint? Stride = null) : ShaderType
    {
        /// <summary>Name of the override determining the length; mutually exclusive with Length.</summary>
        public string? OverrideLength { get; init; }
    }
    public sealed record BindingArray(ShaderType Element, uint? Length) : ShaderType
    {
        /// <summary>Name of the override determining the length; mutually exclusive with Length.</summary>
        public string? OverrideLength { get; init; }
    }
    public sealed record Structure(string Name, IReadOnlyList<StructMember> Members, BuiltinResultKind? BuiltinResult = null) : ShaderType;
    public sealed record Sampler(bool Comparison = false) : ShaderType;
    public sealed record AccelerationStructure(bool VertexReturn = false) : ShaderType;
    public sealed record RayQuery(bool VertexReturn = false) : ShaderType;
    public sealed record Image(ImageDimension Dimension, Scalar Component, bool Arrayed = false,
        bool Multisampled = false, bool Depth = false, string? StorageFormat = null,
        StorageAccess Access = StorageAccess.Read) : ShaderType;

    public static readonly Scalar Bool = new(ScalarKind.Bool, 1);
    public static readonly Scalar I16 = new(ScalarKind.Sint, 2);
    public static readonly Scalar U16 = new(ScalarKind.Uint, 2);
    public static readonly Scalar I32 = new(ScalarKind.Sint);
    public static readonly Scalar U32 = new(ScalarKind.Uint);
    public static readonly Scalar F32 = new(ScalarKind.Float);
    public static readonly Scalar F16 = new(ScalarKind.Float, 2);
}

public sealed record IoBinding(uint? Location = null, string? Builtin = null,
    string? Interpolation = null, string? Sampling = null, bool Invariant = false, uint? BlendSource = null, bool PerPrimitive = false);
public sealed record ResourceBinding(uint Group, uint Binding);
public sealed record StructMember(string Name, ShaderType Type, uint? Offset = null,
    uint? Alignment = null, uint? Size = null, IoBinding? Binding = null)
{
    public MemoryDecorations MemoryDecorations { get; init; }
}
