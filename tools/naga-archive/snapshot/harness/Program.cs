#nullable enable
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Back;
try { if (args.Length is 2 or 3 or 4) {
    var module = args[0].EndsWith(".spv") ? SpirvReader.Parse(File.ReadAllBytes(args[0])) : WgslReader.Parse(File.ReadAllText(args[0]));
    Sia.Spirv.Naga.Valid.ModuleValidator.Validate(module);
    System.Collections.Generic.Dictionary<string, double>? values = args.Length >= 3 && args[2] != "unresolved" ? args[2].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(v => v.Split('=')).ToDictionary(v => v[0], v => double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture)) : null;
    if (args[1].EndsWith(".spv")) File.WriteAllBytes(args[1], SpirvWriter.Write(module, new() { PipelineConstants = values, UseLocalSizeId = args.Length == 4 && args[3] == "local-size-id" }));
    else if (values is not null) File.WriteAllText(args[1], WgslWriter.Write(Sia.Spirv.Naga.Proc.PipelineConstantResolver.Resolve(module, values)));
    else File.WriteAllText(args[1], WgslWriter.Write(module));
} else {
    throw new ArgumentException("input.spv output.wgsl required");
} } catch (Exception error) {
    Console.WriteLine(error.ToString());
    if (error is Sia.Spirv.Naga.ShaderException e) {
        Console.WriteLine($"Span: {e.Diagnostic.Span.Start}:{e.Diagnostic.Span.Length}");
        if (!args[0].EndsWith(".spv")) {
            string source = File.ReadAllText(args[0]); int start = e.Diagnostic.Span.Start;
            Console.WriteLine(source.Substring(System.Math.Max(0, start - 60), System.Math.Min(source.Length - System.Math.Max(0, start - 60), 150)));
        }
    }
    return 1;
}
return 0;
