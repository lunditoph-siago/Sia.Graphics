using Sia.Spirv.Compiler;
using Sia.Spirv.Compiler.LLVM;

namespace Sia.Spirv.Tests;

public sealed class LlvmIrEmitterTests
{
    private static readonly string _assemblyPath =
        typeof(LlvmIrEmitterTests).Assembly.Location;

    [Fact]
    public void EmitsVulkanBufferAndPushConstantAbiForSaxpy()
    {
        var frontend = new SpirvFrontend().Analyze(_assemblyPath);
        var kernel = Assert.Single(frontend.Kernels, static kernel => kernel.Name == "Saxpy");

        var module = new LlvmIrEmitter().Emit(_assemblyPath, kernel);

        Assert.Equal("Saxpy", module.EntryPoint);
        Assert.Contains("spirv.VulkanBuffer", module.Text, StringComparison.Ordinal);
        Assert.Contains("addrspace(13) @sia.push.constants", module.Text, StringComparison.Ordinal);
        Assert.Contains("@llvm.spv.thread.id.i32", module.Text, StringComparison.Ordinal);
        Assert.Contains("\"hlsl.numthreads\"=\"64,1,1\"", module.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsBranchesAndIntegerBufferAccessForControlFlow()
    {
        var frontend = new SpirvFrontend().Analyze(_assemblyPath);
        var kernel = Assert.Single(frontend.Kernels, static kernel => kernel.Name == "ControlFlow");

        var module = new LlvmIrEmitter().Emit(_assemblyPath, kernel);

        Assert.Contains("br i1", module.Text, StringComparison.Ordinal);
        Assert.Contains("tspirv.VulkanBuffer_a0i32_12_1t", module.Text, StringComparison.Ordinal);
        Assert.Contains(" and i32 ", module.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Saxpy")]
    [InlineData("ControlFlow")]
    public void ProducesValidatedSpirvWithLocalToolchainWhenAvailable(string kernelName)
    {
        LlvmToolchain toolchain;
        try {
            toolchain = LlvmToolchain.Locate();
        }
        catch (FileNotFoundException) {
            return;
        }

        var frontend = new SpirvFrontend().Analyze(_assemblyPath);
        var kernel = Assert.Single(frontend.Kernels, kernel => kernel.Name == kernelName);
        var module = new LlvmIrEmitter().Emit(_assemblyPath, kernel);
        var directory = Path.Combine(Path.GetTempPath(), $"sia-spirv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try {
            var rawPath = Path.Combine(directory, "kernel.raw.ll");
            var llvmPath = Path.Combine(directory, "kernel.ll");
            var spirvPath = Path.Combine(directory, "kernel.spv");
            File.WriteAllText(rawPath, module.Text);

            toolchain.Optimize(rawPath, llvmPath);
            toolchain.Compile(llvmPath, spirvPath, 2, "vulkan1.2");
            toolchain.Validate(spirvPath, "vulkan1.2");

            Assert.Equal(0x07230203u, BitConverter.ToUInt32(File.ReadAllBytes(spirvPath)));
        }
        finally {
            Directory.Delete(directory, true);
        }
    }
}
