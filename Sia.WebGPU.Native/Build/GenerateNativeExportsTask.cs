using Microsoft.Build.Framework;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;

public sealed class GenerateNativeExportsTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string Header { get; set; }

    [Required]
    public string OutputDirectory { get; set; }

    public override bool Execute()
    {
        try {
            Directory.CreateDirectory(OutputDirectory);
            var symbols = Regex.Matches(File.ReadAllText(Header), @"(?m)^WGPU_EXPORT[^;\r\n]*\b(wgpu[A-Z]\w*)\(")
                .Cast<Match>().Select(match => match.Groups[1].Value).ToArray();
            if (symbols.Length < 200) throw new InvalidDataException("Incomplete native C API.");
            var definition = "EXPORTS\n" + string.Join("\n", symbols) + "\n";
            var source = "#include <dawn/webgpu.h>\nextern \"C\" {\nvoid (*sia_webgpu_exports[])() = {\n" +
                string.Join("\n", symbols.Select(symbol => "reinterpret_cast<void(*)()>(&" + symbol + "),")) +
                "\n};\n}\n";
            foreach (var pair in new[] { new[] { "Native.def", definition }, new[] { "NativeExports.cpp", source } }) {
                var path = Path.Combine(OutputDirectory, pair[0]);
                if (!File.Exists(path) || File.ReadAllText(path) != pair[1]) File.WriteAllText(path, pair[1]);
            }
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }
}
