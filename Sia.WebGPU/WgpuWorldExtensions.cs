using Sia;

namespace Sia.WebGPU;

public static class WgpuWorldExtensions
{
    public static Entity OwnWgpu<T>(
        this World world,
        WgpuHandle<T> handle)
        where T : unmanaged =>
        OwnWgpu(world, handle, static (ref WgpuHandle<T> owned) =>
            Wgpu.Release(ref owned));

    public static Entity OwnWgpu<T>(
        this World world,
        WgpuHandle<T> handle,
        WgpuReleaseHandler<T> release)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(release);
        if (handle.IsNull) {
            throw new ArgumentException("A null WebGPU handle cannot be owned.", nameof(handle));
        }

        var module = world.AcquireAddon<WgpuResources>();
        var entity = world.Create(HList.From(new WgpuResource<T>(handle)));

        try {
            module.Own(entity, handle, release);
            return entity;
        }
        catch {
            if (entity.IsValid) {
                entity.Destroy();
            }
            release(ref handle);
            throw;
        }
    }

    public static WgpuHandle<T> GetWgpu<T>(this Entity entity)
        where T : unmanaged =>
        entity.Get<WgpuResource<T>>().Handle;

    public static void AttachWgpu<T>(
        this World world,
        Entity entity,
        WgpuHandle<T> handle)
        where T : unmanaged =>
        AttachWgpu(
            world,
            entity,
            handle,
            static (ref WgpuHandle<T> owned) => Wgpu.Release(ref owned));

    public static void AttachWgpu<T>(
        this World world,
        Entity entity,
        WgpuHandle<T> handle,
        WgpuReleaseHandler<T> release)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(release);
        if (!entity.IsValid) {
            throw new ObjectDisposedException(nameof(entity));
        }
        if (entity.Contains<WgpuResource<T>>()) {
            throw new InvalidOperationException(
                $"Entity {entity.Id} already contains {typeof(T).Name}.");
        }

        var module = world.AcquireAddon<WgpuResources>();
        entity.AddMany(HList.From(new WgpuResource<T>(handle)));
        try {
            module.Own(entity, handle, release);
        }
        catch {
            if (entity.IsValid && entity.Contains<WgpuResource<T>>()) {
                entity.Remove<WgpuResource<T>>();
            }
            release(ref handle);
            throw;
        }
    }

    public static bool ReleaseWgpu<T>(this Entity entity)
        where T : unmanaged
    {
        if (!entity.IsValid || !entity.Contains<WgpuResource<T>>()) {
            return false;
        }

        entity.Remove<WgpuResource<T>>();
        return true;
    }

    public static Entity CreateWgpuInstance(this World world) =>
        world.OwnWgpu(Wgpu.CreateInstance());

    public static Entity CreateWgpuInstance(
        this World world,
        in WGPUInstanceDescriptor descriptor) =>
        world.OwnWgpu(Wgpu.CreateInstance(in descriptor));

    public static Entity CreateWgpuSurface(
        this World world,
        Entity instance,
        in WGPUSurfaceDescriptor descriptor) =>
        world.OwnWgpu(Wgpu.CreateSurface(
            instance.GetWgpu<WGPUInstance>(), in descriptor));

    public static Entity CreateWgpuBuffer(
        this World world,
        Entity device,
        in WGPUBufferDescriptor descriptor)
    {
        var handle = Wgpu.CreateBuffer(device.GetWgpu<WGPUDevice>(), in descriptor);
        var module = world.AcquireAddon<WgpuResources>();
        var resource = new WgpuResource<WGPUBuffer>(handle);
        var info = new WgpuBufferInfo(descriptor.Size, descriptor.Usage);
        var entity = world.Create(HList.From(resource, info));

        try {
            module.Own(
                entity,
                handle,
                static (ref WgpuHandle<WGPUBuffer> owned) => Wgpu.Release(ref owned));
            return entity;
        }
        catch {
            if (entity.IsValid) {
                entity.Destroy();
            }
            Wgpu.Release(ref handle);
            throw;
        }
    }

    public static Entity CreateWgpuTexture(
        this World world,
        Entity device,
        in WGPUTextureDescriptor descriptor)
    {
        var handle = Wgpu.CreateTexture(device.GetWgpu<WGPUDevice>(), in descriptor);
        var module = world.AcquireAddon<WgpuResources>();
        var resource = new WgpuResource<WGPUTexture>(handle);
        var info = new WgpuTextureInfo(
            descriptor.Size,
            descriptor.Dimension,
            descriptor.Format,
            descriptor.Usage,
            descriptor.MipLevelCount,
            descriptor.SampleCount);
        var entity = world.Create(HList.From(resource, info));

        try {
            module.Own(
                entity,
                handle,
                static (ref WgpuHandle<WGPUTexture> owned) => Wgpu.Release(ref owned));
            return entity;
        }
        catch {
            if (entity.IsValid) {
                entity.Destroy();
            }
            Wgpu.Release(ref handle);
            throw;
        }
    }

    public static Entity CreateWgpuTextureView(
        this World world,
        Entity texture,
        in WGPUTextureViewDescriptor descriptor) =>
        world.OwnWgpu(Wgpu.CreateTextureView(
            texture.GetWgpu<WGPUTexture>(), in descriptor));

    public static Entity CreateWgpuSampler(
        this World world,
        Entity device,
        in WGPUSamplerDescriptor descriptor) =>
        world.OwnWgpu(Wgpu.CreateSampler(
            device.GetWgpu<WGPUDevice>(), in descriptor));

    public static Entity CreateWgpuShaderModule(
        this World world,
        Entity device,
        in WGPUShaderModuleDescriptor descriptor) =>
        world.OwnWgpu(Wgpu.CreateShaderModule(
            device.GetWgpu<WGPUDevice>(), in descriptor));

    public static SystemChain AddWgpu(this SystemChain chain) =>
        chain
            .Add<WgpuProcessEventsSystem>()
            .Add<WgpuRequestSystem>();
}
