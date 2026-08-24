using Sia.Spirv;

namespace Sia.Spirv.Sample;

internal static class Program
{
    public static void Main()
    {
        Console.WriteLine("Build this project to generate the SPIR-V artifacts under bin/<configuration>/net11.0/spirv.");
    }

    [SpirvKernel(64)]
    private static void Saxpy(
        StorageBuffer<float> x,
        StorageBuffer<float> y,
        StorageBuffer<float> output,
        float a)
    {
        var index = Gpu.GlobalInvocationId.X;
        output[index] = (a * x[index]) + y[index];
    }
}
