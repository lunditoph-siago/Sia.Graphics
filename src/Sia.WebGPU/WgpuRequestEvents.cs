using Sia;

namespace Sia.WebGPU;

public static class WgpuRequestEvents
{
    /// <summary>The target entity now contains an adapter resource component.</summary>
    public readonly record struct AdapterReady : IEvent;

    /// <summary>The target entity now contains device and queue resource components.</summary>
    public readonly record struct DeviceReady : IEvent;

    public readonly record struct Failed(
        WgpuRequestKind Kind,
        string Status,
        string Message) : IEvent;
}
