using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Tests;
using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Spirv;
string root=".work/naga-csharp/member-memory";Directory.CreateDirectory(root);
var factory=typeof(MemberMemoryTests).GetMethod("DecoratedSource",BindingFlags.Static|BindingFlags.NonPublic)!;
string scalar=(string)typeof(MemberMemoryTests).GetField("ScalarSource",BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
var cases=new List<object>();
void Save(string name,SpirvBinary input,uint[] values,uint[] expected,bool strong=false,bool coop=false,string rejection=null){
 File.WriteAllBytes($"{root}/{name}.spv",input.ToBytes());
 var module=SpirvReader.Parse(input.ToBytes());module.VulkanMemoryModel=true;
 File.WriteAllBytes($"{root}/{name}-vulkan.spv",SpirvWriter.Write(module));
 cases.Add(new{name,values,expected,strong,coop,rejection});
}
void Decorate(string name,string source,string structure,uint decoration,uint[] values,uint[] expected,bool strong=false){
 var input=(SpirvBinary)factory.Invoke(null,[source,structure,0u,decoration])!;
 Save(name,input,values,expected,strong);
}
foreach(uint decoration in new uint[]{21,23}){
 Decorate("scalar-"+decoration,scalar,"Data",decoration,[9,17],[9,17]);
 Decorate("nested-"+decoration,"struct Inner{x:u32,y:u32,}struct Outer{values:array<Inner,2>,}"
 +"@group(0) @binding(0) var<storage,read_write> data:Outer;@group(0) @binding(1) var<storage,read_write> output:array<u32>;"
 +"@compute @workgroup_size(1) fn main(){output[0]=data.values[1].x;output[1]=data.values[0].y;}","Inner",decoration,[9,17,25,33],[25,17]);
 foreach(bool strong in new[]{false,true})Decorate($"atomic-{decoration}-{strong}","struct Data{x:atomic<u32>,}"
 +"@group(0) @binding(0) var<storage,read_write> data:Data;@group(0) @binding(1) var<storage,read_write> output:array<u32>;"
 +"@compute @workgroup_size(1) fn main(){output[0]="+(strong?"atomicCompareExchangeWeak(&data.x,9u,10u).old_value":"atomicAdd(&data.x,1u)")+";output[1]=atomicLoad(&data.x);}","Data",decoration,[9],[9,10],strong&&decoration==21);
}
foreach(bool alias in new[]{false,true}){
 string body=alias?"let p=&data;output[0]=*p;*p=*p+1u;output[1]=*p;":"output[0]=data;data=data+1u;output[1]=data;";
 var module=WgslReader.Parse("@coherent @volatile @group(0) @binding(0) var<storage,read_write> data:u32;"
 +"@group(0) @binding(1) var<storage,read_write> output:array<u32>;@compute @workgroup_size(1) fn main(){"+body+"}");
 Save("combined-"+alias,SpirvBinary.Parse(SpirvWriter.Write(module)),[9],[9,10]);
}
string cooperative=(string)typeof(CooperativeMatrixTests).GetField("Source",BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
string functionSource="struct Data{x:u32,y:u32,}@group(0) @binding(0) var<storage,read> input:array<u32>;"
 +"@group(0) @binding(1) var<storage,read_write> output:array<u32>;@compute @workgroup_size(1) fn main(){var local:Data;local.x=input[0];output[0]=local.x;}";
Save("function-volatile",(SpirvBinary)factory.Invoke(null,[functionSource,"Data",0u,21u])!,[9],[9],rejection:"storage-buffer root");
var aggregate=typeof(MemberMemoryTests).GetMethod("AggregateFixture",BindingFlags.Static|BindingFlags.NonPublic)!;
foreach(uint decoration in new uint[]{21,23})foreach(int mode in new[]{0,1,2,3})
 Save($"aggregate-{decoration}-{mode}",(SpirvBinary)aggregate.Invoke(null,[mode,decoration])!,[0,0,0],[mode==3?9u:10u,7,99]);
foreach(string attributes in new[]{"@coherent","@coherent @volatile"}){
 var module=WgslReader.Parse(cooperative.Replace("@group(0) @binding(0)",attributes+" @group(0) @binding(0)"));
 var binary=SpirvBinary.Parse(SpirvWriter.Write(module));string name=attributes=="@coherent"?"coop-coherent":"coop-combined";
 File.WriteAllBytes($"{root}/{name}-vulkan.spv",binary.ToBytes());cases.Add(new{name,values=Array.Empty<uint>(),expected=Array.Empty<uint>(),strong=false,coop=true});
}
File.WriteAllText(root+"/fixtures.json",JsonSerializer.Serialize(cases,new JsonSerializerOptions{WriteIndented=true}));
