namespace Sia.Spirv.Compiler.Tests.Support;

public sealed class NativeCompilerTheoryAttribute : TheoryAttribute
{
    public NativeCompilerTheoryAttribute(string environmentVariable)
    {
        // An absent optional tool skips. A configured but broken path must fail
        // when started, so a setup mistake cannot silently turn CI green.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable))) {
            Skip = $"Set {environmentVariable} to the native compiler executable.";
        }
    }
}
