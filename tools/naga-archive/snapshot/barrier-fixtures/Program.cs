using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Tests;
using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
var factory = typeof(BarrierMemoryTests).GetMethod("Fixture", BindingFlags.Static | BindingFlags.NonPublic)!;
string root = ".work/naga-csharp/barrier-memory"; Directory.CreateDirectory(root);
var cases = new List<object>();
void Export(bool control, uint execution, uint scope, uint semantics, bool vulkan = false, uint size = 4, bool conditional = false, string rejection = null) {
    string name = $"native-{control}-{execution}-{scope}-{semantics}-{vulkan}-{size}-{conditional}";
    var binary = (SpirvBinary)factory.Invoke(null, [control, execution, scope, semantics, vulkan, size, conditional])!;
    File.WriteAllBytes($"{root}/{name}.spv", binary.ToBytes());
    cases.Add(new { name, control, execution, scope, semantics, vulkan, size, conditional, rejection });
}
Export(true,2,2,264);Export(true,2,1,264);Export(true,2,3,264);Export(true,3,3,264);
Export(true,3,2,264,rejection:"no equivalent WGSL subgroupBarrier");
Export(true,2,1,72,rejection:"cannot be narrowed");
Export(true,2,2,72);Export(true,2,2,2056);Export(true,2,2,66);Export(true,2,2,68);Export(true,2,4,0);
Export(true,2,5,264,true);Export(true,2,1,24648,true,rejection:"no equivalent WGSL synchronization builtin");
Export(true,2,2,272,rejection:"no equivalent WGSL synchronization builtin");
foreach (uint scope in new uint[] { 1,2,3,4,5 }) Export(false,0,scope,scope is 1 or 5?72u:264u,scope==5,rejection:"uniformity-proven");
Export(false,0,1,24648,true,rejection:"uniformity-proven");
Export(false,0,2,264,size:1);Export(false,0,2,264,conditional:true,rejection:"uniformity-proven");
Export(false,0,2,264,size:1,conditional:true);Export(false,0,1,72,size:1,rejection:"cannot be narrowed");
Export(false,0,1,264,size:1);Export(false,0,4,72,size:1);
File.WriteAllText($"{root}/fixtures.json", JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
foreach (bool vertex in new[] { false, true }) {
    var module = WgslReader.Parse(vertex ? "@vertex fn main()->@builtin(position) vec4f{return vec4f();}" : "@fragment fn main(){}");
    module.Functions[0].Body.Statements.Insert(0, new Statement.MemoryBarrier(true, false) { NativeMemory = new(1, 72) });
    File.WriteAllBytes($"{root}/{(vertex ? "vertex" : "fragment")}-fence.spv", SpirvWriter.Write(module));
}
