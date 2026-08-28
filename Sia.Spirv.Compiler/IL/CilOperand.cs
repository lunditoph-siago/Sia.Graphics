namespace Sia.Spirv.Compiler.IL;

public enum CilOperandKind : byte
{
    None,
    Int32,
    Int64,
    Float32,
    Float64,
    SwitchTargets
}

public readonly struct CilOperand
{
    private readonly CilOperandKind _kind;
    private readonly long _scalar;
    private readonly int[]? _switchTargets;

    private CilOperand(CilOperandKind kind, long scalar, int[]? switchTargets)
    {
        _kind = kind;
        _scalar = scalar;
        _switchTargets = switchTargets;
    }

    public static CilOperand None => default;

    public CilOperandKind Kind => _kind;

    public object? Value => _kind switch {
        CilOperandKind.None => null,
        CilOperandKind.Int32 => unchecked((int)_scalar),
        CilOperandKind.Int64 => _scalar,
        CilOperandKind.Float32 => BitConverter.Int32BitsToSingle(unchecked((int)_scalar)),
        CilOperandKind.Float64 => BitConverter.Int64BitsToDouble(_scalar),
        CilOperandKind.SwitchTargets => _switchTargets,
        _ => throw new InvalidOperationException($"Unknown CIL operand kind '{_kind}'.")
    };

    public static implicit operator CilOperand(int value) =>
        new(CilOperandKind.Int32, value, null);

    public static implicit operator CilOperand(long value) =>
        new(CilOperandKind.Int64, value, null);

    public static implicit operator CilOperand(float value) =>
        new(CilOperandKind.Float32, BitConverter.SingleToInt32Bits(value), null);

    public static implicit operator CilOperand(double value) =>
        new(CilOperandKind.Float64, BitConverter.DoubleToInt64Bits(value), null);

    public static implicit operator CilOperand(int[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return new CilOperand(CilOperandKind.SwitchTargets, 0, targets);
    }

    internal int GetInt32(int offset)
    {
        if (_kind != CilOperandKind.Int32) {
            throw Unexpected("Int32", offset);
        }
        return unchecked((int)_scalar);
    }

    internal float GetSingle(int offset)
    {
        if (_kind != CilOperandKind.Float32) {
            throw Unexpected("Single", offset);
        }
        return BitConverter.Int32BitsToSingle(unchecked((int)_scalar));
    }

    internal int[] GetSwitchTargets(int offset)
    {
        if (_kind != CilOperandKind.SwitchTargets) {
            throw Unexpected("switch targets", offset);
        }
        return _switchTargets!;
    }

    private static InvalidDataException Unexpected(string expected, int offset) =>
        new($"Expected {expected} operand at IL_{offset:x4}.");
}
