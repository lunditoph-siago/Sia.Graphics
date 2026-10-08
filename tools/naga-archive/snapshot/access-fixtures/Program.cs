using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Tests;
using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
string root=".work/naga-csharp/access-memory";Directory.CreateDirectory(root);
var ordinary=typeof(MemoryAccessTests).GetMethod("Fixture",BindingFlags.Static|BindingFlags.NonPublic)!;
var aggregate=typeof(AtomicMemoryTests).GetMethod("AggregateFixture",BindingFlags.Static|BindingFlags.NonPublic)!;
var operands=typeof(MemoryAccessTests).GetMethod("Operands",BindingFlags.Static|BindingFlags.NonPublic)!;
uint[] Args(uint flags,uint scope=15)=>(uint[])operands.Invoke(null,[flags,scope])!;
var cases=new List<object>();
void Save(string name,SpirvBinary input,bool vulkan,bool copy,string rejection=null,bool coop=false,bool aggr=false){
 File.WriteAllBytes($"{root}/{name}.spv",input.ToBytes());cases.Add(new{name,vulkan,copy,rejection,coop,aggr});
}
void Export(uint load,uint store,bool vulkan=false,bool copy=false,bool separate=false,uint scope=5){
 var binary=(SpirvBinary)ordinary.Invoke(null,[load,store,vulkan,copy,separate,scope])!;
 Save($"access-{load}-{store}-{vulkan}-{copy}-{separate}-{scope}",binary,vulkan,copy,((load|store)&24u)!=0&&scope!=5?"availability/visibility":null);
}
foreach(uint flags in new uint[]{0,1,2,4,7})Export(flags,flags);
Export(32,32,true);Export(48,40,true);Export(55,47,true);
foreach(uint scope in new uint[]{1,2,3,4})Export(55,47,true,scope:scope);
foreach(uint flags in new uint[]{1,7,32,56})Export(flags,flags,flags>=32,copy:true);
Export(48,40,true,true,true);Export(55,47,true,true,true);Export(0,1,true,true,true);Export(1,0,true,true,true);
foreach(int mode in new[]{0,1,2,3}){
 var input=(SpirvBinary)aggregate.Invoke(null,[mode])!;
 var code=input.Instructions.Select(i=>i.Opcode is 61 or 62 or 63?new SpirvInstruction(i.Opcode,[..i.Operands,3u,4u]):i).ToArray();
 Save("aggregate-"+mode,new(){Bound=input.Bound,Instructions=code},false,false,aggr:true);
}
string cooperative=(string)typeof(CooperativeMatrixTests).GetField("Source",BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
foreach(uint flags in new uint[]{0,1,7,32,48}){
 var input=SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(cooperative));
 uint scope=input.Instructions.First(i=>i.Opcode==43&&i.Operands[2]==3).Operands[1];
 var code=input.Instructions.Select(i=>i.Opcode==4457?new SpirvInstruction(i.Opcode,[..i.Operands,..Args(flags,scope)])
  :i.Opcode==4458?new SpirvInstruction(i.Opcode,[..i.Operands,..Args((flags&~16u)|((flags&16)!=0?8u:0u),scope)]):i).ToArray();
 Save("coop-"+flags,new(){Version=input.Version,Bound=input.Bound,Instructions=code},true,false,(flags&16)!=0?"availability/visibility":null,coop:true);
}
foreach(bool atomic in new[]{false,true}){
 string source=atomic?"@volatile @group(0) @binding(0) var<storage,read_write> data:atomic<u32>; @group(0) @binding(1) var<storage,read_write> output:array<u32>; @compute @workgroup_size(1) fn main(){output[0]=atomicAdd(&data,1u);output[1]=atomicLoad(&data);}":
  "@volatile @group(0) @binding(0) var<storage,read_write> data:u32; @group(0) @binding(1) var<storage,read_write> output:array<u32>; @compute @workgroup_size(1) fn main(){output[0]=data;data=data+1u;output[1]=data;}";
 var module=WgslReader.Parse(source);module.VulkanMemoryModel=true;
 Save(atomic?"global-volatile-atomic":"global-volatile-ordinary",SpirvBinary.Parse(SpirvWriter.Write(module)),true,false);
}
foreach(bool vulkan in new[]{false,true}){
 var factory=typeof(AtomicMemoryTests).GetMethod("Fixture",BindingFlags.Static|BindingFlags.NonPublic)!;
 object operation=Enum.Parse(factory.GetParameters()[0].ParameterType,"AtomicCompareExchange");
 var input=(SpirvBinary)factory.Invoke(null,[operation,1u,vulkan?32768u:0u,vulkan?32768u:0u,vulkan])!;
 var code=input.Instructions.ToList();if(!vulkan)code.Insert(code.FindIndex(i=>i.Opcode==19),new(71,[20u,21u]));
 Save(vulkan?"strong-volatile-vulkan":"strong-volatile-legacy",new(){Bound=input.Bound,Instructions=code},vulkan,false,"cannot be duplicated");
}
foreach(uint decoration in new uint[]{21,23})foreach(bool member in new[]{false,true}){
 var input=(SpirvBinary)ordinary.Invoke(null,[0u,0u,true,false,false,5u])!;
 var code=input.Instructions.ToList();code.Insert(code.FindIndex(i=>i.Opcode==19),member?new(72,[6u,0u,decoration]):new(71,[20u,decoration]));
 string name=$"invalid-vulkan-decoration-{decoration}-{member}";
 File.WriteAllBytes($"{root}/{name}.spv",new SpirvBinary{Bound=input.Bound,Instructions=code}.ToBytes());cases.Add(new{name,invalid=true});
}
File.WriteAllText(root+"/fixtures.json",JsonSerializer.Serialize(cases,new JsonSerializerOptions{WriteIndented=true}));
