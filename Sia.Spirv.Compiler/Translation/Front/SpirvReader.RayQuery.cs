using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private void AddRayStructure(ShaderType.Structure type)
        { if (!module.Structures.Contains(type)) module.Structures.Add(type); }
        private Expression RayPointer(Expression expression)
        {
            if (expression.Type is not ShaderType.Pointer { Base: ShaderType.RayQuery, Space: AddressSpace.Function }) throw Error("Ray query instruction requires a function query pointer.");
            return new Expression.Unary("&", expression, expression.Type);
        }
        private Expression RayIntersectionValue(Op operation, Expression query, Expression intersection, ShaderType type)
        {
            if (!ConstantEvaluator.TryEvaluate(intersection, out var evaluated) || evaluated is not Expression.Literal selector
                || selector.Type != ShaderType.U32 || Convert.ToUInt32(selector.Value) > 1) throw Error("Ray intersection selector must be constant zero or one.");
            string rawName = "spirv" + operation;
            if (!RayQueryTypes.RawGetterResult(rawName, type)) throw Error("Ray query getter result type mismatch.");
            return new Expression.Call(rawName, [RayPointer(query), Expression.U32(Convert.ToUInt32(selector.Value))], type);
        }
    }
}
