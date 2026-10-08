using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Spirv;
var type = typeof(Sia.Spirv.Naga.Tests.MatrixHelperTests);
var root = Path.GetFullPath(".work/naga-csharp/matrix-helpers");Directory.CreateDirectory(root);
var cases = new List<object>();
File.WriteAllText(Path.Combine(root, "private.wgsl"), (string)type.GetField("PrivateSource", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!);
foreach (bool rowMajor in new[] { false, true })
foreach (string kind in new[] { "helper", "init", "zero" })
{
    string name = kind + "-" + rowMajor;
    var method = type.GetMethod(kind == "helper" ? "HelperFixture" : "InitializerFixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var binary = (SpirvBinary)method.Invoke(null, kind == "helper" ? [rowMajor] : [rowMajor, kind == "zero"])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, kind, rowMajor });
}
foreach (bool rowMajor in new[] { false, true })
foreach (bool workgroup in new[] { false, true })
{
    string name = "whole-" + rowMajor + "-" + workgroup;
    var method = type.GetMethod("WholeHelperFixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var binary = (SpirvBinary)method.Invoke(null, [rowMajor, workgroup])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, kind = "whole", rowMajor, workgroup });
}
foreach (bool rowMajor in new[] { false, true })
foreach (bool zero in new[] { false, true })
{
    string name = "array-" + rowMajor + "-" + zero;
    var method = type.GetMethod("ArrayInitializerFixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var binary = (SpirvBinary)method.Invoke(null, [rowMajor, zero])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, kind = "array", rowMajor, zero });
}
File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
foreach (string kind in new[] { "select", "phi", "load", "return" })
{
    var method = type.GetMethod("PointerProducerFixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var binary = (SpirvBinary)method.Invoke(null, [kind])!;
    File.WriteAllBytes(Path.Combine(root, "pointer-producer-" + kind + ".spv"), binary.ToBytes());
}
foreach (bool rowMajor in new[] { false, true })
foreach (string kind in new[] { "shared", "evaluation" })
{
    string name = kind + "-" + rowMajor;
    var method = type.GetMethod(kind == "shared" ? "SharedInitializerFixture" : "EvaluationFixture", BindingFlags.NonPublic | BindingFlags.Static)!;
    var binary = (SpirvBinary)method.Invoke(null, [rowMajor])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, kind, rowMajor });
}
File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
