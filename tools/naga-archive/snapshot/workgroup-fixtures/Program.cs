using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Tests;
using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Module = Sia.Spirv.Naga.IR.Module;
string root=".work/naga-csharp/workgroup-memory";Directory.CreateDirectory(root);
var cases=new List<object>();
Module Fixture(string name,params object[] args)=>(Module)typeof(WorkgroupMemoryTests).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,args)!;
void Save(string name,Module module,bool gpu,bool volatileAccess){
 module.VulkanMemoryModel=false;
 byte[] original=SpirvWriter.Write(module);File.WriteAllBytes($"{root}/{name}.spv",original);
 var imported=SpirvReader.Parse(original);imported.VulkanMemoryModel=true;
 File.WriteAllBytes($"{root}/{name}-converted.spv",SpirvWriter.Write(imported));
 module.VulkanMemoryModel=true;File.WriteAllBytes($"{root}/{name}-direct.spv",SpirvWriter.Write(module));
 cases.Add(new{name,gpu,volatileAccess});
}
foreach(string kind in new[]{"scalar","alias","nested","specialized","atomic","multi","rawmember","rawarray"})foreach(var memory in new[]{MemoryDecorations.None,MemoryDecorations.Coherent,MemoryDecorations.Volatile,MemoryDecorations.Coherent|MemoryDecorations.Volatile})
 Save($"{kind}-{(int)memory}",Fixture("Fixture",kind,memory),true,(memory&MemoryDecorations.Volatile)!=0);
foreach(var memory in new[]{MemoryDecorations.None,MemoryDecorations.Coherent,MemoryDecorations.Volatile})Save($"mesh-{(int)memory}",Fixture("MeshFixture",memory),false,memory==MemoryDecorations.Volatile);
foreach(bool atomic in new[]{false,true})Save($"task-{atomic}",Fixture("TaskFixture",atomic,MemoryDecorations.Volatile),false,true);
File.WriteAllText(root+"/fixtures.json",JsonSerializer.Serialize(cases,new JsonSerializerOptions{WriteIndented=true}));
