namespace Sia.Spirv.Compiler.IL;

public abstract record CilOperand
{
    private CilOperand()
    {
    }

    public static implicit operator CilOperand(int value) =>
        new Int32(value);

    public static implicit operator CilOperand(long value) =>
        new Int64(value);

    public static implicit operator CilOperand(float value) =>
        new Float32(value);

    public static implicit operator CilOperand(double value) =>
        new Float64(value);

    public static implicit operator CilOperand(int[] targets) =>
        new SwitchTargets(targets);

    internal int GetInt32(int offset) => this switch {
        Int32(var value) => value,
        _ => throw Unexpected("Int32", offset)
    };

    internal float GetSingle(int offset) => this switch {
        Float32(var value) => value,
        _ => throw Unexpected("Single", offset)
    };

    internal int[] GetSwitchTargets(int offset) => this switch {
        SwitchTargets { Value: var targets } => targets,
        _ => throw Unexpected("switch targets", offset)
    };

    public sealed record None : CilOperand
    {
        public static None Instance { get; } = new();

        private None()
        {
        }
    }

    public sealed record Int32(int Value) : CilOperand;

    public sealed record Int64(long Value) : CilOperand;

    public sealed record Float32(float Value) : CilOperand;

    public sealed record Float64(double Value) : CilOperand;

    public sealed record SwitchTargets : CilOperand
    {
        public SwitchTargets(int[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        public int[] Value { get; }
    }

    private static InvalidDataException Unexpected(string expected, int offset) =>
        new($"Expected {expected} operand at IL_{offset:x4}.");
}
