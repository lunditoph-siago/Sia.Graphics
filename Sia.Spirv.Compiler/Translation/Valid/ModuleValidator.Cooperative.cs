using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Valid;

public static partial class ModuleValidator
{
    private sealed partial class Validator
    {
        private ShaderType Cooperative(Expression.Call call, ShaderType[] args)
        {
            Restrict(Compute);
            if (call.Function == "coopMultiplyAdd")
            {
                Require(args.Length == 3, "Cooperative multiply-add requires three matrices.", call.Span);
                Require(args[0] is ShaderType.CooperativeMatrix { Role: CooperativeRole.A }
                    && args[1] is ShaderType.CooperativeMatrix { Role: CooperativeRole.B }
                    && args[2] is ShaderType.CooperativeMatrix { Role: CooperativeRole.C }, "Cooperative multiply-add requires A, B and C roles in order.", call.Span);
                var a = (ShaderType.CooperativeMatrix)args[0]; var b = (ShaderType.CooperativeMatrix)args[1]; var c = (ShaderType.CooperativeMatrix)args[2];
                Require(a.Columns == b.Rows && a.Rows == c.Rows && b.Columns == c.Columns && a.Scope == b.Scope && b.Scope == c.Scope,
                    "Cooperative multiply-add dimensions or scopes differ.", call.Span);
                return c;
            }
            bool load = call.Function is "coopLoad" or "coopLoadT";
            Require(args.Length == (load ? 2 : 3), "Cooperative memory operation requires a pointer and stride.", call.Span);
            ShaderType matrix = load ? call.Type : args[0];
            Require(matrix is ShaderType.CooperativeMatrix, "Cooperative memory operation requires a cooperative matrix.", call.Span);
            int pointerIndex = load ? 0 : 1;
            Require(args[pointerIndex] is ShaderType.Pointer p && p.Base is ShaderType.Scalar or ShaderType.Vector
                && Numeric(p.Base) && Scalar(p.Base)!.Width is 2 or 4, "Cooperative memory pointer must address numeric scalar/vector data.", call.Span);
            var pointer = (ShaderType.Pointer)args[pointerIndex];
            Require((pointer.Access & (load ? StorageAccess.Read : StorageAccess.Write)) != 0, "Cooperative memory access is not permitted by the pointer.", call.Span);
            Require(args[^1] is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 4 }, "Cooperative stride must be a 32-bit integer scalar.", call.Span);
            return load ? matrix : new ShaderType.Void();
        }
    }
}
