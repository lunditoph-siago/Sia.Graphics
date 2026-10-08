using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class NativeMatrixLayoutTests
{
    internal static SpirvBinary Fixture(int columns, int rows, uint stride, bool rowMajor, string operation = "component", string kind = "direct", bool uniform = false, bool half = false, uint memoryFlags = 0)
    {
        string matrix = $"mat{columns}x{rows}{(half ? 'h' : 'f')}", vector = $"vec{rows}{(half ? 'h' : 'f')}";
        string place = kind switch { "array" => "data.matrix[data.indices.z]", "nested" => "data.values[data.indices.z].matrix", _ => "data.matrix" };
        string write = operation switch
        {
            "component" => place + "[data.indices.x][data.indices.y]=301.0;",
            "column" => $"{place}[data.indices.x]={vector}({string.Join(',', Enumerable.Range(0, rows).Select(i => (401 + i) + ".0"))});",
            "matrix" => $"{place}={matrix}({string.Join(',', Enumerable.Range(0, columns).Select(c => vector + "(" + string.Join(',', Enumerable.Range(0, rows).Select(r => (501 + 10 * c + r) + ".0")) + ")"))});",
            "copy" => $"{place}=transpose(transpose({place}));",
            "root" => "let copied=data;data=copied;",
            "swap" when kind == "array" => $"data.matrix=array<{matrix},2>(data.matrix[1],data.matrix[0]);",
            _ => throw new ArgumentException(operation)
        };
        if (uniform) write = "";
        string reads(string expression, int offset) => string.Join('\n', from c in Enumerable.Range(0, columns) from r in Enumerable.Range(0, rows)
            select $"output[{offset + c * rows + r}]=f32(({expression})[{c}][{r}]);");
        string dataType = kind switch
        {
            "array" => $"struct Data{{indices:vec4u,matrix:array<{matrix},2>,tail:f32,}}",
            "nested" => $"struct Inner{{matrix:{matrix},tail:f32,}}struct Data{{indices:vec4u,values:array<Inner,2>,tail:f32,}}",
            _ => $"struct Data{{indices:vec4u,matrix:{matrix},tail:f32,}}"
        };
        string source = $$"""
            {{(half ? "enable f16;" : "")}}
            {{dataType}}
            @group(0) @binding(0) var<storage,read_write> data:Data;
            @group(0) @binding(1) var<storage,read_write> output:array<f32>;
            @compute @workgroup_size(1) fn main(){
                let whole={{place}};
                {{reads("whole", 0)}}
                output[{{columns * rows}}]=f32({{place}}[data.indices.x][data.indices.y]);
                let column={{place}}[data.indices.x];
                output[{{columns * rows + 1}}]=f32(column[data.indices.y]);
                {{write}}
                {{reads(place, columns * rows + 2)}}
                output[{{columns * rows * 2 + 2}}]=data.tail;
            }
            """;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source)));
        uint data = binary.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "Data").Operands[0];
        uint owner = kind == "nested" ? binary.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "Inner").Operands[0] : data;
        uint field = kind == "nested" ? 0u : 1u;
        var instructions = binary.Instructions.Select(i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == owner && i.Operands[1] == field && i.Operands[2] == 7
            ? new SpirvInstruction(i.Opcode, [owner, field, 7, stride])
            : (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == owner && i.Operands[1] == field && i.Operands[2] == 5 && rowMajor
                ? new SpirvInstruction(i.Opcode, [owner, field, 4])
                : (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == data && i.Operands[1] == 2 && i.Operands[2] == 35
                    ? new SpirvInstruction(i.Opcode, [data, 2, 35, kind == "array" ? 336u : kind == "nested" ? 400u : 160u])
                    : kind == "nested" && (Op)i.Opcode == Op.MemberDecorate && i.Operands is [var s, 1, 35, _] && s == owner
                        ? new SpirvInstruction(i.Opcode, [owner, 1, 35, 160])
                        : kind != "direct" && (Op)i.Opcode == Op.Decorate && i.Operands is [var arrayId, 6, _]
                            && binary.Instructions.Any(t => (Op)t.Opcode == Op.TypeArray && t.Operands[0] == arrayId
                                && (kind == "nested" ? t.Operands[1] == owner : binary.Instructions.Any(m => (Op)m.Opcode == Op.TypeMatrix && m.Operands[0] == t.Operands[1])))
                            ? new SpirvInstruction(i.Opcode, [arrayId, 6, kind == "nested" ? 192u : 160u]) : i).ToList();
        uint bound = binary.Bound;
        if (uniform)
        {
            uint variable = instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "data").Operands[0];
            var roots = new HashSet<uint> { variable }; var mapped = new Dictionary<uint, uint>(); var pointers = new List<SpirvInstruction>();
            uint Pointer(uint original)
            {
                if (mapped.TryGetValue(original, out uint type)) return type;
                var definition = instructions.Single(i => (Op)i.Opcode == Op.TypePointer && i.Operands[0] == original);
                type = bound++; mapped.Add(original, type); pointers.Add(new((ushort)Op.TypePointer, [type, 2, definition.Operands[2]])); return type;
            }
            for (int i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i]; var operands = instruction.Operands;
                if ((Op)instruction.Opcode == Op.Variable && operands[1] == variable) instructions[i] = new(instruction.Opcode, [Pointer(operands[0]), variable, 2]);
                else if ((Op)instruction.Opcode is Op.AccessChain or Op.InBoundsAccessChain or Op.CopyObject && roots.Contains(operands[2]))
                {
                    roots.Add(operands[1]); var changed = operands.ToArray(); changed[0] = Pointer(operands[0]); instructions[i] = new(instruction.Opcode, changed);
                }
            }
            instructions.InsertRange(instructions.FindIndex(i => (Op)i.Opcode == Op.Variable), pointers);
        }
        if (memoryFlags != 0)
        {
            uint variable = instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "data").Operands[0];
            var roots = new HashSet<uint> { variable };
            for (int i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i]; var operands = instruction.Operands;
                if ((Op)instruction.Opcode is Op.AccessChain or Op.InBoundsAccessChain or Op.CopyObject && roots.Contains(operands[2])) roots.Add(operands[1]);
                else if ((Op)instruction.Opcode == Op.Load && roots.Contains(operands[2]) || (Op)instruction.Opcode == Op.Store && roots.Contains(operands[0]))
                    instructions[i] = new(instruction.Opcode, operands.Concat((memoryFlags & 2) == 0 ? [memoryFlags] : new uint[] { memoryFlags, half ? 2u : 4u }).ToArray());
            }
        }
        return new() { Version = binary.Version, Bound = bound, Instructions = instructions };
    }

    [Theory]
    [InlineData(false, "component")] [InlineData(true, "component")]
    [InlineData(false, "column")] [InlineData(true, "column")]
    [InlineData(false, "matrix")] [InlineData(true, "matrix")]
    [InlineData(false, "copy")] [InlineData(true, "copy")]
    public void ExplicitMatrixLayoutReadsAndWritesTranslateBothWays(bool rowMajor, string operation)
    {
        var module = SpirvReader.Parse(Fixture(3, 2, 32, rowMajor, operation).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData("array", false, false)] [InlineData("array", true, false)]
    [InlineData("nested", false, false)] [InlineData("nested", true, false)]
    [InlineData("array", false, true)] [InlineData("array", true, true)]
    [InlineData("nested", false, true)] [InlineData("nested", true, true)]
    public void MatrixArrayAndNestedLayoutKeepMemoryAndValueTypesSeparate(string kind, bool rowMajor, bool uniform)
    {
        var module = SpirvReader.Parse(Fixture(2, 3, 32, rowMajor, "root", kind, uniform).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    public static IEnumerable<object[]> Shapes() => from c in Enumerable.Range(2, 3) from r in Enumerable.Range(2, 3)
        from rowMajor in new[] { false, true } from half in new[] { false, true } select new object[] { c, r, rowMajor, half };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryConcreteMatrixShapeSupportsPaddedNativeLayout(int columns, int rows, bool rowMajor, bool half)
    {
        var module = SpirvReader.Parse(Fixture(columns, rows, 32, rowMajor, "matrix", half: half).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SplitMatrixOperationsRetainVolatileCacheHintsAndDiscardBaseAlignment(bool rowMajor)
    {
        var module = SpirvReader.Parse(Fixture(3, 2, 32, rowMajor, "matrix", memoryFlags: 7).ToBytes());
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 5);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && i.Operands[2] == 5);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 4 && i.Operands[3] == 7 && i.Operands[4] == 4);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }
}
