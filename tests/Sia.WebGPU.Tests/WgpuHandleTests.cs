using System.Runtime.CompilerServices;

using Sia;

namespace Sia.WebGPU.Tests;

public sealed class WgpuHandleTests
{
    [Fact]
    public void HandleAndResourceAreUnmanagedEcsValues()
    {
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<WgpuHandle<WGPUBuffer>>());
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<WgpuResource<WGPUBuffer>>());
        Assert.True(default(WgpuHandle<WGPUBuffer>).IsNull);
    }

    [Fact]
    public void MoveClearsCanonicalSourceWithoutChangingCapability()
    {
        var source = new WgpuHandle<WGPUBuffer>(42);

        var destination = Wgpu.Move(ref source);

        Assert.True(source.IsNull);
        Assert.Equal((nint)42, destination.Handle);
    }

    [Fact]
    public void SurfaceTextureIsAPlainFrameSnapshot()
    {
        var texture = new WgpuHandle<WGPUTexture>(42);
        var frame = new WgpuSurfaceTexture(
            WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal,
            texture);

        Assert.True(frame.HasTexture);
        Assert.Equal(texture, frame.Texture);
        Assert.False(default(WgpuSurfaceTexture).HasTexture);
    }

    [Fact]
    public void RemovingResourceComponentReleasesExactlyOnce()
    {
        using var world = new World();
        var releaseCount = 0;
        var entity = world.OwnWgpu(
            new WgpuHandle<WGPUBuffer>(42),
            (ref WgpuHandle<WGPUBuffer> handle) => {
                Assert.Equal((nint)42, handle.Handle);
                releaseCount++;
                handle = default;
            });

        var removed = entity.ReleaseWgpu<WGPUBuffer>();
        var removedAgain = entity.ReleaseWgpu<WGPUBuffer>();

        Assert.True(removed);
        Assert.False(removedAgain);
        Assert.True(entity.IsValid);
        Assert.Equal(1, releaseCount);
        Assert.Equal(0, world.GetAddon<WgpuResources>().Count);
    }

    [Fact]
    public void DestroyingResourceEntityReleasesExactlyOnce()
    {
        using var world = new World();
        var releaseCount = 0;
        var entity = world.OwnWgpu(
            new WgpuHandle<WGPUTexture>(42),
            (ref WgpuHandle<WGPUTexture> handle) => {
                releaseCount++;
                handle = default;
            });

        entity.Destroy();

        Assert.False(entity.IsValid);
        Assert.Equal(1, releaseCount);
        Assert.Equal(0, world.GetAddon<WgpuResources>().Count);
    }

    [Fact]
    public void EntityCanOwnSeveralTypedResourcesReleasedInReverseOrder()
    {
        using var world = new World();
        var releases = new List<string>();
        var entity = world.OwnWgpu(
            new WgpuHandle<WGPUInstance>(1),
            (ref WgpuHandle<WGPUInstance> handle) => {
                releases.Add("instance");
                handle = default;
            });
        world.AttachWgpu(
            entity,
            new WgpuHandle<WGPUAdapter>(2),
            (ref WgpuHandle<WGPUAdapter> handle) => {
                releases.Add("adapter");
                handle = default;
            });

        Assert.Equal((nint)1, entity.GetWgpu<WGPUInstance>().Handle);
        Assert.Equal((nint)2, entity.GetWgpu<WGPUAdapter>().Handle);
        Assert.Equal(2, world.GetAddon<WgpuResources>().Count);

        entity.Destroy();

        Assert.Equal(["adapter", "instance"], releases);
    }

    [Fact]
    public void DisposingWorldReleasesResourcesWhoseHostsWereCleared()
    {
        var world = new World();
        var releaseCount = 0;
        world.OwnWgpu(
            new WgpuHandle<WGPUInstance>(42),
            (ref WgpuHandle<WGPUInstance> handle) => {
                releaseCount++;
                handle = default;
            });

        world.Dispose();
        world.Dispose();

        Assert.Equal(1, releaseCount);
    }
}
