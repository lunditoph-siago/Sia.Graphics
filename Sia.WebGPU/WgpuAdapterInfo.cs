namespace Sia.WebGPU;

public readonly record struct WgpuAdapterInfo(
    string Vendor,
    string Architecture,
    string Device,
    string Description,
    WGPUBackendType BackendType,
    WGPUAdapterType AdapterType,
    uint VendorId,
    uint DeviceId,
    uint SubgroupMinSize,
    uint SubgroupMaxSize);
