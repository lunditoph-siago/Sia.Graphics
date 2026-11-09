using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class NativeBuiltinFixture
{
    // Evaluate the emitted straight-line fixture with its known private inputs.
    // Expected numbers above are independent of the reader's opcode mapping.
    // ConstantEvaluator supplies scalar/vector arithmetic; refract is evaluated
    // separately because it is not implemented by the compiler's constant folder.
    internal static double[] Evaluate(Module module)
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

    internal static string Source(string resultType, string operation) => $$"""
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
