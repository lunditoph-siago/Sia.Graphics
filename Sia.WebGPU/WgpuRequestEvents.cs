using Sia;

namespace Sia.WebGPU;

public static class WgpuRequestEvents
{
    public readonly record struct AdapterReady : IEvent;

    public readonly record struct DeviceReady : IEvent;

    public readonly record struct Failed(
        WgpuRequestKind Kind,
        string Status,
        string Message) : IEvent;
}
