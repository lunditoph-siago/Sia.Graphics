using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class WgslReader
{
    private sealed partial class Lowerer
    {
        private static readonly HashSet<string> UnaryMath = new("abs acos acosh asin asinh atan atanh ceil cos cosh countLeadingZeros countOneBits countTrailingZeros degrees exp exp2 firstLeadingBit firstTrailingBit floor fract inverseSqrt log log2 quantizeToF16 radians reverseBits round saturate sign sin sinh sqrt tan tanh trunc dpdx dpdy fwidth dpdxFine dpdyFine fwidthFine dpdxCoarse dpdyCoarse fwidthCoarse".Split(' '), StringComparer.Ordinal);
        private static readonly HashSet<string> BinaryMath = new("atan2 cross distance dot max min pow reflect step ldexp".Split(' '), StringComparer.Ordinal);
        private static readonly HashSet<string> TernaryMath = new("clamp faceForward fma mix refract smoothstep".Split(' '), StringComparer.Ordinal);
        private static readonly HashSet<string> FloatingMath = new("acos acosh asin asinh atan atanh atan2 ceil cos cosh cross degrees determinant distance dot exp exp2 faceForward floor fma fract inverseSqrt ldexp length log log2 mix modf frexp normalize pow quantizeToF16 radians reflect refract round saturate sign sin sinh smoothstep sqrt step tan tanh transpose trunc dpdx dpdy fwidth dpdxFine dpdyFine fwidthFine dpdxCoarse dpdyCoarse fwidthCoarse".Split(' '), StringComparer.Ordinal);
        private Expression Builtin(SExpression.Name name, Expression[] arguments, Block block)
        {
            string function = name.Text;
            if (name.Templates.Count != 0 && function is not ("coopLoad" or "coopLoadT")) throw Error("Builtin does not take template arguments.", name.Span);
            if (FloatingMath.Contains(function) && function != "sign")
                for (int i = 0; i < arguments.Length; i++)
                {
                    ShaderType promote = arguments[i].Type switch
                    {
                        ShaderType.Scalar { Kind: ScalarKind.AbstractInt } => WgslNumbers.AbstractFloat,
                        ShaderType.Vector { Component.Kind: ScalarKind.AbstractInt } v => new ShaderType.Vector(v.Size, WgslNumbers.AbstractFloat),
                        _ => arguments[i].Type
                    };
                    if (function != "ldexp" || i == 0) arguments[i] = Materialize(arguments[i], promote);
                }
            ShaderType result;
            bool snapshot = false;
            void Count(int minimum, int maximum)
            { if (arguments.Length < minimum || arguments.Length > maximum) throw Error($"Invalid argument count for '{function}'.", name.Span); }
            ShaderType.Scalar Scalar(ShaderType type) => type switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, ShaderType.Matrix m => m.Component, _ => throw Error("Builtin needs a numeric value.", name.Span) };
            void Common()
            {
                for (int i = 1; i < arguments.Length; i++)
                {
                    if (arguments[0].Type == arguments[i].Type) continue;
                    if (Implicit(arguments[0].Type, arguments[i].Type))
                    {
                        var type = arguments[i].Type;
                        for (int j = 0; j < i; j++) arguments[j] = Materialize(arguments[j], type);
                    }
                    else arguments[i] = Materialize(arguments[i], arguments[0].Type);
                }
            }
            if (function is "coopLoad" or "coopLoadT" or "coopStore" or "coopStoreT" or "coopMultiplyAdd")
            {
                if (!syntax.Enables.Contains("wgpu_cooperative_matrix")) throw Error("Cooperative builtins require wgpu_cooperative_matrix.", name.Span);
                bool load = function is "coopLoad" or "coopLoadT", store = function is "coopStore" or "coopStoreT";
                Count(load ? 1 : store ? 2 : 3, load ? 2 : 3);
                ShaderType.CooperativeMatrix matrix;
                if (load)
                {
                    if (name.Templates is not [SExpression.Name type] || ResolveType(type) is not ShaderType.CooperativeMatrix target)
                        throw Error("Cooperative load requires a cooperative matrix template type.", name.Span);
                    ShaderType pointed = arguments[0].Type is ShaderType.Pointer p ? p.Base : throw Error("Cooperative load requires a pointer.", name.Span);
                    var component = pointed is ShaderType.Vector v ? v.Component : pointed as ShaderType.Scalar;
                    matrix = target with { Component = component ?? throw Error("Cooperative load requires scalar/vector memory.", name.Span) };
                }
                else matrix = arguments[store ? 0 : 2].Type as ShaderType.CooperativeMatrix ?? throw Error("Cooperative builtin needs a matrix operand.", name.Span);
                if (store)
                {
                    ShaderType? pointed = arguments[1].Type is ShaderType.Pointer p ? p.Base : null;
                    ShaderType.Scalar? component = pointed is ShaderType.Vector v ? v.Component : pointed as ShaderType.Scalar;
                    if (component != matrix.Component) throw Error("Cooperative store component differs from its memory pointer.", name.Span);
                }
                if (load || store)
                {
                    int count = load ? 2 : 3;
                    if (arguments.Length != count) arguments = [.. arguments, Expression.U32((uint)(function.EndsWith('T') ? matrix.Columns : matrix.Rows))];
                    arguments[^1] = Materialize(arguments[^1], DefaultType(arguments[^1].Type));
                }
                result = store ? new ShaderType.Void() : matrix; snapshot = true;
            }
            else if (function.StartsWith("rayQuery", StringComparison.Ordinal) || function is "getCommittedHitVertexPositions" or "getCandidateHitVertexPositions")
            {
                if (!syntax.Enables.Contains("wgpu_ray_query")) throw Error("Ray query builtins require wgpu_ray_query.", name.Span);
                Count(function == "rayQueryInitialize" ? 3 : function == "rayQueryGenerateIntersection" ? 2 : 1,
                    function == "rayQueryInitialize" ? 3 : function == "rayQueryGenerateIntersection" ? 2 : 1);
                if (function == "rayQueryInitialize") _ = ResolveType(new("RayDesc", [], name.Span));
                if (function == "rayQueryGenerateIntersection") arguments[1] = Materialize(arguments[1], ShaderType.F32);
                result = function switch
                {
                    "rayQueryInitialize" or "rayQueryGenerateIntersection" or "rayQueryConfirmIntersection" or "rayQueryTerminate" => new ShaderType.Void(),
                    "rayQueryProceed" => ShaderType.Bool,
                    "rayQueryGetCommittedIntersection" or "rayQueryGetCandidateIntersection" => ResolveType(new("RayIntersection", [], name.Span)),
                    "getCommittedHitVertexPositions" or "getCandidateHitVertexPositions" => RayQueryTypes.Vertices,
                    _ => throw Error($"Unknown ray query builtin '{function}'.", name.Span)
                };
                snapshot = true;
            }
            else if (function == "select")
            {
                Count(3, 3); (arguments[0], arguments[1]) = Unify(arguments[0], arguments[1]);
                ShaderType condition = arguments[2].Type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.Bool) : ShaderType.Bool;
                arguments[2] = Materialize(arguments[2], condition);
                if (!ConstantEvaluator.TryEvaluate(arguments[2], out _))
                { arguments[0] = Materialize(arguments[0], DefaultType(arguments[0].Type)); arguments[1] = Materialize(arguments[1], arguments[0].Type); }
                result = arguments[0].Type;
            }
            else if (SubgroupBuiltins.Contains(function))
            {
                if (function == "subgroupBallot")
                {
                    Count(0, 1); if (arguments.Length == 1) arguments[0] = Materialize(arguments[0], ShaderType.Bool);
                    result = new ShaderType.Vector(4, ShaderType.U32);
                }
                else
                {
                    Count(SubgroupBuiltins.Indexed(function) ? 2 : 1, SubgroupBuiltins.Indexed(function) ? 2 : 1);
                    arguments[0] = Materialize(arguments[0], DefaultType(arguments[0].Type));
                    if (arguments.Length == 2) arguments[1] = Materialize(arguments[1], ShaderType.U32);
                    result = arguments[0].Type;
                }
                snapshot = true;
            }
            else if (function is "dot4I8Packed" or "dot4U8Packed")
            {
                Count(2, 2); arguments = arguments.Select(e => Materialize(e, ShaderType.U32)).ToArray(); result = function == "dot4I8Packed" ? ShaderType.I32 : ShaderType.U32;
            }
            else if (UnaryMath.Contains(function))
            {
                Count(1, 1);
                if (function is "countLeadingZeros" or "countOneBits" or "countTrailingZeros" or "firstLeadingBit" or "firstTrailingBit" or "reverseBits" or "quantizeToF16") arguments[0] = Materialize(arguments[0], DefaultType(arguments[0].Type));
                if (function.StartsWith("dpdx", StringComparison.Ordinal) || function.StartsWith("dpdy", StringComparison.Ordinal) || function.StartsWith("fwidth", StringComparison.Ordinal)) arguments[0] = Materialize(arguments[0], DefaultType(arguments[0].Type));
                result = arguments[0].Type;
            }
            else if (BinaryMath.Contains(function))
            {
                Count(2, 2);
                if (function != "ldexp") Common();
                else arguments[1] = Materialize(arguments[1], arguments[0].Type is ShaderType.Vector exponentVector ? new ShaderType.Vector(exponentVector.Size, ShaderType.I32) : ShaderType.I32);
                result = function is "dot" or "distance" ? Scalar(arguments[0].Type) : arguments[0].Type;
            }
            else if (TernaryMath.Contains(function))
            {
                Count(3, 3);
                if (function == "refract") { (arguments[0], arguments[1]) = Unify(arguments[0], arguments[1]); arguments[2] = Materialize(arguments[2], Scalar(arguments[0].Type)); }
                else if (function == "mix" && arguments[2].Type is ShaderType.Scalar && arguments[0].Type is ShaderType.Vector)
                { (arguments[0], arguments[1]) = Unify(arguments[0], arguments[1]); arguments[2] = Materialize(arguments[2], Scalar(arguments[0].Type)); }
                else Common();
                result = arguments[0].Type;
            }
            else if (function is "length" or "normalize") { Count(1, 1); result = function == "length" ? Scalar(arguments[0].Type) : arguments[0].Type; }
            else if (function == "determinant") { Count(1, 1); if (arguments[0].Type is not ShaderType.Matrix) throw Error("determinant needs a matrix."); result = Scalar(arguments[0].Type); }
            else if (function == "transpose")
            {
                Count(1, 1); if (arguments[0].Type is not ShaderType.Matrix matrix) throw Error("transpose needs a matrix.");
                result = new ShaderType.Matrix(matrix.Rows, matrix.Columns, matrix.Component);
            }
            else if (function is "all" or "any") { Count(1, 1); result = ShaderType.Bool; }
            else if (function is "extractBits" or "insertBits")
            {
                Count(function == "extractBits" ? 3 : 4, function == "extractBits" ? 3 : 4);
                result = DefaultType(arguments[0].Type); arguments[0] = Materialize(arguments[0], result);
                int firstOffset = function == "extractBits" ? 1 : 2;
                if (firstOffset == 2) arguments[1] = Materialize(arguments[1], result);
                for (int i = firstOffset; i < arguments.Length; i++) arguments[i] = Materialize(arguments[i], ShaderType.U32);
            }
            else if (function is "pack4x8snorm" or "pack4x8unorm" or "pack2x16snorm" or "pack2x16unorm" or "pack2x16float" or "pack4xI8" or "pack4xU8" or "pack4xI8Clamp" or "pack4xU8Clamp")
            {
                Count(1, 1); result = ShaderType.U32;
                ShaderType.Scalar component = function.Contains("I8", StringComparison.Ordinal) ? ShaderType.I32 : function.Contains("U8", StringComparison.Ordinal) ? ShaderType.U32 : ShaderType.F32;
                arguments[0] = Materialize(arguments[0], new ShaderType.Vector(function.StartsWith("pack4", StringComparison.Ordinal) ? 4 : 2, component));
            }
            else if (function is "unpack4x8snorm" or "unpack4x8unorm" or "unpack2x16snorm" or "unpack2x16unorm" or "unpack2x16float" or "unpack4xI8" or "unpack4xU8")
            {
                Count(1, 1); arguments[0] = Materialize(arguments[0], ShaderType.U32);
                result = new ShaderType.Vector(function.StartsWith("unpack4", StringComparison.Ordinal) ? 4 : 2,
                    function.EndsWith("I8", StringComparison.Ordinal) ? ShaderType.I32 : function.EndsWith("U8", StringComparison.Ordinal) ? ShaderType.U32 : ShaderType.F32);
            }
            else if (function is "modf" or "frexp")
            {
                Count(1, 1);
                // These WGSL overloads produce concrete predeclared result structures.
                arguments[0] = Materialize(arguments[0], DefaultType(arguments[0].Type));
                ShaderType type = arguments[0].Type;
                ShaderType second = function == "modf" ? type : type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.I32) : ShaderType.I32;
                var structure = new ShaderType.Structure(Fresh(), [new StructMember("fract", type), new StructMember(function == "modf" ? "whole" : "exp", second)], function == "modf" ? BuiltinResultKind.Modf : BuiltinResultKind.Frexp);
                module.Structures.Add(structure); result = structure;
            }
            else if (function == "arrayLength")
            {
                Count(1, 1);
                if (arguments[0].Type is not ShaderType.Pointer { Base: ShaderType.Array { Length: null } }) throw Error("arrayLength needs a pointer to a runtime array.");
                result = ShaderType.U32; snapshot = true;
            }
            else if (function is "storageBarrier" or "workgroupBarrier" or "textureBarrier" or "subgroupBarrier") { Count(0, 0); result = new ShaderType.Void(); }
            else if (function == "workgroupUniformLoad")
            {
                Count(1, 1); if (arguments[0].Type is not ShaderType.Pointer { Space: AddressSpace.Workgroup } pointer) throw Error("workgroupUniformLoad needs a workgroup pointer.");
                result = pointer.Base is ShaderType.Atomic atomic ? atomic.Component : pointer.Base; snapshot = true;
            }
            else if (function.StartsWith("atomic", StringComparison.Ordinal))
            {
                Count(function == "atomicLoad" ? 1 : function == "atomicCompareExchangeWeak" ? 3 : 2,
                    function == "atomicLoad" ? 1 : function == "atomicCompareExchangeWeak" ? 3 : 2);
                if (arguments[0].Type is not ShaderType.Pointer { Base: ShaderType.Atomic atomic } pointer || pointer.Space is not (AddressSpace.Storage or AddressSpace.Workgroup or AddressSpace.TaskPayload)) throw Error("Atomic operation needs a storage, workgroup or task payload atomic pointer.");
                for (int i = 1; i < arguments.Length; i++) arguments[i] = Materialize(arguments[i], atomic.Component);
                if (function == "atomicCompareExchangeWeak")
                {
                    var structure = new ShaderType.Structure(Fresh(), [new StructMember("old_value", atomic.Component), new StructMember("exchanged", ShaderType.Bool)], BuiltinResultKind.AtomicCompareExchange);
                    module.Structures.Add(structure); result = structure;
                }
                else if (function == "atomicStore") result = new ShaderType.Void();
                else if (function is "atomicLoad" or "atomicAdd" or "atomicSub" or "atomicMin" or "atomicMax" or "atomicAnd" or "atomicOr" or "atomicXor" or "atomicExchange") result = atomic.Component;
                else throw Error($"Unknown atomic builtin '{function}'.");
                snapshot = true;
            }
            else if (function.StartsWith("texture", StringComparison.Ordinal))
            {
                Count(1, 8);
                int textureIndex = function == "textureGather" && arguments[0].Type is not ShaderType.Image ? 1 : 0;
                if (arguments.Length <= textureIndex || arguments[textureIndex].Type is not ShaderType.Image image) throw Error("Texture builtin needs a texture.");
                result = function switch
                {
                    "textureDimensions" => image.Dimension switch { ImageDimension.D1 => ShaderType.U32, ImageDimension.D3 => new ShaderType.Vector(3, ShaderType.U32), _ => new ShaderType.Vector(2, ShaderType.U32) },
                    "textureNumLayers" or "textureNumLevels" or "textureNumSamples" => ShaderType.U32,
                    "textureSampleCompare" or "textureSampleCompareLevel" => ShaderType.F32,
                    "textureStore" or "textureAtomicAdd" or "textureAtomicMin" or "textureAtomicMax" or "textureAtomicAnd" or "textureAtomicOr" or "textureAtomicXor" => new ShaderType.Void(),
                    "textureLoad" or "textureSample" or "textureSampleBias" or "textureSampleLevel" or "textureSampleGrad" or "textureSampleBaseClampToEdge" when image.Depth => ShaderType.F32,
                    "textureLoad" or "textureSample" or "textureSampleBias" or "textureSampleLevel" or "textureSampleGrad" or "textureSampleBaseClampToEdge" or "textureGather" or "textureGatherCompare" => new ShaderType.Vector(4, image.Component),
                    _ => throw Error($"Unknown texture builtin '{function}'.")
                };
                // Abstract coordinates and offsets must become concrete before entering the IR.
                int cursor = textureIndex + 1;
                bool sampling = !function.StartsWith("textureAtomic", StringComparison.Ordinal) && function is not ("textureLoad" or "textureStore" or "textureDimensions" or "textureNumLayers" or "textureNumLevels" or "textureNumSamples");
                if (sampling)
                {
                    cursor++; int rank = image.Dimension switch { ImageDimension.D1 => 1, ImageDimension.D2 => 2, _ => 3 };
                    ShaderType coordinate = rank == 1 ? ShaderType.F32 : new ShaderType.Vector(rank, ShaderType.F32);
                    if (cursor < arguments.Length) arguments[cursor] = Materialize(arguments[cursor], coordinate);
                    cursor++;
                    if (image.Arrayed) cursor++;
                    if (function.Contains("Compare", StringComparison.Ordinal) && cursor < arguments.Length) { arguments[cursor] = Materialize(arguments[cursor], ShaderType.F32); cursor++; }
                    if (function is "textureSampleBias" or "textureSampleLevel" && cursor < arguments.Length)
                    {
                        if (!image.Depth || arguments[cursor].Type is ShaderType.Scalar { Kind: ScalarKind.AbstractInt or ScalarKind.AbstractFloat or ScalarKind.Float }) arguments[cursor] = Materialize(arguments[cursor], ShaderType.F32);
                        cursor++;
                    }
                    if (function == "textureSampleGrad")
                        for (int i = 0; i < 2 && cursor < arguments.Length; i++, cursor++) arguments[cursor] = Materialize(arguments[cursor], coordinate);
                }
                if (function == "textureStore" && arguments.Length > 1) arguments[^1] = Materialize(arguments[^1], new ShaderType.Vector(4, image.Component));
                if (function.StartsWith("textureAtomic", StringComparison.Ordinal) && arguments.Length > 1 && arguments[^1].Type is ShaderType.Scalar { Kind: ScalarKind.AbstractInt or ScalarKind.AbstractFloat })
                    throw Error("Image atomic values require an explicit component type.", name.Span);
                for (int i = 0; i < arguments.Length; i++) arguments[i] = Materialize(arguments[i], DefaultType(arguments[i].Type));
                snapshot = true;
            }
            else throw Error($"Unknown function '{function}'.", name.Span);
            var call = new Expression.Call(function, arguments, result) { Binding = CallBinding.Builtin, Span = name.Span };
            return snapshot && result is not ShaderType.Void ? Snapshot(call, block) : call;
        }
    }
}
