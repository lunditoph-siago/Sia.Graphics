using Sia.Spirv.Compiler;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using System.Text.Json;
var input = File.ReadAllBytes(args[0]);
var core = File.ReadAllBytes(args[1]);
Directory.CreateDirectory(args[2]);
var entries = new SpirvFrontend().Analyze(input, core).Kernels;
var compiler = new SpirvCompiler();
if (args.Length > 3 && args[3] == "static") {
    foreach (var kernel in entries) File.WriteAllText(Path.Combine(args[2],kernel.QualifiedName+".diagnostic.ll"),new Sia.Spirv.Compiler.LLVM.LlvmIrEmitter().Emit(args[0],kernel,SpirvKernelAbi.WebGpu).Text);
    compiler.CompileAssembly(new SpirvFileCompilationRequest(args[0], args[2]) { EmitWgsl = false, OptimizationLevel = 3 });
    foreach (var path in Directory.GetFiles(args[2], "*.spv")) {
        File.WriteAllText(Path.ChangeExtension(path,".wgsl"), Sia.Spirv.Compiler.Translation.ShaderTranslator.SpirvToWgsl(File.ReadAllBytes(path), SpirvCompilationTarget.Default));
    }
    return;
}
foreach (var kernel in entries) {
    var name = kernel.DeclaringType.Split('.').Last()+"-"+kernel.Name;
    var request = new SpirvModuleCompilationRequest(input, kernel.MetadataToken, core);
    var module = compiler.CompileModule(request);
    File.WriteAllBytes(Path.Combine(args[2], name+".spv"), SpirvWriter.Write(module, request.Target));
    File.WriteAllText(Path.Combine(args[2], name+".wgsl"), WgslWriter.Write(module, request.Target));
    Console.WriteLine(name);
}
var selectedTarget = SpirvCompilationTarget.Default;
File.WriteAllText(Path.Combine(args[2], "entries.json"), JsonSerializer.Serialize(entries.Select(k => new {
    name=k.DeclaringType.Split('.').Last()+"-"+k.Name, token=k.MetadataToken,
    targetIdentity=selectedTarget.Identity, selectedTarget.Environment, selectedTarget.Version
}), new JsonSerializerOptions { WriteIndented=true }));
