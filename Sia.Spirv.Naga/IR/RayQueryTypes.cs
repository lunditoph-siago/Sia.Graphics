namespace Sia.Spirv.Naga.IR;

internal static class RayQueryTypes
{
    public static readonly ShaderType.Structure Descriptor = new("RayDesc",
        [new("flags", ShaderType.U32), new("cull_mask", ShaderType.U32),
         new("tmin", ShaderType.F32), new("tmax", ShaderType.F32),
         new("origin", new ShaderType.Vector(3, ShaderType.F32)), new("dir", new ShaderType.Vector(3, ShaderType.F32))], BuiltinResultKind.RayDesc);
    public static readonly ShaderType.Structure Intersection = new("RayIntersection",
        [new("kind", ShaderType.U32), new("t", ShaderType.F32), new("instance_custom_data", ShaderType.U32),
         new("instance_index", ShaderType.U32), new("sbt_record_offset", ShaderType.U32),
         new("geometry_index", ShaderType.U32), new("primitive_index", ShaderType.U32),
         new("barycentrics", new ShaderType.Vector(2, ShaderType.F32)), new("front_face", ShaderType.Bool),
         new("object_to_world", new ShaderType.Matrix(4, 3, ShaderType.F32)),
         new("world_to_object", new ShaderType.Matrix(4, 3, ShaderType.F32))], BuiltinResultKind.RayIntersection);
    public static readonly ShaderType.Array Vertices = new(new ShaderType.Vector(3, ShaderType.F32), 3);
    // Raw committed getters can observe in-progress traversal, which WGSL's
    // committed-intersection builtin cannot represent. Keep them distinct in IR.
    public static ShaderType? RawGetterType(string name) => name switch
    {
        "spirvRayQueryGetIntersectionTypeKHR" or "spirvRayQueryGetIntersectionInstanceCustomIndexKHR"
            or "spirvRayQueryGetIntersectionInstanceIdKHR" or "spirvRayQueryGetIntersectionInstanceShaderBindingTableRecordOffsetKHR"
            or "spirvRayQueryGetIntersectionGeometryIndexKHR" or "spirvRayQueryGetIntersectionPrimitiveIndexKHR" => ShaderType.U32,
        "spirvRayQueryGetIntersectionTKHR" => ShaderType.F32,
        "spirvRayQueryGetIntersectionBarycentricsKHR" => new ShaderType.Vector(2, ShaderType.F32),
        "spirvRayQueryGetIntersectionFrontFaceKHR" => ShaderType.Bool,
        "spirvRayQueryGetIntersectionObjectToWorldKHR" or "spirvRayQueryGetIntersectionWorldToObjectKHR" => new ShaderType.Matrix(4, 3, ShaderType.F32),
        "spirvRayQueryGetIntersectionTriangleVertexPositionsKHR" => Vertices,
        "spirvRayQueryGetRayTMinKHR" => ShaderType.F32,
        "spirvRayQueryGetRayFlagsKHR" => ShaderType.U32,
        _ => null
    };
    public static bool RawGetterResult(string name, ShaderType type) => name == "spirvRayQueryGetIntersectionTriangleVertexPositionsKHR"
        ? type is ShaderType.Array { Element: ShaderType.Vector { Size: 3, Component: { Kind: ScalarKind.Float, Width: 4 } }, Length: 3, Stride: null or 16, OverrideLength: null }
        : RawGetterType(name) == type;
    public static bool SameStructure(ShaderType type, ShaderType.Structure expected) => type is ShaderType.Structure actual
        && actual.BuiltinResult == expected.BuiltinResult && actual.Members.SequenceEqual(expected.Members);

    public static bool TryConstant(string name, out uint value)
    {
        value = name switch
        {
            "RAY_FLAG_NONE" or "RAY_QUERY_INTERSECTION_NONE" => 0,
            "RAY_FLAG_FORCE_OPAQUE" or "RAY_QUERY_INTERSECTION_TRIANGLE" => 1,
            "RAY_FLAG_FORCE_NO_OPAQUE" or "RAY_QUERY_INTERSECTION_GENERATED" => 2,
            "RAY_QUERY_INTERSECTION_AABB" => 3,
            "RAY_FLAG_TERMINATE_ON_FIRST_HIT" => 4, "RAY_FLAG_SKIP_CLOSEST_HIT_SHADER" => 8,
            "RAY_FLAG_CULL_BACK_FACING" => 16, "RAY_FLAG_CULL_FRONT_FACING" => 32,
            "RAY_FLAG_CULL_OPAQUE" => 64, "RAY_FLAG_CULL_NO_OPAQUE" => 128,
            "RAY_FLAG_SKIP_TRIANGLES" => 256, "RAY_FLAG_SKIP_AABBS" => 512, _ => uint.MaxValue
        };
        return value != uint.MaxValue;
    }
}
