using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.LLVM;

/// <summary>Retain the public resource access contract on the variable as well
/// as its block members. Consumers differ in propagating member decorations.</summary>
internal static class SpirvResourceAccessLowering
{
    internal static void Rewrite(string path, SpirvKernel kernel)
    {
        var binary = SpirvBinary.Parse(File.ReadAllBytes(path));
        var bindings = binary.Instructions.Where(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, _])
            .ToDictionary(i => i.Operands[0], i => i.Operands[2]);
        var sets = binary.Instructions.Where(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 34, _])
            .ToDictionary(i => i.Operands[0], i => i.Operands[2]);
        var existing = binary.Instructions.Where(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 24])
            .Select(i => i.Operands[0]).ToHashSet();
        var readOnly = kernel.Parameters.Where(p => p.IsResource).Select((p,index) => (p,index))
            .Where(pair => pair.p.Kind == SpirvKernelParameterKind.ReadOnlyStorageBuffer)
            .Select(pair => (uint)pair.index).ToHashSet();
        var added = bindings.Where(pair => sets.GetValueOrDefault(pair.Key, uint.MaxValue) == 0
                && readOnly.Contains(pair.Value) && !existing.Contains(pair.Key))
            .Select(pair => new SpirvInstruction((ushort)Op.Decorate, [pair.Key, 24])).ToArray();
        if (added.Length == 0) return;
        var instructions = binary.Instructions.ToList();
        int insertion = instructions.FindIndex(i => (Op)i.Opcode == Op.TypeVoid);
        if (insertion < 0) throw new InvalidDataException("SPIR-V has no type declaration section.");
        instructions.InsertRange(insertion, added);
        File.WriteAllBytes(path, new SpirvBinary { Version = binary.Version, Generator = binary.Generator,
            Bound = binary.Bound, Instructions = instructions }.ToBytes());
    }
}
