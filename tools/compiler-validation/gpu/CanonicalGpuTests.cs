using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    private sealed record CanonicalCase(string Fixture, string Sample, uint[] Input, uint[] Expected);

    // Expectations are fixed independently of emitted code and production constant evaluation.
    private static IEnumerable<CanonicalCase> CanonicalCases()
    {
        yield return new("IntegerSignedRuntime", "negative", [0xfffffff9, 3], [0xfffffffe, 0xffffffff]);
        yield return new("IntegerSignedRuntime", "negative-divisor", [7, 0xfffffffd], [0xfffffffe, 1]);
        yield return new("IntegerSignedRuntime", "overflow", [0x80000000, 0xffffffff], [0x80000000, 0]);
        yield return new("IntegerSignedRuntime", "zero", [0xfffffff9, 0], [0xfffffff9, 0]);
        yield return new("IntegerUnsignedRuntime", "large", [0xffffffff, 2], [0x7fffffff, 1]);
        yield return new("IntegerUnsignedRuntime", "zero", [17, 0], [17, 0]);
        yield return new("IntegerOrderedRuntime", "negative", [0xfffffff9, 3], [0xfffffffe, 12]);
        yield return new("IntegerOrderedRuntime", "zero", [17, 0], [17, 12]);
        yield return new("IntegerOrderedRuntime", "overflow", [0x80000000, 0xffffffff], [0x80000000, 12]);
        yield return new("IntegerVectorRuntime", "negative", [0xfffffff9, 3], [0, 0]);
        yield return new("IntegerVectorRuntime", "negative-divisor", [7, 0xfffffffd], [0, 0xfffffffe]);
        yield return new("IntegerVectorRuntime", "zero", [17, 0], [0xffffffef, 0xffffffff]);
        yield return new("IntegerVectorRuntime", "overflow", [0x80000000, 0xffffffff], [0x7ffffffe, 0xffffffff]);
        foreach (var (scalar, size, scalarLeft) in new[] {
            ("i16", 1, false), ("u16", 1, false), ("i32", 1, false), ("u32", 1, false),
            ("i64", 1, false), ("u64", 1, false), ("i16", 2, false), ("u16", 3, false),
            ("i64", 4, false), ("u64", 2, false), ("i32", 2, true), ("u32", 4, true) })
            yield return new("IntegerWidth-" + scalar + "-v" + size + (scalarLeft ? "-scalar-left" : "-scalar-right"),
                "normal", [27, 4], [6, 3]);
        yield return new("NativeScalarPhiLoop", "zero", [0], [12, 0]);
        yield return new("NativeScalarPhiLoop", "one", [1], [21, 0]);
        yield return new("NativeScalarPhiLoop", "two", [2], [12, 0]);
        yield return new("NativeScalarPhiLoop", "five", [5], [21, 0]);
        yield return new("SwapLoop", "zero", [0], [12, 0]);
        yield return new("SwapLoop", "one", [1], [21, 0]);
        yield return new("SwapLoop", "two", [2], [12, 0]);
        yield return new("SwapLoop", "five", [5], [21, 0]);
        yield return new("EarlyExit", "return", [0], [10, 0]);
        yield return new("EarlyExit", "switch", [1], [20, 99]);
        yield return new("EarlyExit", "loop", [4], [6, 99]);
        yield return new("CapturedIndex", "capture", [1, 5], [6, 0]);
        yield return new("SignedShift", "negative", [0xfffffff8], [0xfffffffc, 0]);
        yield return new("ShortCircuitOr", "skip", [0], [7, 0]);
        yield return new("ShortCircuitAnd", "skip", [0], [9, 0]);
        yield return new("ScalarHelpers", "zero", [0], [14, 2]);
        yield return new("ScalarHelpers", "positive", [2], [6, 2]);
        yield return new("NestedLoops", "zero", [0], [0, 0]);
        yield return new("NestedLoops", "one", [1], [4, 0]);
        yield return new("NestedLoops", "two", [2], [28, 1]);
        yield return new("NestedLoops", "break-if", [10], [136, 3]);
        yield return new("ContinuingBreakIf", "previous", [0], [3, 0]);
        yield return new("BodyContinuingScope", "scope", [0], [3, 0]);
        yield return new("NumericVectorLoop", "zero", [0], [0x3fc00000, 0x41500000]);
        yield return new("NumericVectorLoop", "one", [1], [0x40600000, 0x41c80000]);
        yield return new("NumericVectorLoop", "two", [2], [0x3fc00000, 0x41500000]);
        yield return new("NumericVectorLoop", "five", [5], [0x40600000, 0x41c80000]);
        yield return new("MatrixAggregate", "original", [0], [0x41300000, 0x41800000]);
        yield return new("MatrixAggregate", "transpose", [1], [0x41000000, 0x41900000]);
        yield return new("OrderedSelect", "two-effects", [0], [0x40000000, 2]);
        yield return new("FloatBuiltin", "negative", [0xc0200000], [0x40200000, 0x40000000]);
        yield return new("FloatBuiltin", "upper", [0x40c00000], [0x40800000, 0x40000000]);
        yield return new("FloatBuiltin", "lower", [0], [0x3f800000, 0x40000000]);
        yield return new("FiniteArrayLoop", "zero", [0], [1, 2]);
        yield return new("FiniteArrayLoop", "one", [1], [3, 4]);
        yield return new("FiniteArrayLoop", "two", [2], [1, 2]);
        yield return new("AtomicSerial", "zero", [0], [0, 9]);
        yield return new("AtomicSerial", "ten", [10], [10, 9]);
        yield return new("AtomicIndexCapture", "three", [3], [308, 20]);
        yield return new("AtomicIndexCapture", "ten", [10], [1015, 20]);
        yield return new("WorkgroupReduction", "zero", [0], [10, 6]);
        yield return new("WorkgroupReduction", "three", [3], [10, 18]);
        yield return new("UniformWorkgroupLoad", "zero", [0], [0, 1]);
        yield return new("UniformWorkgroupLoad", "seven", [7], [7, 8]);
        yield return new("UniformHelper", "zero", [0], [5, 0]);
        yield return new("UniformHelper", "three", [3], [8, 12]);
        yield return new("UniformLoopOverwrite", "zero", [0], [2, 17]);
        yield return new("UniformLoopOverwrite", "three", [3], [5, 17]);
        yield return new("ReconvergentBranches", "zero", [0], [8, 1]);
        yield return new("ReconvergentBranches", "three", [3], [20, 4]);
        yield return new("PointerScalar", "zero", [0], [0, 3]);
        yield return new("PointerScalar", "five", [5], [5, 8]);
        yield return new("PointerNested", "zero", [0], [77, 7]);
        yield return new("PointerNested", "five", [5], [127, 7]);
        yield return new("PointerIndexCapture", "zero", [0], [920, 100]);
        yield return new("PointerIndexCapture", "five", [5], [1420, 100]);
        yield return new("PointerPrivate", "zero", [0], [7, 7]);
        yield return new("PointerPrivate", "five", [5], [12, 12]);
        yield return new("PointerDistinct", "zero", [0], [1, 710]);
        yield return new("PointerDistinct", "five", [5], [6, 715]);
        yield return new("PointerLoop", "zero", [0], [3, 3]);
        yield return new("PointerLoop", "five", [5], [8, 3]);
        // Native IR has two writable arguments to one slot. Its WGSL target
        // expands the call; there is no legal authored WGSL alias-call control.
        yield return new("NativePointerAlias", "zero", [0], [7, 1601]);
        yield return new("NativePointerAlias", "five", [5], [7, 1606]);
        // These fixtures start as canonical pointer block arguments, so no authored WGSL source variant exists.
        yield return new("CanonicalPointerMergeStorage", "zero", [0], [7, 10]);
        yield return new("CanonicalPointerMergeStorage", "five", [5], [5, 17]);
        yield return new("CanonicalPointerMergeFunction", "zero", [0], [7, 10]);
        yield return new("CanonicalPointerMergeFunction", "five", [5], [5, 17]);
        yield return new("CanonicalPointerMergeSwapLoop", "zero", [0], [2, 10]);
        yield return new("CanonicalPointerMergeSwapLoop", "one", [1], [2, 11]);
        yield return new("CanonicalPointerMergeSwapLoop", "two", [2], [3, 11]);
        yield return new("CanonicalPointerMergeSwapLoop", "five", [5], [4, 13]);
        // Pointer-return helpers start as typed IR, including early exits and repeated calls.
        yield return new("CanonicalPointerReturnBranch", "zero", [0], [8, 10]);
        yield return new("CanonicalPointerReturnBranch", "five", [5], [6, 19]);
        yield return new("CanonicalPointerReturnNestedLoop", "zero", [0], [9, 10]);
        yield return new("CanonicalPointerReturnNestedLoop", "five", [5], [7, 19]);
        yield return new("CanonicalPointerReturnEarlyLoop", "zero", [0], [9, 10]);
        yield return new("CanonicalPointerReturnEarlyLoop", "five", [5], [13, 10]);
        yield return new("CanonicalPointerReturnRepeated", "zero", [0], [17, 10]);
        yield return new("CanonicalPointerReturnRepeated", "five", [5], [21, 10]);
        yield return new("CanonicalPointerReturnLocal", "zero", [0], [16, 10]);
        yield return new("CanonicalPointerReturnLocal", "five", [5], [14, 19]);
        // Native inputs retain actual pointer returns. Run them as controls alongside
        // both legalized outputs; unsupported consumer behavior remains visible.
        yield return new("NativePointerReturnBranch", "zero", [0], [2, 34]);
        yield return new("NativePointerReturnBranch", "five", [5], [14, 26]);
        yield return new("NativePointerReturnNested", "zero", [0], [2, 34]);
        yield return new("NativePointerReturnNested", "five", [5], [14, 26]);
        yield return new("NativePointerReturnSelect", "zero", [0], [2, 34]);
        yield return new("NativePointerReturnSelect", "five", [5], [14, 26]);
        yield return new("NativePointerReturnRepeated", "zero", [0], [3, 48]);
        yield return new("NativePointerReturnRepeated", "five", [5], [22, 42]);
        yield return new("NativePointerReturnQualified", "zero", [0], [2, 34]);
        yield return new("NativePointerReturnQualified", "five", [5], [14, 26]);
        yield return new("NativePointerPhiBranch", "zero", [0], [2, 34]);
        yield return new("NativePointerPhiBranch", "five", [5], [14, 26]);
        yield return new("NativePointerPhiBranchRepeated", "zero", [0], [3, 48]);
        yield return new("NativePointerPhiBranchRepeated", "five", [5], [22, 42]);
        yield return new("NativePointerPhiLoopOdd", "zero", [0], [9, 16]);
        yield return new("NativePointerPhiLoopOdd", "five", [5], [7, 34]);
        yield return new("NativePointerPhiLoopEven", "zero", [0], [2, 34]);
        yield return new("NativePointerPhiLoopEven", "five", [5], [14, 26]);
        yield return new("NativePointerPhiLoopRepeated", "zero", [0], [17, 20]);
        yield return new("NativePointerPhiLoopRepeated", "five", [5], [8, 14]);
        yield return new("NativePointerPhiQualifiedBranch", "zero", [0], [2, 34]);
        yield return new("NativePointerPhiQualifiedBranch", "five", [5], [14, 26]);
        yield return new("NativePointerPhiQualifiedLoop", "zero", [0], [9, 16]);
        yield return new("NativePointerPhiQualifiedLoop", "five", [5], [7, 34]);
        // These inputs begin as canonical memory slots, before shared SSA promotion.
        yield return new("CanonicalSlotSnapshot", "zero", [0], [18, 23]);
        yield return new("CanonicalSlotBranch", "zero", [0], [18, 23]);
        yield return new("CanonicalSlotBranch", "five", [5], [11, 18]);
        yield return new("CanonicalSlotLoop", "zero", [0], [11, 23]);
        yield return new("CanonicalSlotLoop", "one", [1], [25, 12]);
        yield return new("CanonicalSlotLoop", "two", [2], [14, 26]);
        yield return new("CanonicalSlotLoop", "five", [5], [31, 18]);
        yield return new("NativeSlotBranch", "zero", [0], [2, 34]);
        yield return new("NativeSlotBranch", "five", [5], [14, 26]);
        yield return new("NativeSlotLoopOdd", "zero", [0], [9, 16]);
        yield return new("NativeSlotLoopOdd", "five", [5], [7, 34]);
        yield return new("NativeSlotLoopEven", "zero", [0], [2, 34]);
        yield return new("NativeSlotLoopEven", "five", [5], [14, 26]);
        yield return new("NativeSlotRepeated", "zero", [0], [17, 20]);
        yield return new("NativeSlotRepeated", "five", [5], [8, 14]);
        yield return new("NativeSlotSelect", "zero", [0], [2, 34]);
        yield return new("NativeSlotSelect", "five", [5], [14, 26]);
        yield return new("NativeSlotStandalone", "zero", [0], [0, 34]);
        yield return new("NativeSlotStandalone", "five", [5], [12, 24]);
        yield return new("NativeSlotQualifiedMemory", "zero", [0], [2, 34]);
        yield return new("NativeSlotQualifiedMemory", "five", [5], [14, 26]);
        yield return new("NativeSlotPrivateMemoryHelpers", "zero", [0], [3, 48]);
        yield return new("NativeSlotPrivateMemoryHelpers", "five", [5], [22, 42]);
        foreach (string mode in new[] { "select", "phi", "loop", "slot", "helper" }) {
            yield return new("NativePointerComparison-" + mode, "zero", [0], mode == "loop" ? [2, uint.MaxValue] : [1, 0]);
            yield return new("NativePointerComparison-" + mode, "five", [5], mode == "loop" ? [1, 0] : [2, uint.MaxValue]);
        }
        yield return new("NativeSlotHelper", "zero", [0], [2, 34]);
        yield return new("NativeSlotHelper", "five", [5], [14, 26]);
        yield return new("NativeSlotHelperNested", "zero", [0], [2, 34]);
        yield return new("NativeSlotHelperNested", "five", [5], [14, 26]);
        yield return new("NativeSlotHelperRepeated", "zero", [0], [3, 48]);
        yield return new("NativeSlotHelperRepeated", "five", [5], [22, 42]);
        yield return new("NativeSlotHelperNestedRepeated", "zero", [0], [3, 48]);
        yield return new("NativeSlotHelperNestedRepeated", "five", [5], [22, 42]);
        yield return new("NativeUnreachableBranch", "five", [5], [14, 26]);
        yield return new("NativeUnreachableBranch", "seven", [7], [16, 30]);
        yield return new("NativeUnreachableNested", "five", [5], [14, 26]);
        yield return new("NativeUnreachableNested", "seven", [7], [16, 30]);
        yield return new("NativeUnreachableRepeated", "five", [5], [22, 42]);
        yield return new("NativeUnreachableRepeated", "seven", [7], [24, 46]);
        yield return new("NativeUnreachableScalar", "zero", [0], [9, 99]);
        yield return new("NativeUnreachableScalar", "two", [2], [9, 99]);
        yield return new("NativeUnreachableContinuing", "zero", [0], [7, 13]);
        yield return new("NativeUnreachableContinuing", "two", [2], [9, 13]);
    }

    [ModuleInitializer]
    internal static void InitializeCanonical() => TestModules.Register("canonical", registry => {
        foreach (var sample in CanonicalCases())
            foreach (string variant in sample.Fixture.StartsWith("NativePointerReturn", StringComparison.Ordinal)
                || sample.Fixture.StartsWith("NativePointerPhi", StringComparison.Ordinal)
                || sample.Fixture.StartsWith("NativeSlot", StringComparison.Ordinal)
                || sample.Fixture.StartsWith("NativeUnreachable", StringComparison.Ordinal)
                || sample.Fixture == "NativeScalarPhiLoop"
                ? new[] { "source-spirv", "canonical-wgsl", "canonical-spirv" }
                : sample.Fixture == "NativePointerAlias" || sample.Fixture.StartsWith("CanonicalPointerMerge", StringComparison.Ordinal)
                || sample.Fixture.StartsWith("CanonicalPointerReturn", StringComparison.Ordinal)
                || sample.Fixture.StartsWith("CanonicalSlot", StringComparison.Ordinal)
                ? new[] { "canonical-wgsl", "canonical-spirv" }
                : new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv" })
                registry.Add(sample.Fixture + "/" + sample.Sample + "/" + variant,
                    context => RunCanonical(context, sample, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunCanonical(TestContext context, CanonicalCase sample, string variant)
    {
        byte[] text = Asset(sample.Fixture + (variant == "source-wgsl" ? ".source.wgsl" : ".wgsl"));
        string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant is "canonical-spirv" or "source-spirv" ? Asset(sample.Fixture + (variant == "source-spirv" ? ".input.spv" : ".spv")) : null;
        context.RecordShader(variant == "source-spirv" ? "wgsl-sidecar" : "executed", source);
        context.Capture.Resources.Add(new {
            sample.Fixture, sample.Sample, Variant = variant,
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)),
            SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)),
            Input = sample.Input, Expected = sample.Expected
        });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        List<GpuResource> buffers = [
            gpu.Upload<uint>(sample.Input, WGPUBufferUsage.Storage),
            gpu.Upload<uint>(new uint[2], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc)
        ];
        List<WGPUBindGroupLayoutEntry> entries = [
            GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)
        ];
        context.Progress("Creating pipeline and dispatching " + variant);
        Dispatch(context, gpu, buffers, entries, source, spirv, "main");
        uint[] actual = await Read(context, gpu, buffers[1], sample.Expected.Length);
        context.CompareWords("output", sample.Expected, actual);
    }
}
