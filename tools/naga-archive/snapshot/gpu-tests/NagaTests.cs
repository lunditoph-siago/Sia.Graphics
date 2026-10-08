using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Sia.Spirv.Naga;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static class NagaTests
{
    private const int Count = 257, Fields = 15;
    [ModuleInitializer]
    internal static void Initialize() => TestModules.Register("naga", registry =>
    {
        foreach (string family in new[] { "integer", "half" })
            foreach (string variant in new[] { "source", "managed-wgsl", "managed-roundtrip", "reference-roundtrip", "managed-spirv", "reference-spirv" })
                registry.Add(family + "/" + variant, context => RunAsync(context, family, variant),
                    TestMode.Gpu, requiresGpu: true, configureDevice: configuration =>
                    {
                        if (family == "half") configuration.Features.Add(WGPUFeatureName.ShaderF16);
                    });
    });

    private static async Task RunAsync(TestContext context, string family, string variant)
    {
        var words = new uint[Count * 4];
        uint[] edges = [0, 1, 2, 3, 7, 15, 16, 31, 32, 33, 63, 64, 0x7fffffff, 0x80000000, 0xffffffff, 0x55555555, 0xaaaaaaaa];
        uint random = 0x12345678;
        for (int i = 0; i < Count; i++)
        {
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            words[i * 4] = i < edges.Length ? edges[i] : random;
            words[i * 4 + 1] = i < 2 ? 0 : i % 3 == 0 ? uint.MaxValue : edges[(i * 7) % edges.Length];
            words[i * 4 + 2] = edges[(i * 3) % edges.Length];
            words[i * 4 + 3] = edges[(i * 5) % edges.Length];
        }
        if (family == "half")
        {
            uint[] packed = [0, 0x80000000, 0x00008000, 0x3c003c00, 0xbc003c00, 0x7bfffbff, 0x04000400, 0x3555b555];
            for (int i = 0; i < Count; i++) words[i * 4] = packed[i % packed.Length];
        }
        words[13 * 4 + 1] = uint.MaxValue; // Explicit signed minimum / -1 overflow boundary.
        if (family == "half" && variant.StartsWith("reference-", StringComparison.Ordinal)) TestContext.Block("Reference Naga WGSL frontend loses the packed f16 bitcast target shape; reference generation failed before GPU execution.");
        string source = ReadShader(family + ".wgsl");
        byte[]? spirv = variant == "managed-spirv" ? ShaderTranslator.WgslToSpirv(source) : variant == "reference-spirv" ? ReadBytes(family + "-reference.spv") : null;
        string shader = variant switch
        {
            "source" => source,
            "managed-wgsl" => Sia.Spirv.Naga.Back.WgslWriter.Write(Sia.Spirv.Naga.Front.WgslReader.Parse(source)),
            "managed-roundtrip" => ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source)),
            "reference-roundtrip" => ReadShader(family + "-reference-roundtrip.wgsl"),
            "managed-spirv" or "reference-spirv" => source,
            _ => throw new ArgumentException(variant)
        };
        context.RecordShader("executed", shader);
        if (spirv is not null) context.Capture.Resources.Add(new { Name = "executed-spirv", Raw = Convert.ToBase64String(spirv) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var input = gpu.Upload<uint>(words, WGPUBufferUsage.Storage);
        uint[] initial = Enumerable.Repeat(0xdeadbeefu, Count * Fields + 4).ToArray();
        var output = gpu.Upload<uint>(initial, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        context.Capture.Resources.Add(new { Name = "inputs", Words = words, Fields, Count, family, variant });
        Dispatch(context, gpu, input, output, shader, spirv);
        uint[] actual = await ReadAsync(context, gpu, output.GetWgpu<WGPUBuffer>(), initial.Length);
        uint[] expected = initial.ToArray();
        for (int i = 0; i < Count; i++)
        {
            uint v = words[4 * i], arg = words[4 * i + 1], offset = Math.Min(32, words[4 * i + 2]);
            int count = (int)Math.Min(32 - offset, words[4 * i + 3]);
            uint mask = count == 32 ? uint.MaxValue : (1u << count) - 1;
            uint extracted = count == 0 ? 0 : v >> (int)offset & mask;
            uint inserted = count == 0 ? v : v & ~(mask << (int)offset) | (arg & mask) << (int)offset;
            int signedExtracted = count == 0 ? 0 : count == 32 ? unchecked((int)v) : (int)(extracted << (32 - count)) >> (32 - count);
            int signed = unchecked((int)v), divisor = unchecked((int)arg);
            if (divisor == 0 || signed == int.MinValue && divisor == -1) divisor = 1;
            uint[] result;
            if (family == "integer")
            {
                uint reverse = 0; for (int bit = 0; bit < 32; bit++) reverse |= (v >> bit & 1) << (31 - bit);
                uint signedLeadingBits = signed < 0 ? ~v : v;
                result = [(uint)BitOperations.LeadingZeroCount(v), (uint)BitOperations.TrailingZeroCount(v),
                    v == 0 ? uint.MaxValue : (uint)BitOperations.Log2(v), v == 0 ? uint.MaxValue : (uint)BitOperations.TrailingZeroCount(v),
                    (uint)BitOperations.PopCount(v), reverse, extracted, inserted, unchecked((uint)signedExtracted),
                    unchecked((uint)(signed / divisor)), unchecked((uint)(signed % divisor)), v / (arg == 0 ? 1u : arg), v % (arg == 0 ? 1u : arg),
                    signedLeadingBits == 0 ? uint.MaxValue : (uint)BitOperations.Log2(signedLeadingBits), arg];
            }
            else result = [v, BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf((ushort)v)),
                BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf((ushort)(v >> 16))), .. Enumerable.Repeat(0u, Fields - 3)];
            result.CopyTo(expected, i * Fields);
        }
        context.CompareWords("output", expected, actual);
    }

    private static string ReadShader(string name)
    {
        using var stream = typeof(NagaTests).Assembly.GetManifestResourceStream("tests/" + name) ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static byte[] ReadBytes(string name)
    {
        using var stream = typeof(NagaTests).Assembly.GetManifestResourceStream("tests/" + name) ?? throw new FileNotFoundException(name);
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }

    private static unsafe void Dispatch(TestContext context, GpuResources gpu, GpuResource input, GpuResource output, string source, byte[]? spirv)
    {
        var layout = GpuBinding.Layout(gpu, [GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute), GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)]);
        var group = GpuBinding.Group(gpu, layout, [GpuBinding.Buffer(0, input), GpuBinding.Buffer(1, output)]);
        var module = gpu.Own(spirv is null ? Wgpu.CreateWgslShaderModule(context.Gpu.Device, source, "managed-naga-equivalence") : Wgpu.CreateSpirvShaderModule(context.Gpu.Device, spirv, "managed-naga-equivalence"));
        if (DeviceSession.Errors.TryPeek(out var shaderError)) throw new InvalidOperationException(shaderError);
        var entry = Encoding.UTF8.GetBytes("main");
        fixed (byte* text = entry)
        {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)GpuBinding.PipelineLayout(gpu, layout).GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Compute.Module = (WGPUShaderModule*)module.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Compute.EntryPoint = new() { Data = text, Length = (nuint)entry.Length };
            var pipeline = gpu.Own(Wgpu.CreateComputePipeline(context.Gpu.Device, descriptor));
            if (DeviceSession.Errors.TryPeek(out var pipelineError)) throw new InvalidOperationException(pipelineError);
            var encoder = Wgpu.CreateCommandEncoder(context.Gpu.Device);
            var pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
            try
            {
                Wgpu.SetComputePipeline(pass, pipeline.GetWgpu<WGPUComputePipeline>()); Wgpu.SetBindGroup(pass, 0, group.GetWgpu<WGPUBindGroup>());
                Wgpu.DispatchWorkgroups(pass, (Count + 63) / 64); Wgpu.EndComputePass(pass); Submit(context.Gpu, encoder);
                context.Capture.Commands.Add("dispatch(5,1,1); queue.submit");
            }
            finally { Wgpu.Release(ref pass); Wgpu.Release(ref encoder); }
        }
    }
    private static async Task<uint[]> ReadAsync(TestContext context, GpuResources gpu, WgpuHandle<WGPUBuffer> output, int words)
    {
        ulong size = (ulong)words * 4;
        var staging = gpu.Buffer(size, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst).GetWgpu<WGPUBuffer>();
        var encoder = Wgpu.CreateCommandEncoder(context.Gpu.Device);
        try { Wgpu.CopyBufferToBuffer(encoder, output, 0, staging, 0, size); Submit(context.Gpu, encoder); }
        finally { Wgpu.Release(ref encoder); }
        context.Capture.Commands.Add($"copyBufferToBuffer({size} bytes); mapRead");
        await context.Gpu.WaitAsync(Wgpu.MapBufferReadAsync(staging, 0, size));
        try { return Wgpu.GetMappedRangeReadOnly<uint>(staging, 0, words).ToArray(); }
        finally { Wgpu.UnmapBuffer(staging); }
    }
    private static void Submit(DeviceSession session, WgpuHandle<WGPUCommandEncoder> encoder)
    {
        var command = Wgpu.FinishCommandEncoder(encoder, WGPUCommandBufferDescriptor.Default);
        try { Wgpu.Submit(session.Queue, [command]); } finally { Wgpu.Release(ref command); }
    }
}
