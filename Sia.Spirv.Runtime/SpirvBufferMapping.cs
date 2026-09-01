using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.Spirv.Runtime;

public sealed class SpirvBufferMapping<T>
    where T : unmanaged
{
    private readonly FieldMapping[] _fields;

    private SpirvBufferMapping(
        SpirvResourceBinding resource,
        int cpuStride,
        FieldMapping[] fields)
    {
        Resource = resource;
        CpuStride = cpuStride;
        _fields = fields;
    }

    public SpirvResourceBinding Resource { get; }

    public int CpuStride { get; }

    public int GpuStride => Resource.ArrayStride;

    public static SpirvBufferMapping<T> Create(
        SpirvArtifactManifest manifest,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var resource = manifest.Resources.SingleOrDefault(resource =>
            resource.Name == resourceName) ?? throw new ArgumentException(
                $"SPIR-V resource '{resourceName}' was not found.", nameof(resourceName));
        return Create(resource);
    }

    public static SpirvBufferMapping<T> Create(SpirvResourceBinding resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!BitConverter.IsLittleEndian) {
            throw new PlatformNotSupportedException(
                "SPIR-V buffer mapping currently requires a little-endian CPU.");
        }
        if (resource.Kind is not ("storage-buffer" or "uniform-buffer")) {
            throw new ArgumentException(
                $"Resource '{resource.Name}' is '{resource.Kind}', not a mappable buffer.",
                nameof(resource));
        }
        if (resource.Alignment <= 0 || resource.Size <= 0 || resource.ArrayStride <= 0 ||
            resource.ArrayStride < resource.Size ||
            resource.ArrayStride % resource.Alignment != 0) {
            throw new ArgumentException(
                $"Resource '{resource.Name}' has an invalid GPU layout.", nameof(resource));
        }

        var type = typeof(T);
        if (!type.IsLayoutSequential && !type.IsExplicitLayout) {
            throw new ArgumentException(
                $"CPU type '{type}' must use sequential or explicit layout.", nameof(resource));
        }
        var cpuStride = Unsafe.SizeOf<T>();
        var fields = resource.Fields is { Count: > 0 }
            ? CreateFieldMappings(type, cpuStride, resource)
            : CreateScalarMapping(cpuStride, resource);
        return new SpirvBufferMapping<T>(resource, cpuStride, fields);
    }

    public int GetByteLength(int elementCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        if (Resource.ElementCount is { } capacity) {
            if (elementCount > capacity) {
                throw new ArgumentOutOfRangeException(
                    nameof(elementCount),
                    $"Resource '{Resource.Name}' has capacity for {capacity} elements.");
            }
            return checked(capacity * GpuStride);
        }
        return checked(elementCount * GpuStride);
    }

    public byte[] Pack(ReadOnlySpan<T> source)
    {
        var result = new byte[GetByteLength(source.Length)];
        Pack(source, result);
        return result;
    }

    public void Pack(ReadOnlySpan<T> source, Span<byte> destination)
    {
        var requiredLength = GetByteLength(source.Length);
        if (destination.Length < requiredLength) {
            throw new ArgumentException(
                $"The GPU destination requires at least {requiredLength} bytes.",
                nameof(destination));
        }

        destination[..requiredLength].Clear();
        var sourceBytes = MemoryMarshal.AsBytes(source);
        for (var index = 0; index < source.Length; index++) {
            foreach (var field in _fields) {
                sourceBytes.Slice(
                        checked(index * CpuStride + field.CpuOffset),
                        field.Size)
                    .CopyTo(destination.Slice(
                        checked(index * GpuStride + field.GpuOffset),
                        field.Size));
            }
        }
    }

    public void Unpack(ReadOnlySpan<byte> source, Span<T> destination)
    {
        var requiredLength = GetByteLength(destination.Length);
        if (source.Length < requiredLength) {
            throw new ArgumentException(
                $"The GPU source requires at least {requiredLength} bytes.", nameof(source));
        }

        var destinationBytes = MemoryMarshal.AsBytes(destination);
        destinationBytes.Clear();
        for (var index = 0; index < destination.Length; index++) {
            foreach (var field in _fields) {
                source.Slice(
                        checked(index * GpuStride + field.GpuOffset),
                        field.Size)
                    .CopyTo(destinationBytes.Slice(
                        checked(index * CpuStride + field.CpuOffset),
                        field.Size));
            }
        }
    }

    private static FieldMapping[] CreateScalarMapping(
        int cpuStride,
        SpirvResourceBinding resource)
    {
        if (cpuStride < resource.Size) {
            throw new ArgumentException(
                $"CPU type '{typeof(T)}' is {cpuStride} bytes, but resource " +
                $"'{resource.Name}' requires {resource.Size} bytes.", nameof(resource));
        }
        return [new FieldMapping(0, 0, resource.Size)];
    }

    private static FieldMapping[] CreateFieldMappings(
        Type type,
        int cpuStride,
        SpirvResourceBinding resource)
    {
        var result = new FieldMapping[resource.Fields!.Count];
        for (var index = 0; index < resource.Fields.Count; index++) {
            var gpuField = resource.Fields[index];
            var cpuField = type.GetField(
                gpuField.Name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                throw new ArgumentException(
                    $"CPU type '{type}' does not declare field '{gpuField.Name}' required by " +
                    $"resource '{resource.Name}'.", nameof(resource));
            var cpuOffset = checked((int)Marshal.OffsetOf(type, cpuField.Name));
            if (cpuOffset < 0 || cpuOffset + gpuField.Size > cpuStride) {
                throw new ArgumentException(
                    $"CPU field '{type}.{cpuField.Name}' cannot provide the " +
                    $"{gpuField.Size}-byte GPU field.", nameof(resource));
            }
            if (gpuField.Offset < 0 || gpuField.Size <= 0 ||
                gpuField.Offset + gpuField.Size > resource.Size) {
                throw new ArgumentException(
                    $"GPU field '{resource.Name}.{gpuField.Name}' exceeds its declared layout.",
                    nameof(resource));
            }
            result[index] = new FieldMapping(cpuOffset, gpuField.Offset, gpuField.Size);
        }
        return result;
    }

    private readonly record struct FieldMapping(int CpuOffset, int GpuOffset, int Size);
}
