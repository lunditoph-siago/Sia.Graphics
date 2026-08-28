using Sia.Spirv;

namespace Smoke.Modules;

internal static class SimulationKernels
{
    [SpirvKernel(64)]
    public static void Integrate(
        StorageBuffer<float> positions,
        StorageBuffer<float> velocities,
        float deltaTime,
        float damping,
        uint count)
    {
        var index = Gpu.GlobalInvocationId.X;
        if (index >= count) {
            return;
        }

        var velocity = velocities[index] * damping;
        var position = positions[index] + velocity * deltaTime;
        if (position > ShaderConstants.Maximum) {
            position = ShaderConstants.Maximum;
            velocity = 0.0f - velocity;
        }
        else if (position < ShaderConstants.Minimum) {
            position = ShaderConstants.Minimum;
            velocity = 0.0f - velocity;
        }

        positions[index] = position;
        velocities[index] = velocity;
    }
}
