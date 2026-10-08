using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class PointerSelectionTests
{
    internal static SpirvBinary Fixture(string kind, bool nested, bool qualified)
    {
        bool descriptors = kind.StartsWith("descriptor", StringComparison.Ordinal);
        bool shared = kind == "workgroup", atomic = kind is "atomic" or "descriptor-atomic" or "descriptor-dynamic-atomic", mixed = kind.StartsWith("mixed", StringComparison.Ordinal),
            aggregate = kind is "array" or "mixed-array" or "mixed-struct";
        ShaderType leaf = atomic ? new ShaderType.Atomic(ShaderType.U32) : ShaderType.U32;
        var flags = new ShaderType.Array(ShaderType.U32, 8);
        ShaderType values = kind == "vector" ? new ShaderType.Vector(4, ShaderType.U32)
            : kind == "mixed-struct" ? new ShaderType.Structure("Values", Enumerable.Range(0, 4).Select(i => new StructMember("v" + i, ShaderType.U32)).ToArray())
            : new ShaderType.Array(leaf, 4);
        var data = new ShaderType.Structure("Data", [new("flags", flags), new("a", values), new("b", values), new("tail", ShaderType.U32)]);
        var module = new Module { WorkgroupInitializationRequired = false, VulkanMemoryModel = atomic && qualified };
        if (values is ShaderType.Structure structure) module.Structures.Add(structure);
        module.Structures.Add(data);
        module.Globals.Add(new("data", descriptors ? new ShaderType.BindingArray(data, 2) : data, AddressSpace.Storage, Binding: new(0, 0)));
        module.Globals.Add(new("output", new ShaderType.Array(ShaderType.U32, null), AddressSpace.Storage, Binding: new(0, 1)));
        if (shared) module.Globals.Add(new("shared", data, AddressSpace.Workgroup));
        Expression Root(string name, ShaderType type, AddressSpace space) => new Expression.Reference(name, new ShaderType.Pointer(type, space));
        Expression Field(Expression root, string name, ShaderType type) => new Expression.Member(root, name, ((ShaderType.Pointer)root.Type) with { Base = type });
        Expression Index(Expression root, Expression index, ShaderType type) => root.Type is ShaderType.Pointer { Base: ShaderType.Structure s } && index is Expression.Literal { Value: uint i }
            ? Field(root, s.Members[(int)i].Name, type) : new Expression.Access(root, index, ((ShaderType.Pointer)root.Type) with { Base = type });
        Expression Element(Expression root, uint i) => root.Type is ShaderType.Structure s
            ? new Expression.Member(root, s.Members[(int)i].Name, ShaderType.U32) : new Expression.Access(root, Expression.U32(i), ShaderType.U32);
        Expression input = Root("data", module.Globals[0].Type, AddressSpace.Storage);
        if (descriptors) input = Index(input, Expression.U32(0), data);
        var memory = shared ? Root("shared", data, AddressSpace.Workgroup) : input;
        var output = Root("output", module.Globals[1].Type, AddressSpace.Storage);
        Expression Flag(uint i) => Index(Field(input, "flags", flags), Expression.U32(i), ShaderType.U32);
        Expression Condition(uint i) => new Expression.Binary("!=", new Expression.Load(Flag(i)), Expression.U32(0), ShaderType.Bool);
        Expression A = Field(memory, "a", values), B = Field(memory, "b", values);
        if (descriptors) B = Field(Index(Root("data", module.Globals[0].Type, AddressSpace.Storage), kind.Contains("-dynamic", StringComparison.Ordinal)
            ? new Expression.Load(Flag(4)) : Expression.U32(kind == "descriptor-same" ? 0u : 1u), data), "a", values);
        if (!aggregate) { A = Index(A, new Expression.Load(Flag(1)), leaf); B = Index(B, new Expression.Load(Flag(2)), leaf); }
        if (kind == "cross") B = Index(output, new Expression.Load(Flag(2)), ShaderType.U32);
        Expression Address(Expression place) => new Expression.Unary("&", place, place.Type);
        Expression selected = new Expression.Select(Condition(0), Address(A), Address(B));
        if (nested) selected = new Expression.Select(Condition(3), selected, Address(aggregate ? Field(memory, "a", values)
            : Index(Field(memory, "a", values), new Expression.Load(Flag(4)), leaf)));
        var main = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        if (shared)
        {
            main.Body.Statements.Add(new Statement.Store(memory, new Expression.Load(input)));
            main.Body.Statements.Add(new Statement.Barrier(false, true));
        }
        main.Body.Statements.Add(new Statement.Declare("chosen", selected.Type, selected, false));
        var pointer = new Expression.Unary("*", new Expression.Reference("chosen", selected.Type), selected.Type);
        main.Body.Statements.Add(new Statement.Store(Flag(0), new Expression.Binary("-", Expression.U32(1), new Expression.Load(Flag(0)), ShaderType.U32)));
        main.Body.Statements.Add(new Statement.Store(Flag(1), Expression.U32(3)));
        main.Body.Statements.Add(new Statement.Store(Flag(2), Expression.U32(0)));
        if (kind.Contains("-dynamic", StringComparison.Ordinal)) main.Body.Statements.Add(new Statement.Store(Flag(4), new Expression.Binary("-", Expression.U32(1), new Expression.Load(Flag(4)), ShaderType.U32)));
        if (mixed)
        {
            // Native SPIR-V has scalar pointees for both ordinary and atomic operations.
            var atomicPlace = Index(Field(memory, "a", values), Expression.U32(0), new ShaderType.Atomic(ShaderType.U32));
            main.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("atomicAdd", [Address(atomicPlace), Expression.U32(5)], ShaderType.U32)
                { AtomicMemory = new(1, 0) }));
        }
        var access = qualified ? new SpirvMemoryAccess(7, 4) : null;
        if (atomic)
        {
            var call = new Expression.Call("atomicAdd", [new Expression.Unary("&", pointer, pointer.Type), Expression.U32(100)], ShaderType.U32)
                { AtomicMemory = new(1, qualified ? 32768u : 0u) };
            main.Body.Statements.Add(new Statement.Store(Index(output, Expression.U32(0), ShaderType.U32), call));
            main.Body.Statements.Add(new Statement.Store(Index(output, Expression.U32(1), ShaderType.U32),
                new Expression.Call("atomicLoad", [new Expression.Unary("&", pointer, pointer.Type)], ShaderType.U32)));
        }
        else if (kind == "helper")
        {
            var helper = new ShaderFunction("edit") { ReturnType = ShaderType.U32 };
            helper.Arguments.Add(new("p", pointer.Type)); helper.Arguments.Add(new("q", pointer.Type));
            var p = new Expression.Unary("*", new Expression.Reference("p", pointer.Type), pointer.Type);
            var q = new Expression.Unary("*", new Expression.Reference("q", pointer.Type), pointer.Type);
            helper.Body.Statements.Add(new Statement.Store(p, new Expression.Binary("+", new Expression.Load(p), Expression.U32(100), ShaderType.U32)) { MemoryAccess = access });
            helper.Body.Statements.Add(new Statement.Return(new Expression.Load(q) { MemoryAccess = access })); module.Functions.Add(helper);
            main.Body.Statements.Add(new Statement.Store(Index(output, Expression.U32(0), ShaderType.U32), new Expression.Load(pointer) { MemoryAccess = access }));
            main.Body.Statements.Add(new Statement.Store(Index(output, Expression.U32(1), ShaderType.U32),
                new Expression.Call("edit", [new Expression.Unary("&", pointer, pointer.Type), new Expression.Unary("&", pointer, pointer.Type)], ShaderType.U32)));
        }
        else
        {
            Expression.Load before = new(pointer) { MemoryAccess = access };
            main.Body.Statements.Add(new Statement.Declare("before", before.Type, before, false));
            var saved = new Expression.Reference("before", before.Type);
            Expression value = aggregate ? new Expression.Construct(values, Enumerable.Range(0, 4).Select(i => (Expression)new Expression.Binary("+",
                Element(saved, (uint)i), Expression.U32(100), ShaderType.U32)).ToArray())
                : new Expression.Binary("+", saved, Expression.U32(100), ShaderType.U32);
            main.Body.Statements.Add(new Statement.Store(pointer, value) { MemoryAccess = access });
            Expression old = aggregate ? Element(saved, 2) : saved;
            Expression after = aggregate ? new Expression.Load(Index(pointer, Expression.U32(2), ShaderType.U32)) : new Expression.Load(pointer);
            main.Body.Statements.Add(new Statement.Store(Index(output, Expression.U32(0), ShaderType.U32), old));
            main.Body.Statements.Add(new Statement.Store(Index(output, Expression.U32(1), ShaderType.U32), after));
        }
        main.Body.Statements.Add(new Statement.Store(Index(output, Expression.U32(2), ShaderType.U32), new Expression.Load(Field(memory, "tail", ShaderType.U32))));
        module.Functions.Add(main);
        // Keep actual native pointer selection and helper parameters in inputs.
        var writer = typeof(SpirvWriter).GetNestedType("Writer", System.Reflection.BindingFlags.NonPublic)!;
        var instance = Activator.CreateInstance(writer, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [module, new SpirvWriteOptions()], null)!;
        var binary = (SpirvBinary)writer.GetMethod("Write")!.Invoke(instance, null)!;
        var code = binary.Instructions.ToList(); code.Insert(1, new((ushort)Op.Capability, [shared || kind == "cross" || descriptors && kind != "descriptor-same" ? 4442u : 4441u]));
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = code };
    }

    [Theory]
    [InlineData("scalar", false, false)] [InlineData("scalar", true, true)]
    [InlineData("array", false, false)] [InlineData("array", true, true)]
    [InlineData("workgroup", false, false)] [InlineData("workgroup", true, false)]
    [InlineData("helper", false, false)] [InlineData("helper", true, true)]
    [InlineData("atomic", false, false)] [InlineData("atomic", true, true)]
    [InlineData("mixed", false, false)] [InlineData("mixed", true, true)]
    [InlineData("mixed-array", false, false)] [InlineData("mixed-array", true, true)]
    [InlineData("cross", false, false)] [InlineData("cross", true, true)]
    [InlineData("vector", false, false)] [InlineData("vector", true, true)]
    [InlineData("mixed-struct", false, false)] [InlineData("mixed-struct", true, true)]
    public void SelectedPointersRetainOnlyTheirChosenMemoryOperation(string kind, bool nested, bool qualified)
    {
        var module = SpirvReader.Parse(Fixture(kind, nested, qualified).ToBytes());
        if (kind.StartsWith("mixed", StringComparison.Ordinal))
        {
            int AtomicLeaves(ShaderType type) => type switch
            {
                ShaderType.Atomic => 1, ShaderType.Array a => AtomicLeaves(a.Element),
                ShaderType.Structure s => s.Members.Sum(m => AtomicLeaves(m.Type)), _ => 0
            };
            Assert.Equal(1, AtomicLeaves(module.Globals.Single(g => g.Binding == new ResourceBinding(0, 0)).Type));
        }
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData("scalar", 0u, "variable-pointer capability")]
    [InlineData("workgroup", 4441u, "full VariablePointers")]
    [InlineData("cross", 4441u, "one storage buffer structure")]
    public void PointerSelectionEnforcesItsDeclaredCapability(string kind, uint capability, string diagnostic)
    {
        var binary = Fixture(kind, false, false);
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Capability || i.Operands[0] is not (4441 or 4442)).ToList();
        if (capability != 0) code.Insert(1, new((ushort)Op.Capability, [capability]));
        var invalid = new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(invalid.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains(diagnostic, error.Message);
    }
}
