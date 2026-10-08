using System.Buffers.Binary;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvTests
{
    internal static SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
    internal static SpirvInstruction Entry(uint id, params uint[] interfaces) => I(Op.EntryPoint,
        new uint[] { 5, id }.Concat(SpirvBinary.StringWords("main")).Concat(interfaces).ToArray());
    internal static SpirvBinary Binary(params SpirvInstruction[] instructions) => new() { Bound = 100, Instructions = instructions };

    [Fact]
    public void FramingRoundtripsBothByteOrdersWithoutChangingWords()
    {
        var original = Binary(I(Op.Capability, 1), I(Op.MemoryModel, 0, 1));
        byte[] little = original.ToBytes();
        byte[] big = new byte[little.Length];
        for (int i = 0; i < little.Length; i += 4)
            BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(i), BinaryPrimitives.ReadUInt32LittleEndian(little.AsSpan(i)));
        Assert.Equal(original.ToWords(), SpirvBinary.Parse(little).ToWords());
        Assert.Equal(original.ToWords(), SpirvBinary.Parse(big).ToWords());
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(19)] [InlineData(21)]
    public void RejectsMissingOrPartialWords(int length) => Assert.Throws<ShaderException>(() => SpirvBinary.Parse(new byte[length]));

    [Fact]
    public void RejectsInvalidHeaderAndInstructions()
    {
        uint[] valid = [SpirvBinary.Magic, 0x10300, 0, 10, 0];
        uint[] invalidVersion = (uint[])valid.Clone(); invalidVersion[1] = 0x10700;
        uint[] zeroBound = (uint[])valid.Clone(); zeroBound[3] = 0;
        uint[] schema = (uint[])valid.Clone(); schema[4] = 1;
        foreach (uint[] words in new[] { invalidVersion, zeroBound, schema, valid.Concat(new uint[] { 0 }).ToArray(), valid.Concat(new uint[] { 0x20011 }).ToArray() })
            Assert.Throws<ShaderException>(() => SpirvBinary.Parse(words));
    }

    [Fact]
    public void ReadOnlyMemberDecorationSurvivesTranslation()
    {
        var binary = Binary(
            I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), Entry(1), I(Op.ExecutionMode, 1, 17, 1, 1, 1),
            I(Op.Decorate, 2, 6, 4), I(Op.Decorate, 3, 2), I(Op.MemberDecorate, 3, 0, 35, 0),
            I(Op.MemberDecorate, 3, 0, 24), I(Op.Decorate, 4, 34, 0), I(Op.Decorate, 4, 33, 0),
            I(Op.TypeVoid, 5), I(Op.TypeFunction, 6, 5), I(Op.TypeInt, 7, 32, 0),
            I(Op.TypeRuntimeArray, 2, 7), I(Op.TypeStruct, 3, 2), I(Op.TypePointer, 8, 12, 3),
            I(Op.Variable, 8, 4, 12), I(Op.Function, 5, 1, 0, 6), I(Op.Label, 9), I(Op.Return), I(Op.FunctionEnd));
        var module = SpirvReader.Parse(binary.ToBytes());
        Assert.Equal(StorageAccess.Read, Assert.Single(module.Globals).Access);
        string wgsl = WgslWriter.Write(module);
        Assert.Contains("var<storage, read>", wgsl);
        Assert.DoesNotContain("read_write", wgsl);
        Assert.Contains("@compute", wgsl);
    }

    [Fact]
    public void ComputeStorePreservesInvocationIdAndBinding()
    {
        var module = SpirvReader.Parse(ComputeStore().ToBytes());
        string wgsl = WgslWriter.Write(module);
        Assert.Contains("@builtin(global_invocation_id)", wgsl);
        Assert.Contains("@group(2) @binding(3)", wgsl);
        Assert.Contains("@workgroup_size(64u, 1u, 1u)", wgsl);
        Assert.Contains("g11.m0[r22] = r23;", wgsl);
        var main = module.Functions.Single(f => f.Stage is not null);
        Assert.Equal(ShaderStage.Compute, main.Stage);
        Assert.Single(main.Arguments);
    }

    internal static SpirvBinary ComputeStore() => Binary(
        I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), Entry(20, 10), I(Op.ExecutionMode, 20, 17, 64, 1, 1),
        I(Op.Decorate, 10, 11, 28), I(Op.Decorate, 11, 34, 2), I(Op.Decorate, 11, 33, 3),
        I(Op.Decorate, 5, 6, 4), I(Op.Decorate, 6, 2), I(Op.MemberDecorate, 6, 0, 35, 0),
        I(Op.TypeVoid, 1), I(Op.TypeInt, 2, 32, 0), I(Op.TypeVector, 3, 2, 3),
        I(Op.TypePointer, 4, 1, 3), I(Op.TypeRuntimeArray, 5, 2), I(Op.TypeStruct, 6, 5),
        I(Op.TypePointer, 7, 12, 6), I(Op.TypePointer, 8, 12, 2), I(Op.TypeFunction, 9, 1),
        I(Op.Variable, 4, 10, 1), I(Op.Variable, 7, 11, 12),
        I(Op.Constant, 2, 12, 0), I(Op.Constant, 2, 13, 1),
        I(Op.Function, 1, 20, 0, 9), I(Op.Label, 21), I(Op.Load, 3, 24, 10),
        I(Op.CompositeExtract, 2, 22, 24, 0), I(Op.IAdd, 2, 23, 22, 13),
        I(Op.AccessChain, 8, 25, 11, 12, 22), I(Op.Store, 25, 23), I(Op.Return), I(Op.FunctionEnd));

    [Fact]
    public void PhiBackedgesUseParallelSnapshots()
    {
        var binary = Binary(
            I(Op.Capability, 1), I(Op.MemoryModel, 0, 1), Entry(10), I(Op.ExecutionMode, 10, 17, 1, 1, 1),
            I(Op.TypeVoid, 1), I(Op.TypeInt, 2, 32, 0), I(Op.TypeBool, 3), I(Op.TypeFunction, 4, 1),
            I(Op.Constant, 2, 5, 0), I(Op.Constant, 2, 6, 1), I(Op.Constant, 2, 7, 8),
            I(Op.Function, 1, 10, 0, 4), I(Op.Label, 11), I(Op.Branch, 12), I(Op.Label, 12),
            I(Op.Phi, 2, 20, 5, 11, 21, 14), I(Op.Phi, 2, 21, 6, 11, 20, 14),
            I(Op.ULessThan, 3, 22, 20, 7), I(Op.LoopMerge, 15, 14, 0), I(Op.BranchConditional, 22, 13, 15),
            I(Op.Label, 13), I(Op.Branch, 14), I(Op.Label, 14), I(Op.Branch, 12),
            I(Op.Label, 15), I(Op.Return), I(Op.FunctionEnd));
        string wgsl = WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()));
        Assert.Contains("loop {", wgsl); Assert.Contains("continuing {", wgsl);
        int snapshot = wgsl.IndexOf("let edge_14_12_21", StringComparison.Ordinal);
        int firstCopy = wgsl.IndexOf("r20 = edge_14_12_20", StringComparison.Ordinal);
        Assert.True(snapshot >= 0 && snapshot < firstCopy, wgsl);
        Assert.Contains("break;", wgsl);
    }

    [Fact]
    public void RejectsUnsupportedSemanticsRatherThanEmittingEmptyShader()
    {
        var valid = ComputeStore();
        var instructions = valid.Instructions.ToList();
        instructions.Insert(instructions.Count - 2, new(65500, []));
        var binary = new SpirvBinary { Bound = valid.Bound, Instructions = instructions };
        Assert.Throws<ShaderException>(() => SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void RejectsDuplicateResultIds()
    {
        var binary = Binary(I(Op.MemoryModel, 0, 1), I(Op.TypeInt, 1, 32, 0), I(Op.TypeFloat, 1, 32));
        Assert.Contains("defined twice", Assert.Throws<ShaderException>(() => SpirvReader.Parse(binary.ToBytes())).Message);
    }
}
