namespace Sia.Spirv.Compiler.Translation.Tests;

// Shared input/oracle data: linked into the GPU runner without a test dependency.
internal static class HandleAliasFixtures
{
    internal const string Image = "@group(0) @binding(0) var image:texture_2d<u32>;@group(0) @binding(1) var spare:texture_2d<u32>;@group(0) @binding(2) var<storage,read_write> output:array<u32>;var<private> count:u32;"
        + "fn coordinate()->vec2i{count+=1u;return vec2i(0);}fn read(t:texture_2d<u32>,p:ptr<function,u32>){let copied_handle=t;*p=textureLoad(copied_handle,coordinate(),0).x;}"
        + "@compute @workgroup_size(1) fn main(){let handle=image;var value=0u;read(handle,&value);output[0]=value;let second=spare;output[1]=textureLoad(second,coordinate(),0).x;output[2]=count;}";
    internal const string Sampler = "@group(0) @binding(0) var image:texture_2d<f32>;@group(0) @binding(1) var sampling:sampler;@group(0) @binding(2) var<storage,read_write> output:array<u32>;"
        + "fn read(t:texture_2d<f32>,s:sampler,p:ptr<function,u32>){let copied_handle=t;let sample=s;*p=bitcast<u32>(textureSampleLevel(copied_handle,sample,vec2f(0.5),0.0).x);}"
        + "@compute @workgroup_size(1) fn main(){let handle=image;let sample=sampling;var value=0u;read(handle,sample,&value);output[0]=value;output[1]=7u;}";
    internal const string Scene = "enable wgpu_ray_query;@group(0) @binding(0) var scene:acceleration_structure;"
        + "fn initialize(q:ptr<function,ray_query>,s:acceleration_structure){let copied_handle=s;rayQueryInitialize(q,copied_handle,RayDesc(0u,255u,0.0,1.0,vec3f(0),vec3f(1)));}"
        + "@compute @workgroup_size(1) fn main(){let handle=scene;var query:ray_query;initialize(&query,handle);rayQueryTerminate(&query);}";
    internal const string Descriptor = "enable wgpu_binding_array;@group(0) @binding(0) var images:binding_array<texture_2d<u32>,2>;@group(0) @binding(1) var<storage,read_write> output:array<u32>;var<private> index:u32;"
        + "fn choose()->u32{let v=index;index=1u;return v;}fn read(t:texture_2d<u32>,p:ptr<function,u32>){let copied_handle=t;*p=textureLoad(copied_handle,vec2i(0),0).x;}"
        + "@compute @workgroup_size(1) fn main(){let handles=images;let image=handles[choose()];let captured=image;var value=0u;read(captured,&value);output[0]=value;output[1]=index;}";
    internal const string Atomic = "@group(0) @binding(0) var image:texture_storage_2d<r32uint,atomic>;"
        + "@compute @workgroup_size(1) fn main(){let handle=image;let copied_handle=handle;textureAtomicAdd(copied_handle,vec2i(0),1u);}";
    internal static string Source(string name) => name switch {
        "Image" => Image, "Sampler" => Sampler, "Scene" => Scene, "Descriptor" => Descriptor, "Atomic" => Atomic,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static uint[] Expected(string name) => name switch {
        "Image" => [13, 29, 2], "Sampler" => [0x3e800000, 7],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}
