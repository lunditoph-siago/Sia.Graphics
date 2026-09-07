using System.Collections.Frozen;
using Sia.Graphics.Wgsl;
using Sia.WebGPU;

namespace Sia.Graphics.Compatibility;

public sealed class GpuDeviceCapabilitySnapshot
{
    public IReadOnlyDictionary<string, WgslValue> Values { get; }

    private GpuDeviceCapabilitySnapshot(Dictionary<string, WgslValue> values)
    {
        Values = values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public WgslCompilationContext CreateCompilationContext(
        IReadOnlyDictionary<string, WgslValue>? planValues = null) =>
        GpuCapabilityCatalog.Standard.CreateCompilationContext(Values, planValues);

    public static unsafe GpuDeviceCapabilitySnapshot Query(WgpuHandle<WGPUDevice> device)
    {
        var limits = Wgpu.GetLimits(device, out var stageLimits);
        var features = WGPUSupportedFeatures.Default;
        WgpuUnsafe.wgpuDeviceGetFeatures((WGPUDevice*)device.DangerousGetHandle(), &features);
        try {
            var enabled = new HashSet<WGPUFeatureName>();
            for (nuint index = 0; index < features.FeatureCount; index++) {
                enabled.Add(features.Features[index]);
            }
            return FromReportedValues(enabled, in limits, stageLimits);
        }
        finally {
            WgpuUnsafe.wgpuSupportedFeaturesFreeMembers(features);
        }
    }

    public static GpuDeviceCapabilitySnapshot FromReportedValues(
        IReadOnlySet<WGPUFeatureName> enabledFeatures, in WGPULimits limits,
        WGPUCompatibilityModeLimits? stageLimits = null)
    {
        ArgumentNullException.ThrowIfNull(enabledFeatures);
        var values = new Dictionary<string, WgslValue>(StringComparer.Ordinal);
        foreach (var definition in GpuCapabilityCatalog.Standard.Definitions.Values) {
            if (definition.ShaderSymbol is null) {
                continue;
            }
            var value = WgslValue.Unknown(definition.ValueKind);
            if (definition.Kind == GpuCapabilityKind.DeviceFeature &&
                definition.CMember is { } member &&
                Enum.TryParse<WGPUFeatureName>(member["WGPUFeatureName_".Length..], out var feature)) {
                value = feature == WGPUFeatureName.CoreFeaturesAndLimits && !enabledFeatures.Contains(feature)
                    ? WgslValue.Unknown(WgslValueKind.Boolean)
                    : WgslValue.Boolean(enabledFeatures.Contains(feature));
            }
            else if (definition.Kind == GpuCapabilityKind.Limit) {
                value = ReadLimit(definition.CMember, in limits, definition.ValueKind);
                if (!value.IsKnown && stageLimits is { } reported) {
                    var stageValue = definition.CMember switch {
                        "maxStorageBuffersInVertexStage" => reported.MaxStorageBuffersInVertexStage,
                        "maxStorageBuffersInFragmentStage" => reported.MaxStorageBuffersInFragmentStage,
                        "maxStorageTexturesInVertexStage" => reported.MaxStorageTexturesInVertexStage,
                        "maxStorageTexturesInFragmentStage" => reported.MaxStorageTexturesInFragmentStage,
                        _ => uint.MaxValue
                    };
                    if (stageValue != uint.MaxValue) {
                        value = WgslValue.UInt32(stageValue);
                    }
                }
            }
            values.Add(definition.Id, value);
        }
        return new GpuDeviceCapabilitySnapshot(values);
    }

    private static WgslValue ReadLimit(string? member, in WGPULimits limits, WgslValueKind kind)
    {
        var value = member switch {
            "maxTextureDimension1D" => WgslValue.UInt32(limits.MaxTextureDimension1D),
            "maxTextureDimension2D" => WgslValue.UInt32(limits.MaxTextureDimension2D),
            "maxTextureDimension3D" => WgslValue.UInt32(limits.MaxTextureDimension3D),
            "maxTextureArrayLayers" => WgslValue.UInt32(limits.MaxTextureArrayLayers),
            "maxBindGroups" => WgslValue.UInt32(limits.MaxBindGroups),
            "maxBindGroupsPlusVertexBuffers" => WgslValue.UInt32(limits.MaxBindGroupsPlusVertexBuffers),
            "maxBindingsPerBindGroup" => WgslValue.UInt32(limits.MaxBindingsPerBindGroup),
            "maxDynamicUniformBuffersPerPipelineLayout" => WgslValue.UInt32(limits.MaxDynamicUniformBuffersPerPipelineLayout),
            "maxDynamicStorageBuffersPerPipelineLayout" => WgslValue.UInt32(limits.MaxDynamicStorageBuffersPerPipelineLayout),
            "maxSampledTexturesPerShaderStage" => WgslValue.UInt32(limits.MaxSampledTexturesPerShaderStage),
            "maxSamplersPerShaderStage" => WgslValue.UInt32(limits.MaxSamplersPerShaderStage),
            "maxStorageBuffersPerShaderStage" => WgslValue.UInt32(limits.MaxStorageBuffersPerShaderStage),
            "maxStorageTexturesPerShaderStage" => WgslValue.UInt32(limits.MaxStorageTexturesPerShaderStage),
            "maxUniformBuffersPerShaderStage" => WgslValue.UInt32(limits.MaxUniformBuffersPerShaderStage),
            "maxUniformBufferBindingSize" => WgslValue.UInt64(limits.MaxUniformBufferBindingSize),
            "maxStorageBufferBindingSize" => WgslValue.UInt64(limits.MaxStorageBufferBindingSize),
            "minUniformBufferOffsetAlignment" => WgslValue.UInt32(limits.MinUniformBufferOffsetAlignment),
            "minStorageBufferOffsetAlignment" => WgslValue.UInt32(limits.MinStorageBufferOffsetAlignment),
            "maxVertexBuffers" => WgslValue.UInt32(limits.MaxVertexBuffers),
            "maxBufferSize" => WgslValue.UInt64(limits.MaxBufferSize),
            "maxVertexAttributes" => WgslValue.UInt32(limits.MaxVertexAttributes),
            "maxVertexBufferArrayStride" => WgslValue.UInt32(limits.MaxVertexBufferArrayStride),
            "maxInterStageShaderVariables" => WgslValue.UInt32(limits.MaxInterStageShaderVariables),
            "maxColorAttachments" => WgslValue.UInt32(limits.MaxColorAttachments),
            "maxColorAttachmentBytesPerSample" => WgslValue.UInt32(limits.MaxColorAttachmentBytesPerSample),
            "maxComputeWorkgroupStorageSize" => WgslValue.UInt32(limits.MaxComputeWorkgroupStorageSize),
            "maxComputeInvocationsPerWorkgroup" => WgslValue.UInt32(limits.MaxComputeInvocationsPerWorkgroup),
            "maxComputeWorkgroupSizeX" => WgslValue.UInt32(limits.MaxComputeWorkgroupSizeX),
            "maxComputeWorkgroupSizeY" => WgslValue.UInt32(limits.MaxComputeWorkgroupSizeY),
            "maxComputeWorkgroupSizeZ" => WgslValue.UInt32(limits.MaxComputeWorkgroupSizeZ),
            "maxComputeWorkgroupsPerDimension" => WgslValue.UInt32(limits.MaxComputeWorkgroupsPerDimension),
            "maxImmediateSize" => WgslValue.UInt32(limits.MaxImmediateSize),
            _ => WgslValue.Unknown(kind)
        };
        return value == WgslValue.UInt32(uint.MaxValue) || value == WgslValue.UInt64(ulong.MaxValue)
            ? WgslValue.Unknown(kind) : value;
    }
}
