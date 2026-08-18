using Sia;

namespace Sia.Graphics.Scene;

public readonly record struct GpuFrame(World World, Entity Device, Entity Queue);
