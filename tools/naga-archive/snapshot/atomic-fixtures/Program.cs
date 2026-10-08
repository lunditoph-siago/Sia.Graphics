using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Tests;
var factory = typeof(AtomicMemoryTests).GetMethod("Fixture", BindingFlags.Static | BindingFlags.NonPublic)!;
var operationType = factory.GetParameters()[0].ParameterType;
var root = ".work/naga-csharp/atomic-memory"; Directory.CreateDirectory(root);
var cases = new List<object>();
void Export(string operation, uint scope, uint semantics, uint unequal, bool vulkan) {
    string name = $"native-{operation}-{scope}-{semantics}-{unequal}-{vulkan}";
    var binary = (SpirvBinary)factory.Invoke(null, [Enum.Parse(operationType, operation), scope, semantics, unequal, vulkan])!;
    File.WriteAllBytes($"{root}/{name}.spv", binary.ToBytes());
    cases.Add(new { name, operation, scope, semantics, unequal, vulkan });
}
foreach (bool vulkan in new[] { false, true }) foreach (uint scope in new uint[] { 1, 2, 3, 4, 5 }) {
    if (scope == 5 && !vulkan) continue;
    Export("AtomicIAdd", scope, 0, 0, vulkan);
}
foreach (string operation in new[] { "AtomicLoad", "AtomicStore", "AtomicExchange", "AtomicIAdd", "AtomicISub", "AtomicUMin", "AtomicUMax", "AtomicAnd", "AtomicOr", "AtomicXor", "AtomicIIncrement", "AtomicIDecrement", "AtomicCompareExchange" }) {
    uint semantics = operation == "AtomicLoad" ? 66u : operation == "AtomicStore" ? 68u : 72u;
    Export(operation, 1, semantics, operation == "AtomicCompareExchange" ? 66u : 0u, false);
}
Export("AtomicIAdd", 1, 80, 0, false);
Export("AtomicIAdd", 1, 32840, 0, true);
Export("AtomicIAdd", 1, 8260, 0, true);
Export("AtomicCompareExchange", 1, 57416, 49218, true);
Export("AtomicCompareExchange", 1, 0, 0, false);
File.WriteAllText($"{root}/fixtures.json", JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
var imageFactory = typeof(ImageAtomicTests).GetMethod("MemoryFixture", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (var (scope, semantics, vulkan) in new[] { (3u, 0u, false), (1u, 2056u, false), (5u, 0u, true) })
    File.WriteAllBytes($"{root}/native-image-{scope}-{semantics}-{vulkan}.spv", ((SpirvBinary)imageFactory.Invoke(null, [scope, semantics, vulkan])!).ToBytes());
var aggregateFactory = typeof(AtomicMemoryTests).GetMethod("AggregateFixture", BindingFlags.Static | BindingFlags.NonPublic)!;
for (int mode = 0; mode < 4; mode++)
    File.WriteAllBytes($"{root}/native-aggregate-{mode}.spv", ((SpirvBinary)aggregateFactory.Invoke(null, [mode])!).ToBytes());
