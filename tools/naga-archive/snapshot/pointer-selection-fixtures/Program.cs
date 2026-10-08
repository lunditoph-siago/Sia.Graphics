using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Front;
if (args.Contains("identities"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-descriptor-identity"); Directory.CreateDirectory(directory);
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerDescriptorIdentityTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    void Save(string kind, string mode, string index, bool full, bool comparison, bool slot)
    {
        string name = kind + "-" + mode + "-" + index + "-" + full + "-" + comparison + "-" + slot;
        var original = (SpirvBinary)fixture.Invoke(null, [kind, mode, index, full, comparison, slot])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var normalized = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), normalized.ToBytes());
        fixtures.Add(new { name, kind, mode, index, full, comparison, privateSlot = slot, iterations = mode == "loop" ? 3 : 1 });
    }
    foreach (string kind in new[] { "descriptor", "descriptor-atomic" })
    foreach (string mode in new[] { "select", "phi", "slot", "loop", "return", "nested" })
    {
        foreach (bool full in new[] { false, true }) foreach (bool comparison in new[] { false, true })
        {
            Save(kind, mode, "shared", full, comparison, false);
            if (mode is "slot" or "loop") Save(kind, mode, "shared", full, comparison, true);
        }
        foreach (bool comparison in new[] { false, true }) Save(kind, mode, "copy", false, comparison, false);
        Save(kind, mode, "spec-derived", false, true, false);
        if (mode is "slot" or "loop") Save(kind, mode, "spec-derived", false, true, true);
        if (mode is "select" or "nested") Save(kind, mode, "spec", false, true, false);
    }
    foreach (string mode in new[] { "select", "phi", "loop", "nested" })
        File.WriteAllBytes(Path.Combine(directory, "independent-" + mode + ".spv"), ((SpirvBinary)fixture.Invoke(null, ["descriptor", mode, "independent", true, true, false])!).ToBytes());
    foreach (bool slot in new[] { false, true })
        File.WriteAllBytes(Path.Combine(directory, "temporal-" + slot + ".spv"), ((SpirvBinary)typeof(Sia.Spirv.Naga.Tests.PointerDescriptorIdentityTests).GetMethod("TemporalFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [slot])!).ToBytes());
    File.WriteAllBytes(Path.Combine(directory, "repeated-call.spv"), ((SpirvBinary)typeof(Sia.Spirv.Naga.Tests.PointerDescriptorIdentityTests).GetMethod("RepeatedCallFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).ToBytes());
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("arithmetic"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-arithmetic"); Directory.CreateDirectory(directory);
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerArithmeticTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    void Save(string kind, string mode, string offset, int width, bool slot)
    {
        string name = kind + "-" + mode + "-" + offset + "-" + width + "-" + slot;
        var original = (SpirvBinary)fixture.Invoke(null, [kind, mode, offset, width, slot])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var normalized = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), normalized.ToBytes());
        fixtures.Add(new { name, kind, mode, offset, width, privateSlot = slot, iterations = mode == "loop" ? 3 : 1 });
    }
    foreach (string kind in new[] { "scalar", "atomic" })
    foreach (string mode in new[] { "select", "phi", "slot", "loop", "return", "nested" })
    foreach (string offset in new[] { "forward", "backward", "chained", "selected", "onepast" })
        Save(kind, mode, offset, 32, mode is "slot" or "loop");
    foreach (int width in new[] { 16, -16, -32, 64, -64 })
    foreach (string offset in new[] { "forward", "backward", "chained", "selected", "onepast" })
        Save("scalar", "nested", offset, width, false);
    foreach (string offset in new[] { "forward", "backward" })
    foreach (int width in new[] { 32, -16 })
    {
        string name = "aggregate-" + offset + "-" + width;
        var original = (SpirvBinary)typeof(Sia.Spirv.Naga.Tests.PointerArithmeticTests).GetMethod("AggregateFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [offset, width])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var normalized = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), normalized.ToBytes());
        fixtures.Add(new { name, kind = "aggregate", mode = "select", offset, width, privateSlot = false, iterations = 1 });
    }
    foreach (string kind in new[] { "direct", "constants" })
    {
        string offset = kind == "direct" ? "forward" : "selected", name = kind + "-" + offset + "-32";
        var original = (SpirvBinary)typeof(Sia.Spirv.Naga.Tests.PointerArithmeticTests).GetMethod(kind == "direct" ? "DirectFixture" : "ConstantSourcesFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var normalized = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), normalized.ToBytes());
        fixtures.Add(new { name, kind, mode = "select", offset, width = kind == "direct" ? 32 : -16, privateSlot = false, iterations = 1 });
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("descriptors") || args.Contains("dynamic-descriptors"))
{
    bool dynamicDescriptors = args.Contains("dynamic-descriptors");
    string directory = Path.GetFullPath(dynamicDescriptors ? ".work/naga-csharp/pointer-descriptor-array-dynamic" : ".work/naga-csharp/pointer-descriptor-array"); Directory.CreateDirectory(directory);
    var fixtures = new List<object>();
    var selection = typeof(Sia.Spirv.Naga.Tests.PointerDescriptorArrayTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var comparison = typeof(Sia.Spirv.Naga.Tests.PointerComparisonTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    void Save(SpirvBinary original, string kind, string mode, bool slot, string operation)
    {
        string name = kind + "-" + mode + "-" + slot + "-" + operation;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, kind, mode, privateSlot = slot, operation, iterations = mode == "loop" ? 3 : 1 });
    }
    foreach (string kind in dynamicDescriptors ? new[] { "descriptor-dynamic", "descriptor-dynamic-atomic" } : new[] { "descriptor", "descriptor-same", "descriptor-atomic" })
    foreach (string mode in new[] { "select", "phi", "slot", "loop", "return", "nested" })
    foreach (bool slot in mode is "slot" or "loop" ? new[] { false, true } : new[] { false })
        Save((SpirvBinary)selection.Invoke(null, [kind, mode, slot])!, kind, mode, slot, "memory");
    foreach (string kind in dynamicDescriptors ? Array.Empty<string>() : new[] { "descriptor-same", "descriptor" })
    foreach (string mode in new[] { "select", "phi", "slot", "loop", "return", "nested" })
    foreach (bool slot in mode is "slot" or "loop" ? new[] { false, true } : new[] { false })
        Save((SpirvBinary)comparison.Invoke(null, [kind, mode, kind == "descriptor" ? "self" : "other", 32, slot, 32])!, kind, mode, slot, "comparison");
    foreach (string mode in dynamicDescriptors ? Array.Empty<string>() : new[] { "select", "loop", "nested" })
    {
        var original = (SpirvBinary)comparison.Invoke(null, ["descriptor", mode, "different-field", 32, false, 32])!;
        File.WriteAllBytes(Path.Combine(directory, "alias-" + mode + ".spv"), original.ToBytes());
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    if (!dynamicDescriptors)
        File.WriteAllBytes(Path.Combine(directory, "alias-difference.spv"), ((SpirvBinary)typeof(Sia.Spirv.Naga.Tests.PointerComparisonTests)
            .GetMethod("CrossBindingDifferenceFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).ToBytes());
    return;
}
if (args.Contains("comparisons"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-comparison"); Directory.CreateDirectory(directory);
    var comparisonCases = new List<(string Kind, string Mode, string Relation, int IndexWidth, bool Private, int ResultWidth)>();
    foreach (string kind in new[] { "scalar", "atomic", "workgroup" })
    foreach (string mode in new[] { "select", "phi", "slot", "loop", "return", "nested" })
    foreach (string relation in new[] { "other", "constant", "self" })
    foreach (bool privateSlot in mode is "slot" or "loop" ? new[] { false, true } : new[] { false }) comparisonCases.Add((kind, mode, relation, 32, privateSlot, 32));
    foreach (string kind in new[] { "scalar", "atomic", "workgroup" }) comparisonCases.Add((kind, "select", "different-field", 32, false, 32));
    foreach (string mode in new[] { "select", "nested" }) comparisonCases.Add(("workgroup", mode, "cross-workgroup", 32, false, 32));
    foreach (string mode in new[] { "select", "loop" })
    {
        comparisonCases.Add(("scalar", mode, "self-cross", 32, false, 32)); comparisonCases.Add(("vector", mode, "other", 32, false, 32));
    }
    foreach (int width in new[] { 16, -16, -32, 64, -64 }) comparisonCases.Add(("scalar", "loop", "other", width, true, width));
    comparisonCases.Add(("scalar", "nested", "constant", 64, false, 16));
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerComparisonTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var item in comparisonCases)
    {
        string name = item.Kind + "-" + item.Mode + "-" + item.Relation + "-" + item.IndexWidth + "-" + item.Private + "-" + item.ResultWidth;
        var original = (SpirvBinary)fixture.Invoke(null, [item.Kind, item.Mode, item.Relation, item.IndexWidth, item.Private, item.ResultWidth])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, kind = item.Kind, mode = item.Mode, relation = item.Relation, indexWidth = item.IndexWidth, privateSlot = item.Private, resultWidth = item.ResultWidth, iterations = item.Mode == "loop" ? 3 : 1 });
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("nulls"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-null"); Directory.CreateDirectory(directory);
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerNullTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (string kind in new[] { "scalar", "array", "atomic", "workgroup", "vector" })
    foreach (string mode in new[] { "select", "phi", "slot", "loop", "return", "nested" })
    foreach (bool allNull in new[] { false, true })
    foreach (bool privateSlot in mode is "slot" or "loop" ? new[] { false, true } : new[] { false })
    {
        bool equality = mode is "phi" or "loop" or "return";
        string name = kind + "-" + mode + "-" + allNull + "-" + privateSlot;
        var original = (SpirvBinary)fixture.Invoke(null, [kind, mode, allNull, equality, privateSlot])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, kind, mode, allNull, equality, privateSlot, iterations = mode == "loop" ? 3 : 1 });
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("returns"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-return"); Directory.CreateDirectory(directory);
    var returnCases = new List<(string Kind, string Mode, bool Slot, bool Nested, bool Early, bool Private, bool Qualified, uint Iterations, uint Version)>();
    foreach (string kind in new[] { "scalar", "array", "atomic", "mixed-struct", "workgroup" })
    foreach (bool nested in new[] { false, true })
    {
        foreach (bool early in new[] { false, true }) returnCases.Add((kind, "select", false, nested, early, false, kind != "workgroup", 1, 0));
        foreach (bool privateSlot in new[] { false, true })
        {
            returnCases.Add((kind, "local", true, nested, false, privateSlot, kind != "workgroup", 1, 0));
            returnCases.Add((kind, "swap", true, nested, false, privateSlot, kind != "workgroup", 3, 0));
        }
    }
    foreach (bool nested in new[] { false, true })
    foreach (bool early in new[] { false, true }) returnCases.Add(("cross", "select", false, nested, early, false, false, 1, 0));
    foreach (uint version in new[] { 0x10400u, 0x10500u }) returnCases.Add(("scalar", "swap", true, true, false, true, true, 4, version));
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerReturnTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var item in returnCases)
    {
        string name = item.Kind + "-" + item.Mode + "-" + item.Slot + "-" + item.Nested + "-" + item.Early + "-" + item.Private + "-" + item.Version;
        var original = (SpirvBinary)fixture.Invoke(null, [item.Kind, item.Mode, item.Slot, item.Nested, item.Early, item.Private, item.Qualified, item.Iterations, item.Version])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, kind = item.Kind, mode = item.Mode, slot = item.Slot, nested = item.Nested, early = item.Early, privateSlot = item.Private, qualified = item.Qualified, iterations = item.Iterations });
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("cross-slot"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-cross-buffer"); Directory.CreateDirectory(directory);
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerCrossBufferSlotTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var crossSlotCases = new List<(string Mode, string Transfer, bool Private, bool Qualified, uint Iterations, uint Version)>();
    foreach (bool privateSlot in new[] { false, true })
    {
        foreach (string transfer in new[] { "copy", "cycle", "cross", "helper-copy", "helper-store", "nested-store", "helper-edit" })
        {
            crossSlotCases.Add(("local", transfer, privateSlot, true, 1, 0));
            if (transfer != "helper-edit") crossSlotCases.Add(("swap", transfer, privateSlot, true, 3, 0));
        }
        foreach (string transfer in new[] { "copy", "nested-store" }) crossSlotCases.Add(("nested", transfer, privateSlot, false, 1, 0));
        crossSlotCases.Add(("local", "helper-store", privateSlot, true, 1, 0x10400));
        crossSlotCases.Add(("swap", "nested-store", privateSlot, false, 4, 0x10500));
    }
    foreach (var item in crossSlotCases)
    {
        string name = item.Mode + "-" + item.Transfer + "-" + item.Private + "-" + item.Version;
        var original = (SpirvBinary)fixture.Invoke(null, [item.Mode, item.Transfer, item.Private, item.Qualified, item.Iterations, item.Version, false])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), original.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [original])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, mode = item.Mode, transfer = item.Transfer, privateSlot = item.Private, qualified = item.Qualified, iterations = item.Iterations });
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("transfer"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-slot-transfer"); Directory.CreateDirectory(directory);
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerSlotTransferTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var transferCases = new List<(string Kind, string Mode, string Transfer, bool Private, bool Qualified, uint Iterations, uint Version, bool Dual)>();
    foreach (string kind in new[] { "scalar", "array", "atomic", "workgroup" })
    foreach (string transfer in new[] { "copy", "cycle", "cross", "helper-copy", "helper-store", "nested-store" })
    foreach (bool privateSlot in new[] { false, true }) transferCases.Add((kind, "swap", transfer, privateSlot, kind != "workgroup", 3, 0, false));
    foreach (bool privateSlot in new[] { false, true })
    {
        transferCases.Add(("scalar", "select", "helper-edit", privateSlot, false, 1, 0, false));
        transferCases.Add(("scalar", "local", "helper-edit", privateSlot, true, 1, 0, false));
        transferCases.Add(("atomic", "select", "helper-edit", privateSlot, true, 1, 0, false));
        foreach (string kind in new[] { "scalar", "mixed-struct" })
        foreach (string transfer in new[] { "helper-copy", "helper-store", "nested-store" }) transferCases.Add((kind, "local", transfer, privateSlot, true, 1, 0, false));
        foreach (string transfer in new[] { "cycle", "helper-store" }) transferCases.Add(("same", "swap", transfer, privateSlot, true, 4, 0, false));
        transferCases.Add(("scalar", "local", "copy", privateSlot, true, 1, 0x10400, true));
    }
    transferCases.Add(("array", "local", "helper-copy", true, true, 1, 0x10400, true));
    transferCases.Add(("scalar", "hybrid", "nested-store", true, true, 1, 0x10500, false));
    foreach (var item in transferCases)
    {
        string name = item.Kind + "-" + item.Mode + "-" + item.Transfer + "-" + item.Private + "-" + item.Version + "-" + item.Dual;
        uint iterations = item.Iterations;
        var producer = (SpirvBinary)fixture.Invoke(null, [item.Kind, item.Mode, item.Transfer, item.Private, item.Qualified, iterations, item.Version, item.Dual])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), producer.ToBytes());
        if (args.Contains("normalized"))
        {
            var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [producer])!;
            File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        }
        fixtures.Add(new { name, kind = item.Kind, mode = item.Mode, transfer = item.Transfer, privateSlot = item.Private, qualified = item.Qualified, iterations, slotVolatile = false });
    }
    foreach (bool privateSlot in new[] { false, true })
    {
        string name = "store-only-" + privateSlot;
        var producer = (SpirvBinary)typeof(Sia.Spirv.Naga.Tests.PointerSlotTransferTests).GetMethod("StoreOnlyFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [privateSlot])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), producer.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [producer])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, kind = "noop", mode = "noop", transfer = "copy", privateSlot, qualified = false, iterations = 0, slotVolatile = false });
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("producers"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-memory"); Directory.CreateDirectory(directory);
    foreach (string kind in new[] { "load", "return" })
    {
        var producer = (SpirvBinary)typeof(Sia.Spirv.Naga.Tests.MatrixHelperTests).GetMethod("PointerProducerFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [kind])!;
        File.WriteAllBytes(Path.Combine(directory, kind + ".spv"), producer.ToBytes());
    }
    return;
}
if (args.Contains("memory"))
{
    string directory = Path.GetFullPath(".work/naga-csharp/pointer-memory"); Directory.CreateDirectory(directory);
    var fixtures = new List<object>();
    var fixture = typeof(Sia.Spirv.Naga.Tests.PointerMemoryTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var memoryCases = new List<(string Kind, string Mode, bool Qualified, bool Private, uint Iterations, bool Volatile)>();
    foreach (string kind in new[] { "scalar", "array", "vector", "mixed-struct", "atomic" })
    foreach (string mode in new[] { "select", "local", "swap" })
    foreach (bool privateSlot in new[] { false, true })
        memoryCases.Add((kind, mode, mode != "select", privateSlot, mode == "swap" ? 3u : 1u, false));
    foreach (bool privateSlot in new[] { false, true })
    {
        memoryCases.Add(("workgroup", "branch", false, privateSlot, 1, false));
        memoryCases.Add(("workgroup", "swap", false, privateSlot, 4, false));
        memoryCases.Add(("same", "swap", true, privateSlot, 3, false));
        memoryCases.Add(("same", "swap", true, privateSlot, 4, false));
        memoryCases.Add(("scalar", "hybrid", true, privateSlot, 1, false));
        memoryCases.Add(("array", "hybrid", false, privateSlot, 1, false));
        memoryCases.Add(("array", "nested", true, privateSlot, 1, false));
        memoryCases.Add(("scalar", "local", false, privateSlot, 1, true));
    }
    foreach (var item in memoryCases)
    {
        string name = item.Kind + "-" + item.Mode + "-" + item.Qualified + "-" + item.Private + "-" + item.Iterations + (item.Volatile ? "-volatile" : "");
        var producer = (SpirvBinary)fixture.Invoke(null, [item.Kind, item.Mode, item.Qualified, item.Private, item.Iterations, item.Volatile, 0u])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), producer.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [producer])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, kind = item.Kind, mode = item.Mode, qualified = item.Qualified, privateSlot = item.Private, iterations = item.Iterations, slotVolatile = item.Volatile });
    }
    foreach (uint version in new[] { 0x10400u, 0x10500u })
    foreach (var item in new (string Kind, string Mode, bool Private, uint Iterations)[] {
        ("scalar", "local", false, 1), ("scalar", "local", true, 1), ("workgroup", "swap", true, 4) })
    {
        string name = item.Kind + "-" + item.Mode + "-v" + version.ToString("x") + "-" + item.Private;
        var producer = (SpirvBinary)fixture.Invoke(null, [item.Kind, item.Mode, false, item.Private, item.Iterations, false, version])!;
        File.WriteAllBytes(Path.Combine(directory, name + ".spv"), producer.ToBytes());
        var intermediate = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [producer])!;
        File.WriteAllBytes(Path.Combine(directory, name + "-normalized.spv"), intermediate.ToBytes());
        fixtures.Add(new { name, kind = item.Kind, mode = item.Mode, qualified = false, privateSlot = item.Private, iterations = item.Iterations, slotVolatile = false, version });
    }
    File.WriteAllText(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("debug"))
{
    var m = SpirvReader.Parse((byte[])typeof(Sia.Spirv.Naga.Tests.PointerSelectionTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, ["helper", false, false])!.GetType().GetMethod("ToBytes")!.Invoke(
            typeof(Sia.Spirv.Naga.Tests.PointerSelectionTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["helper", false, false]), null)!);
    foreach (var f in m.Functions) Dump(f.Body);
    return;
    void Dump(Block b)
    {
        foreach (var s in b.Statements)
        {
            if (s is Statement.Declare d) { Console.WriteLine("DECLARE " + d.Name + " : " + d.Type); if (d.Initializer is not null) Expr(d.Initializer, "  "); }
            foreach (var p in s.GetType().GetProperties())
            {
                if (p.GetValue(s) is Block child) Dump(child);
                else if (p.GetValue(s) is Expression e && s is not Statement.Declare) Expr(e, "  ");
            }
        }
    }
    void Expr(Expression e, string indent)
    {
        Console.WriteLine(indent + e.GetType().Name + " " + (e is Expression.Unary u ? u.Operator : e is Expression.Reference r ? r.Name : "") + " : " + e.Type);
        foreach (var p in e.GetType().GetProperties())
            if (p.GetValue(e) is Expression child) Expr(child, indent + "  ");
            else if (p.GetValue(e) is IReadOnlyList<Expression> children) foreach (var item in children) Expr(item, indent + "  ");
    }
}
var type = typeof(Sia.Spirv.Naga.Tests.PointerSelectionTests);
var root = Path.GetFullPath(".work/naga-csharp/pointer-selection");Directory.CreateDirectory(root);
var method = type.GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
var cases = new List<object>();
foreach (string kind in new[] { "scalar", "array", "workgroup", "helper", "atomic", "mixed", "mixed-array", "mixed-struct", "vector", "cross" })
foreach (bool nested in new[] { false, true })
foreach (bool qualified in kind == "workgroup" ? new[] { false } : new[] { false, true })
{
    string name = kind + "-" + nested + "-" + qualified;
    var binary = (SpirvBinary)method.Invoke(null, [kind, nested, qualified])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, kind, nested, qualified });
}
File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));

root = Path.GetFullPath(".work/naga-csharp/pointer-phi"); Directory.CreateDirectory(root);
method = typeof(Sia.Spirv.Naga.Tests.PointerPhiTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
cases.Clear();
var phiCases = new List<(string Kind, string Mode, bool Qualified, uint Iterations)>();
foreach (string kind in new[] { "scalar", "array", "vector", "mixed-struct", "atomic", "workgroup" })
foreach (bool qualified in kind == "workgroup" ? new[] { false } : new[] { false, true }) phiCases.Add((kind, "branch", qualified, 1));
foreach (string kind in new[] { "scalar", "array", "vector", "mixed-struct" })
foreach (bool qualified in new[] { false, true }) phiCases.Add((kind, "local", qualified, 1));
foreach (string kind in new[] { "scalar", "array" }) phiCases.Add((kind, "nested", true, 1));
foreach (string mode in new[] { "swap", "self" })
foreach (uint iterations in new[] { 3u, 4u }) phiCases.Add(("scalar", mode, true, iterations));
foreach (string kind in new[] { "mixed-array", "mixed-struct", "atomic", "workgroup" })
foreach (uint iterations in new[] { 3u, 4u }) phiCases.Add((kind, "swap", kind != "workgroup", iterations));
phiCases.AddRange([("same", "branch", false, 1), ("same", "local", true, 1),
    ("same", "swap", true, 3), ("same", "swap", true, 4), ("same", "self", true, 4)]);
foreach (var item in phiCases)
{
    string name = item.Kind + "-" + item.Mode + "-" + item.Qualified + "-" + item.Iterations;
    var binary = (SpirvBinary)method.Invoke(null, [item.Kind, item.Mode, item.Qualified, item.Iterations])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, kind = item.Kind, mode = item.Mode, qualified = item.Qualified, iterations = item.Iterations });
    var normalized = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", BindingFlags.NonPublic)!
        .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [binary])!;
    File.WriteAllBytes(Path.Combine(root, name + "-normalized.spv"), normalized.ToBytes());
}
File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));

root = Path.GetFullPath(".work/naga-csharp/readonly-pointer-selection"); Directory.CreateDirectory(root);
method = typeof(Sia.Spirv.Naga.Tests.ReadOnlyPointerSelectionTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!;
cases.Clear();
foreach (string kind in new[] { "scalar", "array", "structure" })
foreach (bool atomicFirst in new[] { false, true })
foreach (bool qualified in new[] { false, true })
{
    string name = kind + "-" + atomicFirst + "-" + qualified;
    var binary = (SpirvBinary)method.Invoke(null, [kind, atomicFirst, qualified])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, kind, atomicFirst, qualified });
}
File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
