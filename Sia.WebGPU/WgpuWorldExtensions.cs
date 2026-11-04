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

        return CreateWgpuEntity(
            world, handle, release, HList.From(new WgpuResource<T>(handle)));
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
        where T : unmanaged =>
        AttachWgpu(world, entity, ref handle, release);

    public static void AttachWgpu<T>(
        this World world,
        Entity entity,
        ref WgpuHandle<T> handle)
        where T : unmanaged =>
        AttachWgpu(world, entity, ref handle,
            static (ref WgpuHandle<T> owned) => Wgpu.Release(ref owned));

    public static void AttachWgpu<T>(
        this World world,
        Entity entity,
        ref WgpuHandle<T> handle,
        WgpuReleaseHandler<T> release)
        where T : unmanaged
    {
        ValidateWgpuTarget(world, entity);
        ArgumentNullException.ThrowIfNull(release);
        if (handle.IsNull) {
            throw new ArgumentException("A null WebGPU handle cannot be owned.", nameof(handle));
        }
        if (entity.Contains<WgpuResource<T>>()) {
            throw new InvalidOperationException(
                $"Entity {entity.Id} already contains {typeof(T).Name}.");
        }

        var owned = Wgpu.Move(ref handle);
        WgpuResources? module = null;
        try {
            module = world.AcquireAddon<WgpuResources>();
            entity.AddMany(HList.From(new WgpuResource<T>(owned)));
            module.Own(entity, owned, release);
        }
        catch {
            try {
                if (entity.IsValid && entity.Contains<WgpuResource<T>>()
                    && entity.GetWgpu<T>() == owned
                    && (module is null || !module.Owns<T>(entity))) {
                    entity.Remove<WgpuResource<T>>();
                }
            }
            finally {
                release(ref owned);
            }
            throw;
        }
    }

    internal static void ValidateWgpuTarget(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!entity.IsValid) {
            throw new ObjectDisposedException(nameof(entity));
        }
        if (!ReferenceEquals(entity.Host.World, world)) {
            throw new ArgumentException("The entity belongs to another world.", nameof(entity));
        }
    }

    private static Entity CreateWgpuEntity<T, TComponents>(
        World world,
        WgpuHandle<T> handle,
        WgpuReleaseHandler<T> release,
        in TComponents components)
        where T : unmanaged
        where TComponents : struct, IHList
    {
        var entity = default(Entity);
        try {
            var module = world.AcquireAddon<WgpuResources>();
            world.GetArrayHost<TComponents>().Create(components, out entity);
            module.Own(entity, handle, release);
            return entity;
        }
        catch {
            try {
                if (entity.IsValid) {
                    entity.Destroy();
                }
            }
            finally {
                release(ref handle);
            }
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

    public static Entity CreateWgpuInstance(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.OwnWgpu(Wgpu.CreateInstance());
    }

    public static Entity CreateWgpuInstance(
        this World world,
        in WGPUInstanceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.OwnWgpu(Wgpu.CreateInstance(in descriptor));
    }

    public static Entity CreateWgpuSurface(
        this World world,
        Entity instance,
        in WGPUSurfaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.OwnWgpu(Wgpu.CreateSurface(
            instance.GetWgpu<WGPUInstance>(), in descriptor));
    }

    public static Entity CreateWgpuBuffer(
        this World world,
        Entity device,
        in WGPUBufferDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(world);
        var handle = Wgpu.CreateBuffer(device.GetWgpu<WGPUDevice>(), in descriptor);
        var resource = new WgpuResource<WGPUBuffer>(handle);
        var info = new WgpuBufferInfo(descriptor.Size, descriptor.Usage);
        return CreateWgpuEntity(world, handle,
            static (ref WgpuHandle<WGPUBuffer> owned) => Wgpu.Release(ref owned),
            HList.From(resource, info));
    }

    public static Entity CreateWgpuTexture(
        this World world,
        Entity device,
        in WGPUTextureDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(world);
        var handle = Wgpu.CreateTexture(device.GetWgpu<WGPUDevice>(), in descriptor);
        var resource = new WgpuResource<WGPUTexture>(handle);
        var info = new WgpuTextureInfo(
            descriptor.Size,
            descriptor.Dimension,
            descriptor.Format,
            descriptor.Usage,
            descriptor.MipLevelCount,
            descriptor.SampleCount);
        return CreateWgpuEntity(world, handle,
            static (ref WgpuHandle<WGPUTexture> owned) => Wgpu.Release(ref owned),
            HList.From(resource, info));
    }

    public static Entity CreateWgpuTextureView(
        this World world,
        Entity texture,
        in WGPUTextureViewDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.OwnWgpu(Wgpu.CreateTextureView(
            texture.GetWgpu<WGPUTexture>(), in descriptor));
    }

    public static Entity CreateWgpuSampler(
        this World world,
        Entity device,
        in WGPUSamplerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.OwnWgpu(Wgpu.CreateSampler(
            device.GetWgpu<WGPUDevice>(), in descriptor));
    }

    public static Entity CreateWgpuShaderModule(
        this World world,
        Entity device,
        in WGPUShaderModuleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.OwnWgpu(Wgpu.CreateShaderModule(
            device.GetWgpu<WGPUDevice>(), in descriptor));
    }

    public static SystemChain AddWgpu(this SystemChain chain) =>
        chain
            .Add<WgpuProcessEventsSystem>()
            .Add<WgpuRequestSystem>();
}
