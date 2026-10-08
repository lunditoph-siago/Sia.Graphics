using System.Reflection;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Tests;
var factory = typeof(MeshShaderTests).GetMethod("AggregateFixture", BindingFlags.Static | BindingFlags.NonPublic)!;
for (int mode = 0; mode < 4; mode++)
    File.WriteAllBytes($".work/naga-csharp/mesh/native-aggregate-{mode}.spv", ((SpirvBinary)factory.Invoke(null, [mode])!).ToBytes());
var fragment = typeof(MeshShaderTests).GetMethod("FragmentBlockFixture", BindingFlags.Static | BindingFlags.NonPublic)!;
File.WriteAllBytes(".work/naga-csharp/mesh/native-fragment-block.spv", ((SpirvBinary)fragment.Invoke(null, [])!).ToBytes());
var task = typeof(MeshNativeControlFlowTests).GetMethod("TaskFixture", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (bool payload in new[] { false, true }) foreach (uint selector in new uint[] { 0, 1 }) foreach (bool continuing in new[] { false, true })
    File.WriteAllBytes($".work/naga-csharp/mesh/native-task-helper-{payload}-{selector}-{continuing}.spv", ((SpirvBinary)task.Invoke(null, [payload, selector, continuing])!).ToBytes());
var mesh = typeof(MeshNativeControlFlowTests).GetMethod("MeshFixture", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (bool shared in new[] { false, true })
    File.WriteAllBytes($".work/naga-csharp/mesh/native-mesh-helper-{shared}.spv", ((SpirvBinary)mesh.Invoke(null, [shared])!).ToBytes());
File.WriteAllText(".work/naga-csharp/mesh/payload-atomic-task.wgsl", (string)typeof(TaskPayloadTests).GetField("AtomicSource", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
File.WriteAllText(".work/naga-csharp/mesh/payload-atomic-mesh.wgsl", (string)typeof(TaskPayloadTests).GetProperty("MeshAtomicSource", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);
