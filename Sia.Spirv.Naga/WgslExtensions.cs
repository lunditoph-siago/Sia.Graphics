namespace Sia.Spirv.Naga;

internal static class WgslExtensions
{
    // Match the implemented directive names in the pinned reference. Enabling a
    // name does not imply that every construct using it has been ported yet.
    public static bool Enable(string name) => name is "f16" or "clip_distances" or "dual_source_blending"
        or "wgpu_mesh_shader" or "wgpu_ray_query" or "wgpu_ray_query_vertex_return" or "wgpu_ray_tracing_pipeline"
        or "wgpu_cooperative_matrix" or "draw_index" or "primitive_index" or "wgpu_per_vertex" or "wgpu_binding_array" or "wgpu_int16";

    public static bool Requirement(string name) => name is "readonly_and_readwrite_storage_textures"
        or "packed_4x8_integer_dot_product" or "pointer_composite_access";
}
