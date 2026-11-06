using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static partial class SpirvIntegerArithmeticLowering
{
    private static ShaderType.Scalar? IntegerComponent(ShaderType type) => type switch {
        ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint } scalar => scalar,
        ShaderType.Vector { Component.Kind: ScalarKind.Sint or ScalarKind.Uint } vector => vector.Component,
        _ => null
    };

    private static bool SameIntegerWidth(ShaderType target, ShaderType source)
        => IntegerComponent(target) is { } t && IntegerComponent(source) is { } s && t.Width == s.Width;

    // OR with any known nonzero value stays nonzero. Do not infer this for AND,
    // arithmetic (which may wrap), narrowing conversions, or mutable memory.
    private static bool Nonzero(Expression value)
    {
        if (IntegerComponent(value.Type) is null) return false;
        if (ConstantEvaluator.TryEvaluateRuntime(value, out var constant) && IntegerLanes(constant) is { Length: > 0 } lanes)
            return lanes.All(n => n != 0);
        return value switch {
            Expression.Binary { Operator: "|" } binary => Nonzero(binary.Left) || Nonzero(binary.Right),
            Expression.Construct { Components.Count: > 0 } construct => construct.Components.All(Nonzero),
            Expression.Convert convert when SameIntegerWidth(convert.Type, convert.Operand.Type) => Nonzero(convert.Operand),
            Expression.Select select => Nonzero(select.Accept) && Nonzero(select.Reject),
            Expression.Call { Binding: CallBinding.Builtin, Function: "select", Arguments.Count: 3 } select
                => Nonzero(select.Arguments[0]) && Nonzero(select.Arguments[1]),
            _ => false
        };
    }

    private sealed partial class IntegerFacts
    {
        private readonly Dictionary<int, bool> nonzero = [];

        internal bool Nonzero(SsaValue value)
        {
            if (nonzero.TryGetValue(value.Id, out bool known)) return known;
            nonzero.Add(value.Id, false);
            if (IntegerComponent(value.Type) is null) return false;
            if (IntegerLanes(Constant(value)) is { Length: > 0 } lanes)
                return nonzero[value.Id] = lanes.All(n => n != 0);
            return nonzero[value.Id] = definitions.GetValueOrDefault(value.Id) switch {
                ValueOperation.Let alias => Nonzero(alias.Value),
                ValueOperation.Binary { Operator: "|" } binary => Nonzero(binary.Left) || Nonzero(binary.Right),
                ValueOperation.Construct { Components.Count: > 0 } construct => construct.Components.All(Nonzero),
                ValueOperation.Convert convert when SameIntegerWidth(value.Type, convert.Operand.Type) => Nonzero(convert.Operand),
                ValueOperation.Select select => Nonzero(select.Accept) && Nonzero(select.Reject),
                ValueOperation.Builtin { Function: "select", Arguments.Count: 3 } select
                    => Nonzero(select.Arguments[0]) && Nonzero(select.Arguments[1]),
                _ => false
            };
        }
    }
}
