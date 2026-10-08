using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class ReadOnlyPointerSelectionTests
{
    internal static SpirvBinary Fixture(string kind, bool atomicFirst, bool qualified)
    {
        var flags = new ShaderType.Array(ShaderType.U32, 8);
        ShaderType values = kind == "structure"
            ? new ShaderType.Structure("Values", Enumerable.Range(0, 4).Select(i => new StructMember("v" + i, ShaderType.U32)).ToArray())
            : new ShaderType.Array(ShaderType.U32, 4);
        var inputType = new ShaderType.Structure("Input", [new("flags", flags), new("a", values), new("b", values), new("tail", ShaderType.U32)]);
        var outputType = new ShaderType.Structure("Output", [new("values", values), new("results", new ShaderType.Array(ShaderType.U32, 4))]);
        var module = new Module { WorkgroupInitializationRequired = false };
        if (values is ShaderType.Structure structure) module.Structures.Add(structure);
        module.Structures.Add(inputType); module.Structures.Add(outputType);
        module.Globals.Add(new("input", inputType, AddressSpace.Storage, StorageAccess.Read, new(0, 0)));
        module.Globals.Add(new("output", outputType, AddressSpace.Storage, Binding: new(0, 1)));
        Expression Root(string name, ShaderType type, StorageAccess access = StorageAccess.ReadWrite) => new Expression.Reference(name, new ShaderType.Pointer(type, AddressSpace.Storage, access));
        Expression Field(Expression root, string name, ShaderType type) => new Expression.Member(root, name, ((ShaderType.Pointer)root.Type) with { Base = type });
        Expression Index(Expression root, Expression index, ShaderType type) => root.Type is ShaderType.Pointer { Base: ShaderType.Structure s } && index is Expression.Literal { Value: uint i }
            ? Field(root, s.Members[(int)i].Name, type) : new Expression.Access(root, index, ((ShaderType.Pointer)root.Type) with { Base = type });
        Expression Address(Expression place) => new Expression.Unary("&", place, place.Type);
        Expression Element(Expression value, uint index) => value.Type is ShaderType.Structure s
            ? new Expression.Member(value, s.Members[(int)index].Name, ShaderType.U32) : new Expression.Access(value, Expression.U32(index), ShaderType.U32);
        var input = Root("input", inputType, StorageAccess.Read); var output = Root("output", outputType);
        var a = Field(input, "a", values); var b = Field(output, "values", values);
        var results = Field(output, "results", new ShaderType.Array(ShaderType.U32, 4));
        Expression Flag(uint index) => new Expression.Load(Index(Field(input, "flags", flags), Expression.U32(index), ShaderType.U32));
        var condition = new Expression.Binary("!=", Flag(0), Expression.U32(0), ShaderType.Bool);
        Expression left = kind == "scalar" ? Index(a, Flag(1), ShaderType.U32) : a;
        Expression right = kind == "scalar" ? Index(b, Flag(2), ShaderType.U32) : b;
        var selected = new Expression.Select(condition, Address(atomicFirst ? right : left), Address(atomicFirst ? left : right));
        var main = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        main.Body.Statements.Add(new Statement.Declare("chosen", selected.Type, selected, false));
        var atomic = Index(b, Expression.U32(0), new ShaderType.Atomic(ShaderType.U32));
        main.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("atomicAdd", [Address(atomic), Expression.U32(5)], ShaderType.U32) { AtomicMemory = new(1, 0) }));
        var pointer = new Expression.Unary("*", new Expression.Reference("chosen", selected.Type), selected.Type);
        var load = new Expression.Load(pointer) { MemoryAccess = qualified ? new(7, 4) : null };
        main.Body.Statements.Add(new Statement.Declare("before", load.Type, load, false));
        var saved = new Expression.Reference("before", load.Type);
        Expression old = kind == "scalar" ? saved : Element(saved, 2);
        main.Body.Statements.Add(new Statement.Store(Index(results, Expression.U32(0), ShaderType.U32), old));
        main.Body.Statements.Add(new Statement.Store(Index(results, Expression.U32(1), ShaderType.U32), new Expression.Binary("+", old, Expression.U32(100), ShaderType.U32)));
        main.Body.Statements.Add(new Statement.Store(Index(results, Expression.U32(2), ShaderType.U32), new Expression.Load(Field(input, "tail", ShaderType.U32))));
        module.Functions.Add(main);
        var writer = typeof(SpirvWriter).GetNestedType("Writer", System.Reflection.BindingFlags.NonPublic)!;
        var instance = Activator.CreateInstance(writer, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [module, new SpirvWriteOptions()], null)!;
        var binary = (SpirvBinary)writer.GetMethod("Write")!.Invoke(instance, null)!;
        var code = binary.Instructions.ToList(); code.Insert(1, new((ushort)Op.Capability, [4442u]));
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = code };
    }

    [Theory]
    [InlineData("scalar", false, false)] [InlineData("scalar", true, true)]
    [InlineData("array", false, false)] [InlineData("array", true, true)]
    [InlineData("structure", false, false)] [InlineData("structure", true, true)]
    public void OrdinaryReadOnlyCandidatesRemainOrdinary(string kind, bool atomicFirst, bool qualified)
    {
        var module = SpirvReader.Parse(Fixture(kind, atomicFirst, qualified).ToBytes());
        var readOnly = module.Globals.Single(g => g.Binding == new ResourceBinding(0, 0));
        Assert.Equal(StorageAccess.Read, readOnly.Access);
        bool HasAtomic(ShaderType type) => type switch
        {
            ShaderType.Atomic => true, ShaderType.Array a => HasAtomic(a.Element),
            ShaderType.Structure s => s.Members.Any(m => HasAtomic(m.Type)), _ => false
        };
        Assert.False(HasAtomic(readOnly.Type));
        ModuleValidator.Validate(module);
        byte[] native = SpirvWriter.Write(module);
        var instructions = SpirvBinary.Parse(native).Instructions;
        Assert.Single(instructions, i => (Op)i.Opcode == Op.AtomicIAdd);
        Assert.DoesNotContain(instructions, i => (Op)i.Opcode is Op.AtomicLoad or Op.AtomicStore);
        ModuleValidator.Validate(SpirvReader.Parse(native));
        string wgsl = WgslWriter.Write(module); ModuleValidator.Validate(WgslReader.Parse(wgsl));
        Assert.Contains("var<storage, read> " + readOnly.Name, wgsl);
    }
}
