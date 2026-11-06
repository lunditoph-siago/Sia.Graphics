using System.Diagnostics;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.LLVM;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvNativeBuiltinTests
{
    [Fact]
    public void LdexpIsAdmittedByTheCanonicalPipeline()
    {
        var source = """
            @group(0) @binding(0) var<storage, read_write> data: array<f32>;
            @compute @workgroup_size(1) fn main() { data[1] = ldexp(data[0], -1); }
            """;
        var binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        string result = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        Assert.Contains("ldexp(", result);
        WgslReader.Parse(result);
    }

    [SpirvToolchainFact]
    public void NativeBuiltinOverloadsRetainTheirResultTypesAndValidate()
    {
        var cases = new[] {
            ("int", "OpBitCount %int %u", "countOneBits(", new double[] { 32 }),
            ("uint", "OpBitCount %uint %i", "countOneBits(", new double[] { 32 }),
            ("v2int", "OpBitCount %v2int %vu", "countOneBits(", new double[] { 32, 1 }),
            ("v2uint", "OpBitCount %v2uint %vi", "countOneBits(", new double[] { 32, 1 }),
            ("int", "OpBitReverse %int %i", "reverseBits(", new double[] { -1 }),
            ("uint", "OpExtInst %uint %glsl SAbs %u", "abs(i32(", new double[] { 1 }),
            ("uint", "OpExtInst %uint %glsl SSign %u", "sign(i32(", new double[] { uint.MaxValue }),
            ("uint", "OpExtInst %uint %glsl SClamp %u %ulo %ihi", "clamp(i32(", new double[] { 1 }),
            ("int", "OpExtInst %int %glsl UClamp %i %ilo %uhi", "clamp(u32(", new double[] { 3 }),
            ("v2uint", "OpExtInst %v2uint %glsl SClamp %vu %vi %vi", "clamp(vec2<i32>(", new double[] { uint.MaxValue, 1 }),
            ("v2int", "OpExtInst %v2int %glsl UClamp %vi %vu %vu", "clamp(vec2<u32>(", new double[] { -1, 1 }),
            ("uint", "OpExtInst %uint %glsl SMin %u %ulo", "min(i32(", new double[] { uint.MaxValue }),
            ("int", "OpExtInst %int %glsl UMin %i %ulo", "min(u32(", new double[] { 1 }),
            ("uint", "OpExtInst %uint %glsl SMax %u %ulo", "max(i32(", new double[] { 1 }),
            ("int", "OpExtInst %int %glsl UMax %i %ulo", "max(u32(", new double[] { -1 }),
            ("uint", "OpExtInst %uint %glsl FindSMsb %u", "firstLeadingBit(i32(", new double[] { uint.MaxValue }),
            ("int", "OpExtInst %int %glsl FindUMsb %i", "firstLeadingBit(u32(", new double[] { 31 }),
            ("int", "OpExtInst %int %glsl FindILsb %u", "firstTrailingBit(", new double[] { 0 }),
            ("float", "OpExtInst %float %glsl Ldexp %f %u", "ldexp(", new double[] { 1 }),
            ("v2float", "OpExtInst %v2float %glsl Ldexp %vf %vu", "ldexp(", new double[] { 1, 4 }),
            ("float", "OpExtInst %float %glsl FaceForward %f %negative %f", "select(", new double[] { 2 }),
            ("float", "OpExtInst %float %glsl FaceForward %f %f %f", "select(", new double[] { -2 }),
            ("float", "OpExtInst %float %glsl Reflect %f %negative", " * ", new double[] { -2 }),
            ("float", "OpExtInst %float %glsl Refract %negative %negative %half", "refract(", new double[] { 1 })
        };
        string directory = Path.Combine(Path.GetTempPath(), "sia-spirv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var toolchain = new LlvmToolchain(SpirvTestToolchain.Directory!);
            foreach (var (type, operation, expectedText, expectedValues) in cases) {
                string assembly = Path.Combine(directory, "source.spvasm"), binary = Path.Combine(directory, "source.spv");
                File.WriteAllText(assembly, Source(type, operation));
                Assemble(assembly, binary); toolchain.Validate(binary, "vulkan1.2");
                string wgsl = ShaderTranslator.SpirvToWgsl(File.ReadAllBytes(binary), SpirvCompilationTarget.Default);
                Assert.True(wgsl.Contains(expectedText, StringComparison.Ordinal), operation + "\n" + wgsl);
                var parsed = WgslReader.Parse(wgsl);
                var output = parsed.Globals.Single(g => g.Binding?.Binding == 0);
                ShaderType expected = type switch {
                    "uint" => ShaderType.U32, "int" => ShaderType.I32, "float" => ShaderType.F32,
                    "v2uint" => new ShaderType.Vector(2, ShaderType.U32), "v2int" => new ShaderType.Vector(2, ShaderType.I32),
                    _ => new ShaderType.Vector(2, ShaderType.F32)
                };
                Assert.Equal(expected, ((ShaderType.Structure)output.Type).Members[0].Type);
                Assert.Equal(expectedValues, EvaluateFixture(parsed));
                string roundtrip = Path.Combine(directory, "roundtrip.spv");
                File.WriteAllBytes(roundtrip, ShaderTranslator.WgslToSpirv(wgsl, SpirvCompilationTarget.Default));
                toolchain.Validate(roundtrip, "vulkan1.2");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // Evaluate the emitted straight-line fixture with its known private inputs.
    // Expected numbers above are independent of the reader's opcode mapping.
    // ConstantEvaluator supplies scalar/vector arithmetic; refract is evaluated
    // separately because it is not implemented by the compiler's constant folder.
    private static double[] EvaluateFixture(Module module)
    {
        var values = module.Globals.Where(g => g.Initializer is not null).ToDictionary(g => g.Name, g => g.Initializer!);
        Expression? result = null;
        Expression E(Expression expression) {
            var mapped = expression switch {
                Expression.Reference r => values[r.Name], Expression.Load l => E(l.Pointer),
                Expression.Unary u => u with { Operand = E(u.Operand) },
                Expression.Binary b => b with { Left = E(b.Left), Right = E(b.Right) },
                Expression.Convert c => c with { Operand = E(c.Operand) },
                Expression.Construct c => c with { Components = c.Components.Select(E).ToArray() },
                Expression.Select s => s with { Condition = E(s.Condition), Accept = E(s.Accept), Reject = E(s.Reject) },
                Expression.Access a => a with { Base = E(a.Base), Index = E(a.Index) },
                Expression.Swizzle s => s with { Vector = E(s.Vector) },
                Expression.Call c => c with { Arguments = c.Arguments.Select(E).ToArray() }, _ => expression
            };
            if (mapped is Expression.Call { Function: "refract" } refract) {
                double[] i = Numbers(refract.Arguments[0]), n = Numbers(refract.Arguments[1]);
                double eta = Numbers(refract.Arguments[2])[0], dot = i.Zip(n, (a, b) => a * b).Sum();
                double k = 1 - eta * eta * (1 - dot * dot);
                mapped = new Expression.Construct(refract.Type, i.Select((v, index) =>
                    (Expression)new Expression.Literal((float)(k < 0 ? 0 : eta * v - (eta * dot + System.Math.Sqrt(k)) * n[index]), ShaderType.F32)).ToArray());
            }
            return ConstantEvaluator.Evaluate(mapped);
        }
        void Run(Block block) {
            foreach (var statement in block.Statements) {
                switch (statement) {
                    case Statement.Declare d: values[d.Name] = d.Initializer is { } initializer ? E(initializer) : new Expression.Construct(d.Type, []); break;
                    case Statement.Store { Target: Expression.Reference reference } s: values[reference.Name] = E(s.Value); break;
                    case Statement.Store s: result = E(s.Value); break;
                    case Statement.Nested n: Run(n.Body); break;
                    case Statement.Evaluate { Value: Expression.Call { Binding: CallBinding.Function } call }: Run(module.Functions.Single(f => f.Name == call.Function).Body); break;
                    case Statement.Return: break;
                    default: throw new InvalidOperationException("Unexpected numeric fixture statement: " + statement);
                }
            }
        }
        Run(module.Functions.Single(f => f.Stage == ShaderStage.Compute).Body);
        return Numbers(result ?? throw new InvalidOperationException("Missing numeric fixture output."));
    }

    private static double[] Numbers(Expression expression) => expression switch {
        Expression.Literal literal => [System.Convert.ToDouble(literal.Value)],
        Expression.Construct construct => construct.Components.SelectMany(Numbers).ToArray(),
        _ => throw new InvalidOperationException("Unexpected numeric fixture value.")
    };

    private static void Assemble(string source, string destination)
    {
        var start = new ProcessStartInfo {
            FileName = Path.Combine(SpirvTestToolchain.Directory!, OperatingSystem.IsWindows() ? "spirv-as.exe" : "spirv-as"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "--target-env", "spv1.3", source, "-o", destination }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.True(process.ExitCode == 0, error);
    }

    private static string Source(string resultType, string operation) => $$"""
        OpCapability Shader
        %glsl = OpExtInstImport "GLSL.std.450"
        OpMemoryModel Logical GLSL450
        OpEntryPoint GLCompute %main "main"
        OpExecutionMode %main LocalSize 1 1 1
        OpDecorate %block Block
        OpMemberDecorate %block 0 Offset 0
        OpDecorate %output DescriptorSet 0
        OpDecorate %output Binding 0
        %void = OpTypeVoid
        %signature = OpTypeFunction %void
        %uint = OpTypeInt 32 0
        %int = OpTypeInt 32 1
        %float = OpTypeFloat 32
        %v2uint = OpTypeVector %uint 2
        %v2int = OpTypeVector %int 2
        %v2float = OpTypeVector %float 2
        %zero = OpConstant %uint 0
        %uvalue = OpConstant %uint 4294967295
        %ivalue = OpConstant %int -1
        %uone = OpConstant %uint 1
        %ione = OpConstant %int 1
        %uthree = OpConstant %uint 3
        %ithree = OpConstant %int 3
        %two = OpConstant %float 2
        %minus = OpConstant %float -1
        %pointfive = OpConstant %float 0.5
        %uvector = OpConstantComposite %v2uint %uvalue %uone
        %ivector = OpConstantComposite %v2int %ivalue %ione
        %fvector = OpConstantComposite %v2float %two %two
        %pu = OpTypePointer Private %uint
        %pi = OpTypePointer Private %int
        %pf = OpTypePointer Private %float
        %pvu = OpTypePointer Private %v2uint
        %pvi = OpTypePointer Private %v2int
        %pvf = OpTypePointer Private %v2float
        %uvar = OpVariable %pu Private %uvalue
        %ivar = OpVariable %pi Private %ivalue
        %fvar = OpVariable %pf Private %two
        %negvar = OpVariable %pf Private %minus
        %halfvar = OpVariable %pf Private %pointfive
        %ulovar = OpVariable %pu Private %uone
        %ilovar = OpVariable %pi Private %ione
        %uhivar = OpVariable %pu Private %uthree
        %ihivar = OpVariable %pi Private %ithree
        %vuvar = OpVariable %pvu Private %uvector
        %vivar = OpVariable %pvi Private %ivector
        %vfvar = OpVariable %pvf Private %fvector
        %block = OpTypeStruct %{{resultType}}
        %pblock = OpTypePointer StorageBuffer %block
        %presult = OpTypePointer StorageBuffer %{{resultType}}
        %output = OpVariable %pblock StorageBuffer
        %main = OpFunction %void None %signature
        %entry = OpLabel
        %u = OpLoad %uint %uvar
        %i = OpLoad %int %ivar
        %f = OpLoad %float %fvar
        %negative = OpLoad %float %negvar
        %half = OpLoad %float %halfvar
        %ulo = OpLoad %uint %ulovar
        %ilo = OpLoad %int %ilovar
        %uhi = OpLoad %uint %uhivar
        %ihi = OpLoad %int %ihivar
        %vu = OpLoad %v2uint %vuvar
        %vi = OpLoad %v2int %vivar
        %vf = OpLoad %v2float %vfvar
        %result = {{operation}}
        %pointer = OpAccessChain %presult %output %zero
        OpStore %pointer %result
        OpReturn
        OpFunctionEnd
        """;
}
