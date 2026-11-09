namespace Sia.Spirv.Compiler.Tests.Support;

internal static class NativeCompiler
{
    internal const string Tint = "SIA_TEST_TINT";
    internal const string Naga = "SIA_TEST_NAGA";

    internal static string ReadSpirv(string environmentVariable, byte[] binary)
    {
        using var directory = new TestDirectory();
        string input = directory.File("input.spv"), output = directory.File("output.wgsl");
        File.WriteAllBytes(input, binary);
        string[] arguments = environmentVariable switch {
            Tint => [input, "--format", "wgsl", "-o", output],
            Naga => [input, output],
            _ => throw new ArgumentException("Unknown native compiler.", nameof(environmentVariable))
        };
        TestProcess.Run(Executable(environmentVariable), arguments);
        return File.ReadAllText(output);
    }

    internal static string ReprintWithTint(string source)
    {
        using var directory = new TestDirectory();
        string input = directory.File("input.wgsl"), output = directory.File("output.wgsl");
        File.WriteAllText(input, source);
        TestProcess.Run(Executable(Tint), input, "--format", "wgsl", "-o", output);
        return File.ReadAllText(output);
    }

    private static string Executable(string variable)
        => Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"{variable} is not configured.");
}
