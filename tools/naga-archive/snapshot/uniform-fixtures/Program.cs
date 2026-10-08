using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Tests;
using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Module = Sia.Spirv.Naga.IR.Module;
string root=".work/naga-csharp/uniform-memory";Directory.CreateDirectory(root);
var cases=new List<object>();
object Invoke(string name,params object[] args)=>typeof(UniformMemoryTests).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,args)!;
void Save(string name,Module module,bool vulkan=false,string rejection=null,object[] probes=null,bool gpu=true){
 File.WriteAllBytes($"{root}/{name}.spv",SpirvWriter.Write(module));cases.Add(new{name,vulkan,rejection,probes=probes??Array.Empty<object>(),gpu});
}
foreach(uint flags in new uint[]{0,1,2,4,7,32,48,55})foreach(bool dynamic in new[]{false,true}){
 bool vulkan=flags>=32;var module=(Module)Invoke("Fixture",flags,dynamic,vulkan);
 Save($"leaf-{flags}-{dynamic}",module,vulkan,(flags&24)!=0?"availability/visibility":(flags&1)!=0?"storage-buffer root":null);
}
foreach(string kind in new[]{"root","matrix","array"})Save("whole-"+kind,(Module)Invoke("AggregateFixture",kind,3u,false,MemoryDecorations.None),rejection:"storage-buffer root");
foreach(var decoration in new[]{MemoryDecorations.Volatile,MemoryDecorations.Coherent})Save("member-"+decoration,(Module)Invoke("AggregateFixture","root",0u,true,decoration),true,decoration==MemoryDecorations.Volatile?"storage-buffer root":"availability/visibility");
foreach(int columns in new[]{2,3,4})foreach(int rows in new[]{2,3,4})foreach(bool half in new[]{false,true})
 Save($"shape-{columns}-{rows}-{half}",(Module)Invoke("ShapeFixture",columns,rows,half,true),rejection:"storage-buffer root");
foreach(string field in new[]{"AliasSource","NestedSource"}){
 string source=(string)typeof(UniformMemoryTests).GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
 File.WriteAllText($"{root}/{field}.wgsl",source);Save(field,WgslReader.Parse(source));
}
foreach(bool dynamic in new[]{false,true}){
 string source="enable wgpu_binding_array;struct Data{matrix:mat2x2f,tail:f32,}@group(0) @binding(0) var<uniform> data:binding_array<Data,2>;"
 +"@group(0) @binding(1) var<storage,read_write> output:array<f32>;@compute @workgroup_size(1) fn main(){"
 +(dynamic?"let index=u32(output[0]);":"const index=1u;")+"output[0]=data[index].matrix[1][1];}";
 string name="binding-"+dynamic;File.WriteAllText($"{root}/{name}.wgsl",source);Save(name,WgslReader.Parse(source),gpu:false);
}
var invalid=(Sia.Spirv.Naga.Spirv.SpirvBinary)Invoke("UnsupportedStrideFixture");File.WriteAllBytes(root+"/unsupported-stride.spv",invalid.ToBytes());
File.WriteAllText(root+"/fixtures.json",JsonSerializer.Serialize(cases,new JsonSerializerOptions{WriteIndented=true}));
