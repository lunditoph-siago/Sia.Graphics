using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Valid;

public static partial class ModuleValidator
{
    private sealed partial class Validator
    {
        private static bool Float(ShaderType type) => Scalar(type)?.Kind is ScalarKind.Float or ScalarKind.AbstractFloat;
        private static bool ScalarVector(ShaderType type) => type is ShaderType.Scalar or ShaderType.Vector;
        private ShaderType Call(Expression.Call call)
        {
            Require(Enum.IsDefined(call.Binding), "Call binding must explicitly select function or builtin.", call.Span);
            ShaderType[] args = call.Arguments.Select(Expr).ToArray(); string name = call.Function;
            void Count(int count) => Require(args.Length == count, $"'{name}' requires {count} arguments.", call.Span);
            void AllSame() { foreach (var arg in args.Skip(1)) Same(arg, args[0], $"'{name}' argument types differ.", call.Span); }
            void FloatData() => Require(Float(args[0]) && ScalarVector(args[0]), $"'{name}' requires floating-point scalar/vector data.", call.Span);
            if (call.Binding == CallBinding.Function && functions.TryGetValue(name, out var callee))
            {
                Require(call.AtomicMemory is null && call.MemoryAccess is null, "User function calls cannot carry builtin memory metadata.", call.Span);
                Require(callee.Stage is null && function is not null, "Entry points cannot be called; calls require a function body.", call.Span);
                Count(callee.Arguments.Count);
                for (int i = 0; i < args.Length; i++) Same(args[i], callee.Arguments[i].Type, "Function argument type mismatch.", call.Span);
                calls[function!.Name].Add(name); return callee.ReturnType;
            }
            Require(call.Binding == CallBinding.Builtin, "Unknown resolved function call.", call.Span);
            AtomicMemory(call);
            CallMemoryAccess(call);
            if (name is "coopLoad" or "coopLoadT" or "coopStore" or "coopStoreT" or "coopMultiplyAdd") return Cooperative(call, args);
            if (name.StartsWith("spirvRayQuery", StringComparison.Ordinal))
            {
                bool stateGetter = name is "spirvRayQueryGetRayTMinKHR" or "spirvRayQueryGetRayFlagsKHR";
                bool getter = RayQueryTypes.RawGetterType(name) is not null;
                Count(getter ? stateGetter ? 1 : 2 : name == "spirvRayQueryInitializeKHR" ? 8 : name == "spirvRayQueryGenerateIntersectionKHR" ? 2 : 1);
                Require(args[0] is ShaderType.Pointer { Base: ShaderType.RayQuery, Space: AddressSpace.Function }, "Raw ray query operation requires a function query pointer.", call.Span);
                if (getter)
                {
                    if (!stateGetter) Require(call.Arguments[1] is Expression.Literal { Value: uint selector } && selector <= 1 && args[1] == ShaderType.U32, "Raw getter requires a candidate or committed selector.", call.Span);
                    Require(RayQueryTypes.RawGetterResult(name, call.Type), "Invalid raw ray getter result.", call.Span);
                    if (name == "spirvRayQueryGetIntersectionTriangleVertexPositionsKHR")
                        Require(args[0] is ShaderType.Pointer { Base: ShaderType.RayQuery { VertexReturn: true } }, "Raw vertex fetch requires query vertex support.", call.Span);
                }
                else if (name == "spirvRayQueryInitializeKHR")
                {
                    Require(args[1] is ShaderType.AccelerationStructure, "Raw query initialization requires an acceleration structure.", call.Span);
                    Same(args[2], ShaderType.U32, "Ray flags must be u32."); Same(args[3], ShaderType.U32, "Ray mask must be u32.");
                    foreach (int vector in new[] { 4, 6 }) Same(args[vector], new ShaderType.Vector(3, ShaderType.F32), "Ray origin and direction must be vec3f.");
                    foreach (int distance in new[] { 5, 7 }) Same(args[distance], ShaderType.F32, "Ray distances must be f32.");
                    Same(call.Type, new ShaderType.Void(), "Raw initialize has no result.");
                }
                else if (name == "spirvRayQueryGenerateIntersectionKHR")
                { Same(args[1], ShaderType.F32, "Ray hit distance must be f32."); Same(call.Type, new ShaderType.Void(), "Raw generate has no result."); }
                else if (name == "spirvRayQueryProceedKHR") Same(call.Type, ShaderType.Bool, "Raw proceed result must be bool.");
                else
                { Require(name is "spirvRayQueryTerminateKHR" or "spirvRayQueryConfirmIntersectionKHR", "Unknown raw ray query operation.", call.Span); Same(call.Type, new ShaderType.Void(), "Raw ray operation has no result."); }
                return call.Type;
            }
            if (name.StartsWith("rayQuery", StringComparison.Ordinal) || name is "getCommittedHitVertexPositions" or "getCandidateHitVertexPositions")
            {
                Count(name == "rayQueryInitialize" ? 3 : name == "rayQueryGenerateIntersection" ? 2 : 1);
                Require(args[0] is ShaderType.Pointer { Base: ShaderType.RayQuery, Space: AddressSpace.Function }, "Ray query operation requires a function-local ray query pointer.", call.Span);
                var query = (ShaderType.RayQuery)((ShaderType.Pointer)args[0]).Base;
                if (name == "rayQueryInitialize")
                {
                    Require(args[1] is ShaderType.AccelerationStructure acceleration && (!query.VertexReturn || acceleration.VertexReturn), "Ray query acceleration structure lacks required vertex support.", call.Span);
                    Require(RayQueryTypes.SameStructure(args[2], RayQueryTypes.Descriptor), "Ray query initialize requires RayDesc.", call.Span);
                }
                if (name == "rayQueryGenerateIntersection") Same(args[1], ShaderType.F32, "Generated intersection distance must be f32.", call.Span);
                if (name.EndsWith("VertexPositions", StringComparison.Ordinal)) Require(query.VertexReturn, "Vertex positions require ray_query<vertex_return>.", call.Span);
                return name switch
                {
                    "rayQueryInitialize" or "rayQueryGenerateIntersection" or "rayQueryConfirmIntersection" or "rayQueryTerminate" => new ShaderType.Void(),
                    "rayQueryProceed" => ShaderType.Bool,
                    "rayQueryGetCommittedIntersection" or "rayQueryGetCandidateIntersection" when RayQueryTypes.SameStructure(call.Type, RayQueryTypes.Intersection) => call.Type,
                    "getCommittedHitVertexPositions" or "getCandidateHitVertexPositions" when call.Type == RayQueryTypes.Vertices => call.Type,
                    _ => throw Error("Invalid ray query operation or result type.", call.Span)
                };
            }
            if (name is "storageBarrier" or "workgroupBarrier" or "textureBarrier" or "subgroupBarrier") { Count(0); Restrict(WorkgroupStages); return new ShaderType.Void(); }
            if (name.StartsWith("texture", StringComparison.Ordinal)) return Texture(call, args);
            if (SubgroupBuiltins.Contains(name))
            {
                Restrict(WorkgroupStages | Fragment);
                if (name == "subgroupBallot")
                { Require(args.Length == 0 || args.Length == 1 && args[0] == ShaderType.Bool, "Subgroup ballot takes an optional boolean predicate.", call.Span); return new ShaderType.Vector(4, ShaderType.U32); }
                Count(SubgroupBuiltins.Indexed(name) ? 2 : 1);
                Require(ScalarVector(args[0]), "Subgroup operations require scalar/vector data.", call.Span);
                if (args.Length == 2) Same(args[1], ShaderType.U32, "Subgroup lane index must be u32.", call.Span);
                if (name is "subgroupAll" or "subgroupAny") Same(args[0], ShaderType.Bool, "Subgroup vote requires a boolean scalar.", call.Span);
                else if (!SubgroupBuiltins.Gather(name)) Require(name is "subgroupAnd" or "subgroupOr" or "subgroupXor" ? Integer(args[0]) || Scalar(args[0]) == ShaderType.Bool : Numeric(args[0]), "Invalid subgroup reduction operand.", call.Span);
                return args[0];
            }
            if (name.StartsWith("atomic", StringComparison.Ordinal) || name == "spirvAtomicCompareExchange")
            {
                Count(name == "atomicLoad" ? 1 : name is "atomicCompareExchangeWeak" or "spirvAtomicCompareExchange" ? 3 : 2);
                Require(args[0] is ShaderType.Pointer { Base: ShaderType.Atomic, Space: AddressSpace.Storage or AddressSpace.Workgroup or AddressSpace.TaskPayload } memory && (memory.Access & StorageAccess.ReadWrite) == StorageAccess.ReadWrite, "Atomic operation requires read-write atomic memory.", call.Span);
                if (((ShaderType.Pointer)args[0]).Space == AddressSpace.TaskPayload && name != "atomicLoad") Restrict(Task);
                var atomic = (ShaderType.Atomic)((ShaderType.Pointer)args[0]).Base;
                foreach (var arg in args.Skip(1)) Same(arg, atomic.Component, "Atomic value type mismatch.", call.Span);
                if (atomic.Component.Kind == ScalarKind.Float && name is not ("atomicLoad" or "atomicStore"))
                {
                    Require(name is "atomicAdd" or "atomicSub" or "atomicExchange", "Float32 atomics only support load, store, add, subtract and exchange.", call.Span);
                    Require(((ShaderType.Pointer)args[0]).Space == AddressSpace.Storage, "Float32 read-modify-write atomics require storage memory.", call.Span);
                }
                if (name == "atomicStore") return new ShaderType.Void();
                if (name == "atomicCompareExchangeWeak")
                {
                    Require(call.Type is ShaderType.Structure s && s.Members.Count == 2 && s.Members[0].Type == atomic.Component && s.Members[1].Type == ShaderType.Bool, "Invalid atomic compare/exchange result.", call.Span); return call.Type;
                }
                Require(name is "atomicLoad" or "atomicAdd" or "atomicSub" or "atomicMin" or "atomicMax" or "atomicAnd" or "atomicOr" or "atomicXor" or "atomicExchange" or "spirvAtomicCompareExchange", "Unknown atomic builtin.", call.Span); return atomic.Component;
            }
            if (name == "arrayLength")
            { Count(1); Require(args[0] is ShaderType.Pointer { Base: ShaderType.Array { Length: null, OverrideLength: null }, Space: AddressSpace.Storage }, "arrayLength requires a runtime storage-array pointer.", call.Span); return ShaderType.U32; }
            if (name == "workgroupUniformLoad")
            { Count(1); Require(args[0] is ShaderType.Pointer { Space: AddressSpace.Workgroup } p && (p.Base is ShaderType.Atomic || !ContainsAtomic(p.Base)) && !RuntimeSized(p.Base) && !PipelineSized(p.Base), "workgroupUniformLoad requires constructible workgroup data or an atomic scalar.", call.Span); Restrict(WorkgroupStages); var type = ((ShaderType.Pointer)args[0]).Base; return type is ShaderType.Atomic atomic ? atomic.Component : type; }
            if (name == "select")
            {
                Count(3); Same(args[0], args[1], "Select alternatives differ.", call.Span);
                Require(ScalarVector(args[0]) && (args[2] == ShaderType.Bool || args[0] is ShaderType.Vector v && args[2] == new ShaderType.Vector(v.Size, ShaderType.Bool)), "Invalid select operand types.", call.Span); return args[0];
            }
            if (name is "dot4I8Packed" or "dot4U8Packed")
            { Count(2); foreach (var arg in args) Same(arg, ShaderType.U32, "Packed dot product requires u32 packed operands.", call.Span); return name == "dot4I8Packed" ? ShaderType.I32 : ShaderType.U32; }
            if (name is "all" or "any") { Count(1); Require(ScalarVector(args[0]) && Scalar(args[0]) == ShaderType.Bool, "Boolean reduction requires boolean data.", call.Span); return ShaderType.Bool; }
            if (name is "isNan" or "isInf") { Count(1); FloatData(); return args[0] is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.Bool) : ShaderType.Bool; }
            if (name.StartsWith("dpdx", StringComparison.Ordinal) || name.StartsWith("dpdy", StringComparison.Ordinal) || name.StartsWith("fwidth", StringComparison.Ordinal))
            { Count(1); FloatData(); Restrict(Fragment); return args[0]; }
            if (ShaderBuiltinEffects.IsFloatUnary(name)) { Count(1); FloatData(); return args[0]; }
            if (ShaderBuiltinEffects.IsIntegerUnary(name)) { Count(1); Require(Integer(args[0]) && ScalarVector(args[0]), "Bit builtin requires integer data.", call.Span); return args[0]; }
            if (name is "abs" or "sign")
            { Count(1); Require(Numeric(args[0]) && ScalarVector(args[0]) && (name == "abs" || Scalar(args[0])?.Kind != ScalarKind.Uint), "Invalid signed math operand.", call.Span); return args[0]; }
            if (name is "min" or "max" or "clamp")
            { Count(name == "clamp" ? 3 : 2); AllSame(); Require(Numeric(args[0]) && ScalarVector(args[0]), "Invalid numeric builtin data.", call.Span); return args[0]; }
            if (name is "atan2" or "pow" or "step" or "distance" or "dot" or "cross" or "reflect")
            {
                Count(2); AllSame();
                Require(Numeric(args[0]) && ScalarVector(args[0]) && (name == "dot" || Float(args[0])), "Invalid math operand.", call.Span);
                if (name is "cross") Require(args[0] is ShaderType.Vector { Size: 3 }, "cross requires vec3.", call.Span);
                if (name is "dot" or "reflect") Require(args[0] is ShaderType.Vector, "Builtin requires vectors.", call.Span);
                return name is "distance" or "dot" ? Scalar(args[0])! : args[0];
            }
            if (name is "length" or "normalize") { Count(1); FloatData(); return name == "length" ? Scalar(args[0])! : args[0]; }
            if (name is "fma" or "smoothstep" or "faceForward" or "mix" or "refract")
            {
                Count(3); FloatData(); Same(args[0], args[1], "Math operand types differ.", call.Span);
                Require(args[2] == args[0] && name != "refract" || args[2] == Scalar(args[0]) && name is "mix" or "refract", "Third math operand has the wrong shape.", call.Span);
                if (name is "faceForward" or "refract") Require(args[0] is ShaderType.Vector, "Builtin requires vectors.", call.Span); return args[0];
            }
            if (name == "ldexp")
            { Count(2); FloatData(); Same(args[1], args[0] is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.I32) : ShaderType.I32, "ldexp exponent shape mismatch.", call.Span); return args[0]; }
            if (name is "determinant" or "transpose")
            {
                Count(1); Require(args[0] is ShaderType.Matrix, "Builtin requires a matrix.", call.Span); var m = (ShaderType.Matrix)args[0];
                if (name == "determinant") { Require(m.Columns == m.Rows, "determinant requires a square matrix.", call.Span); return m.Component; }
                return new ShaderType.Matrix(m.Rows, m.Columns, m.Component);
            }
            if (name == "outerProduct")
            { Count(2); Require(args[0] is ShaderType.Vector && args[1] is ShaderType.Vector && Float(args[0]) && Scalar(args[0]) == Scalar(args[1]), "outerProduct requires floating vectors.", call.Span); return new ShaderType.Matrix(((ShaderType.Vector)args[1]).Size, ((ShaderType.Vector)args[0]).Size, Scalar(args[0])!); }
            if (name is "modf" or "frexp")
            {
                Count(1); FloatData(); ShaderType second = name == "modf" ? args[0] : args[0] is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.I32) : ShaderType.I32;
                Require(call.Type is ShaderType.Structure s && s.Members.Count == 2 && s.Members[0].Type == args[0] && s.Members[1].Type == second, "Builtin result structure is invalid.", call.Span); return call.Type;
            }
            if (name is "extractBits" or "insertBits")
            {
                Count(name == "extractBits" ? 3 : 4); Require(Integer(args[0]) && ScalarVector(args[0]), "Bit extraction requires integer data.", call.Span);
                int first = name == "extractBits" ? 1 : 2; if (first == 2) Same(args[0], args[1], "Bit insertion type mismatch.", call.Span);
                for (int i = first; i < args.Length; i++) Same(args[i], ShaderType.U32, "Bit offset/count must be u32.", call.Span); return args[0];
            }
            if (name.StartsWith("pack", StringComparison.Ordinal) || name.StartsWith("unpack", StringComparison.Ordinal))
            {
                Count(1); bool unpack = name.StartsWith("unpack", StringComparison.Ordinal); string suffix = name[(unpack ? 6 : 4)..];
                Require(suffix is "4x8snorm" or "4x8unorm" or "2x16snorm" or "2x16unorm" or "2x16float" or "4xI8" or "4xU8" || !unpack && suffix is "4xI8Clamp" or "4xU8Clamp", "Unknown packing builtin.", call.Span);
                var vector = new ShaderType.Vector(suffix.StartsWith('4') ? 4 : 2, suffix.Contains("I8", StringComparison.Ordinal) ? ShaderType.I32 : suffix.Contains("U8", StringComparison.Ordinal) ? ShaderType.U32 : ShaderType.F32);
                Same(args[0], unpack ? ShaderType.U32 : vector, "Packing builtin operand type mismatch.", call.Span); return unpack ? vector : ShaderType.U32;
            }
            throw Error($"Unsupported builtin '{name}'.", call.Span);
        }

        private ShaderType Texture(Expression.Call call, ShaderType[] args)
        {
            string name = call.Function; int textureIndex = name == "textureGather" && args.Length > 0 && args[0] is not ShaderType.Image ? 1 : 0;
            Require(args.Length > textureIndex && args[textureIndex] is ShaderType.Image, "Texture builtin requires a texture.", call.Span);
            var image = (ShaderType.Image)args[textureIndex]; int dimensions = image.Dimension switch { ImageDimension.D1 => 1, ImageDimension.D3 or ImageDimension.Cube => 3, _ => 2 };
            ShaderType Shape(ShaderType.Scalar component, int n) => n == 1 ? component : new ShaderType.Vector(n, component);
            bool Index(ShaderType t) => t is ShaderType.Scalar && Integer(t);
            if (name.StartsWith("textureAtomic", StringComparison.Ordinal))
            {
                Require(name is "textureAtomicAdd" or "textureAtomicMin" or "textureAtomicMax" or "textureAtomicAnd" or "textureAtomicOr" or "textureAtomicXor", "Unknown image atomic operation.", call.Span);
                Require(image.StorageFormat is "r32uint" or "r32sint" or "r64uint" && (image.Access & StorageAccess.Atomic) != 0, "Image atomic requires an atomic r32uint, r32sint or r64uint storage texture.", call.Span);
                Require(image.StorageFormat != "r64uint" || name is "textureAtomicMin" or "textureAtomicMax", "64-bit image atomics only support min/max.", call.Span);
                Require(image.Dimension != ImageDimension.Cube && !image.Multisampled && args.Length == (image.Arrayed ? 4 : 3), "Invalid image atomic arguments.", call.Span);
                Require(Scalar(args[1])?.Kind is ScalarKind.Sint or ScalarKind.Uint && (dimensions == 1 ? args[1] is ShaderType.Scalar : args[1] is ShaderType.Vector coordinates && coordinates.Size == dimensions), "Image atomic coordinate shape/type mismatch.", call.Span);
                if (image.Arrayed) Require(Index(args[2]), "Image atomic array index must be an integer scalar.", call.Span);
                Same(args[^1], image.Component, "Image atomic value type mismatch.", call.Span);
                Expression origin = call.Arguments[0] is Expression.Access access ? access.Base : call.Arguments[0];
                Require(origin is Expression.Reference reference && module.Globals.Any(g => g.Name == reference.Name && g.Space == AddressSpace.Handle), "Image atomic requires a global texture or binding array element.", call.Span);
                return new ShaderType.Void();
            }
            if (name is "textureDimensions" or "textureNumLayers" or "textureNumLevels" or "textureNumSamples")
            {
                Require(args.Length == 1 || name == "textureDimensions" && args.Length == 2 && Index(args[1]) && image.StorageFormat is null && !image.Multisampled, "Invalid texture query arguments.", call.Span);
                Require(name != "textureNumLayers" || image.Arrayed, "Layer count requires arrayed texture.", call.Span);
                Require(name != "textureNumSamples" || image.Multisampled, "Sample count requires multisampled texture.", call.Span);
                Require(name != "textureNumLevels" || image.StorageFormat is null && !image.Multisampled, "Mip count requires sampled non-multisampled texture.", call.Span);
                return name == "textureDimensions" ? Shape(ShaderType.U32, image.Dimension == ImageDimension.Cube ? 2 : dimensions) : ShaderType.U32;
            }
            bool load = name == "textureLoad", store = name == "textureStore", compare = name.Contains("Compare", StringComparison.Ordinal), gather = name is "textureGather" or "textureGatherCompare";
            if (name == "textureSampleBaseClampToEdge") Require(image.Dimension == ImageDimension.D2 && !image.Arrayed && !image.Depth && image.Component == ShaderType.F32 && args.Length == 3, "Base clamp sampling requires a non-array sampled 2D f32 texture, sampler and coordinates.", call.Span);
            Require(load || store || name is "textureSample" or "textureSampleBias" or "textureSampleLevel" or "textureSampleGrad" or "textureSampleCompare" or "textureSampleCompareLevel" or "textureSampleBaseClampToEdge" or "textureGather" or "textureGatherCompare", "Unknown texture builtin.", call.Span);
            int cursor = textureIndex + 1;
            if (!load && !store)
            {
                Require(image.StorageFormat is null && !image.Multisampled && (image.Component == ShaderType.F32 || gather) && args.Length > cursor && args[cursor] is ShaderType.Sampler s && s.Comparison == compare, "Invalid sampled texture/sampler combination.", call.Span);
                cursor++;
            }
            Require(args.Length > cursor, "Missing texture coordinates.", call.Span);
            ShaderType coordinate = args[cursor++];
            Require(load || store ? Scalar(coordinate)?.Kind is ScalarKind.Sint or ScalarKind.Uint && (dimensions == 1 ? coordinate is ShaderType.Scalar : coordinate is ShaderType.Vector v && v.Size == dimensions) : coordinate == Shape(ShaderType.F32, dimensions), "Texture coordinate shape/type mismatch.", call.Span);
            if (image.Arrayed) { Require(args.Length > cursor && Index(args[cursor]), "Missing texture array index.", call.Span); cursor++; }
            if (load)
            {
                Require(image.Dimension != ImageDimension.Cube && (image.StorageFormat is null || (image.Access & StorageAccess.Read) != 0), "Texture cannot be loaded.", call.Span);
                if (image.StorageFormat is null) { Require(args.Length > cursor && Index(args[cursor]), "Missing sample or mip index.", call.Span); cursor++; }
                Require(args.Length == cursor, "Unexpected textureLoad arguments.", call.Span);
            }
            else if (store)
            {
                Require(image.StorageFormat is not null && (image.Access & StorageAccess.Write) != 0 && args.Length == cursor + 1, "Texture store requires writable storage texture and a value.", call.Span);
                Same(args[cursor], new ShaderType.Vector(4, image.Component), "Texture store value type mismatch.", call.Span);
                return new ShaderType.Void();
            }
            else
            {
                if (compare) { Require(image.Depth && args.Length > cursor && args[cursor] == ShaderType.F32, "Comparison sampling requires depth and f32 comparison value.", call.Span); cursor++; }
                if (name is "textureSampleLevel" or "textureSampleBias") { Require(args.Length > cursor && (args[cursor] == ShaderType.F32 || name == "textureSampleLevel" && image.Depth && Index(args[cursor])), "Missing sampling level/bias.", call.Span); cursor++; }
                if (name == "textureSampleGrad") { Require(args.Length >= cursor + 2 && args[cursor] == Shape(ShaderType.F32, dimensions) && args[cursor + 1] == args[cursor], "Invalid texture gradients.", call.Span); cursor += 2; }
                if (args.Length == cursor + 1) { Require(image.Dimension != ImageDimension.Cube && args[cursor] == Shape(ShaderType.I32, dimensions), "Invalid texture offset.", call.Span); cursor++; }
                Require(args.Length == cursor, "Unexpected sampling arguments.", call.Span);
                if (name is "textureSample" or "textureSampleBias" or "textureSampleCompare") Restrict(Fragment);
            }
            return compare && !gather || load && image.Depth || !gather && !load && image.Depth ? ShaderType.F32 : new ShaderType.Vector(4, image.Component);
        }
    }
}
