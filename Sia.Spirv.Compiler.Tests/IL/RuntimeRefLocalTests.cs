using Sia.Spirv;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;

namespace Sia.Spirv.Compiler.Tests.IL;

public sealed class RuntimeRefLocalTests
{
    [Theory]
    [InlineData(nameof(RefLocalShaders.CapturedAlias), 8u, 9u)]
    [InlineData(nameof(RefLocalShaders.Loop), 8u, 5u)]
    [InlineData(nameof(RefLocalShaders.FunctionLocal), 10u, 10u)]
    [InlineData(nameof(RefLocalShaders.AtomicStorage), 10u, 10u)]
    public void RefLocalsPreserveReadsWritesAndRebinding(string method, uint first, uint second)
    {
        string wgsl = ShaderTranslator.SpirvToWgsl(Compile(method), SpirvCompilationTarget.Default);
        Assert.Equal(new[] { first, second }, WgslFixtureInterpreter.Execute(wgsl).Memory);
    }

    [SpirvToolchainTheory]
    [Trait("Category", "Toolchain")]
    [InlineData(nameof(RefLocalShaders.Loop))]
    [InlineData(nameof(RefLocalShaders.AtomicStorage))]
    public void RefLocalsProduceValidSpirv(string method)
        => SpirvTestToolchain.Validate(Compile(method));

    [Fact]
    public void RefLocalCannotChangeAddressSpaces()
    {
        var error = Assert.Throws<InvalidDataException>(() => Compile(nameof(RefLocalShaders.MixedAddressSpaces)));
        Assert.Contains("same address space and access", error.Message);
    }

    internal static byte[] Compile(string methodName, SpirvCompilationTarget? target = null)
    {
        var template = SpirvTestAssembly.GetKernel(typeof(MemorySemanticsShaders), nameof(MemorySemanticsShaders.SwapLoop));
        var method = typeof(RefLocalShaders).GetMethod(methodName)!;
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        var graph = CilControlFlowGraph.Create(CilInstructionDecoder.Decode(bytes), bytes.Length);
        var kernel = template with { DeclaringType = typeof(RefLocalShaders).FullName!, Name = methodName,
            MetadataToken = method.MetadataToken, ControlFlowGraph = graph };
        var module = RuntimeShaderLowering.Run(File.ReadAllBytes(SpirvTestAssembly.Path),
            File.ReadAllBytes(typeof(Gpu).Assembly.Location), kernel,
            SpirvKernelAbiLayout.Create(kernel.Parameters, SpirvKernelAbi.WebGpu));
        return SpirvWriter.Write(module, target ?? SpirvCompilationTarget.Default);
    }
}
