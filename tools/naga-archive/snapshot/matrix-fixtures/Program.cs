using System.Reflection;
using System.Text.Json;
using Sia.Spirv.Naga.Spirv;

var method = typeof(Sia.Spirv.Naga.Tests.NativeMatrixLayoutTests).GetMethod("Fixture", BindingFlags.Static | BindingFlags.NonPublic)!;
var root = Path.GetFullPath(".work/naga-csharp/matrix-layout");
Directory.CreateDirectory(root);
var cases = new List<object>();
void Export(int columns, int rows, bool rowMajor, string operation, string kind = "direct", bool uniform = false, bool half = false, uint memoryFlags = 0)
{
    uint stride = 32;
    string name = $"{columns}x{rows}-{stride}-{rowMajor}-{operation}-{kind}-{uniform}-{half}-{memoryFlags}";
    var binary = (SpirvBinary)method.Invoke(null, [columns, rows, stride, rowMajor, operation, kind, uniform, half, memoryFlags])!;
    File.WriteAllBytes(Path.Combine(root, name + ".spv"), binary.ToBytes());
    cases.Add(new { name, columns, rows, stride, rowMajor, operation, kind, uniform, half, memoryFlags });
}
foreach (int columns in Enumerable.Range(2, 3))
foreach (int rows in Enumerable.Range(2, 3))
foreach (bool rowMajor in new[] { false, true })
foreach (bool half in new[] { false, true }) Export(columns, rows, rowMajor, "matrix", half: half);
foreach (bool rowMajor in new[] { false, true })
{
    foreach (string operation in new[] { "component", "column", "copy" }) Export(3, 2, rowMajor, operation);
    foreach (bool half in new[] { false, true })
    {
        Export(3, 2, rowMajor, "matrix", uniform: true, half: half);
        Export(2, 3, rowMajor, "column", memoryFlags: 7, half: half);
        Export(3, 2, rowMajor, "matrix", memoryFlags: 7, uniform: true, half: half);
        foreach (string kind in new[] { "array", "nested" })
        {
            Export(2, 3, rowMajor, "matrix", kind, half: half);
            Export(3, 2, rowMajor, "root", kind, half: half);
            Export(2, 3, rowMajor, "matrix", kind, uniform: true, half: half);
        }
        Export(2, 3, rowMajor, "swap", "array", half: half);
    }
}
File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
