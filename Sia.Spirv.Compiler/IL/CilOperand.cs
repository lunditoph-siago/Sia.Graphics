namespace Sia.Spirv.Compiler.IL;

public sealed class CilNoOperand
{
    public static CilNoOperand Instance { get; } = new();

    private CilNoOperand()
    {
    }
}

public union CilOperand(CilNoOperand, int, long, float, double, int[])
{
    internal int GetInt32(int offset) => this switch {
        int value => value,
        _ => throw Unexpected("Int32", offset)
    };

    internal float GetSingle(int offset) => this switch {
        float value => value,
        _ => throw Unexpected("Single", offset)
    };

    internal int[] GetSwitchTargets(int offset) => this switch {
        int[] targets => targets,
        _ => throw Unexpected("switch targets", offset)
    };

    private static InvalidDataException Unexpected(string expected, int offset) =>
        new($"Expected {expected} operand at IL_{offset:x4}.");
}
