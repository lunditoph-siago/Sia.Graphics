using System.Diagnostics;
using Sia.Spirv;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;

namespace Sia.Spirv.Compiler.Tests;

public sealed class IlQualitySemanticsTests
{
    [Theory]
    [InlineData(nameof(QualityShaders.SwapLoop), 1u, 2u)]
    [InlineData(nameof(QualityShaders.SwitchAndContinue), 7u, 11u)]
    [InlineData(nameof(QualityShaders.CaptureBeforeWrite), 10u, 18u)]
    [InlineData(nameof(QualityShaders.RepeatedHelperCalls), 10u, 66u)]
    public void RuntimeIlRoundtripPreservesExpectedMemory(string name, uint first, uint second)
    {
        string wgsl = ShaderTranslator.SpirvToWgsl(Compile(name), SpirvCompilationTarget.Default);
        Assert.Equal(new[] { first, second }, WgslExecutionTests.Execute(wgsl).Memory);
    }

    [UpstreamCompilerFact]
    public void NativeTintAndNagaPreserveExtendedIlResults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sia-spirv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            foreach (var (name, expected) in new[] {
                (nameof(QualityShaders.SwapLoop), new uint[] { 1, 2 }),
                (nameof(QualityShaders.SwitchAndContinue), new uint[] { 7, 11 }),
                (nameof(QualityShaders.CaptureBeforeWrite), new uint[] { 10, 18 }),
                (nameof(QualityShaders.RepeatedHelperCalls), new uint[] { 10, 66 })
            }) {
                string input = Path.Combine(directory, "input.spv"), output = Path.Combine(directory, "output.wgsl");
                // Tint's reader targets SPIR-V 1.3; compile that target directly.
                File.WriteAllBytes(input, Compile(name, new() { Version = 0x00010300 }));
                foreach (string tool in new[] { "SIA_TEST_TINT", "SIA_TEST_NAGA" }) {
                    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable(tool)!) {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                    };
                    foreach (string argument in tool == "SIA_TEST_TINT" ? new[] { input, "--format", "wgsl", "-o", output } : new[] { input, output })
                        start.ArgumentList.Add(argument);
                    using var process = Process.Start(start)!;
                    var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
                    bool completed = process.WaitForExit(30000);
                    if (!completed) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                    Assert.True(completed, tool + " timed out.");
                    Assert.True(process.ExitCode == 0, stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
                    Assert.Equal(expected, WgslExecutionTests.Execute(File.ReadAllText(output)).Memory);
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static byte[] Compile(string name, SpirvCompilationTarget? target = null)
    {
        var module = new SpirvCompiler().CompileModule(new(
            File.ReadAllBytes(SpirvTestAssembly.Path), typeof(QualityShaders).GetMethod(name)!.MetadataToken,
            File.ReadAllBytes(typeof(Gpu).Assembly.Location)));
        return SpirvWriter.Write(module, target ?? SpirvCompilationTarget.Default);
    }
}

public sealed class UpstreamCompilerFactAttribute : FactAttribute
{
    public UpstreamCompilerFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("SIA_TEST_TINT")) || !File.Exists(Environment.GetEnvironmentVariable("SIA_TEST_NAGA")))
            Skip = "Set SIA_TEST_TINT and SIA_TEST_NAGA to native upstream compiler executables.";
    }
}
