using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class MatrixHelperTests
{
    private static SpirvBinary Layout(SpirvBinary binary, bool rowMajor)
    {
        uint data = binary.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "Data").Operands[0];
        var code = binary.Instructions.Select(i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == data && i.Operands[1] == 1 && i.Operands[2] == 7
            ? new SpirvInstruction(i.Opcode, [data, 1, 7, 32])
            : (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == data && i.Operands[1] == 1 && i.Operands[2] == 5 && rowMajor
                ? new SpirvInstruction(i.Opcode, [data, 1, 4])
                : (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == data && i.Operands[1] == 2 && i.Operands[2] == 35
                    ? new SpirvInstruction(i.Opcode, [data, 2, 35, 160]) : i).ToList();
        int firstType = code.FindIndex(i => i.Opcode is >= 19 and <= 39);
        if (!code.Any(i => (Op)i.Opcode == Op.MemberDecorate && i.Operands is [var id, 1, 7, _] && id == data))
            code.Insert(firstType, new((ushort)Op.MemberDecorate, [data, 1, 7, 32]));
        if (!code.Any(i => (Op)i.Opcode == Op.MemberDecorate && i.Operands.Length == 3 && i.Operands[0] == data && i.Operands[1] == 1 && i.Operands[2] is 4 or 5))
            code.Insert(firstType, new((ushort)Op.MemberDecorate, [data, 1, rowMajor ? 4u : 5u]));
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = code };
    }

    internal const string HelperSource = """
        struct Data {indices:vec4u,matrix:mat3x2f,tail:f32,}
        @group(0) @binding(0) var<storage,read_write> data:Data;
        @group(0) @binding(1) var<storage,read_write> output:array<f32>;
        fn edit(p:ptr<storage,vec2f,read_write>,q:ptr<storage,vec2f,read_write>,mode:u32)->f32 {
            (*p).x=(*p).x+100.0;
            if mode==0u{return (*q).x;}
            var n=0u;
            loop {(*q).y=(*q).y+1.0;n++;if n==2u{return (*p).y;}}
            return 999.0;
        }
        fn nested(p:ptr<storage,vec2f,read_write>,q:ptr<storage,vec2f,read_write>,mode:u32)->f32 {
            return edit(p,q,mode);
        }
        @compute @workgroup_size(1) fn main(){
            let column=&data.matrix[data.indices.x];
            output[0]=nested(column,column,data.indices.y);
            output[1]=data.matrix[data.indices.x][0];
            output[2]=data.matrix[data.indices.x][1];output[3]=data.tail;
        }
        """;

    internal static SpirvBinary HelperFixture(bool rowMajor)
    {
        // Invoke the raw emitter to retain actual native pointer parameters in
        // this input fixture even after the public writer learns to expand them.
        return PointerFixture(HelperSource, rowMajor, 4441);
    }

    private static SpirvBinary PointerFixture(string source, bool rowMajor, uint capability)
    {
        // This is a native SPIR-V alias fixture, not legal authored WGSL.
        // Parse distinct roots, then introduce the native alias explicitly in IR.
        string spare = capability == 4442 ? "var<workgroup> sia_alias_fixture:Data;"
            : "@group(0) @binding(2) var<storage,read_write> sia_alias_fixture:Data;";
        string distinct = source.Replace("@compute", spare + "@compute", StringComparison.Ordinal)
            .Replace("nested(column,column,", "nested(column,&sia_alias_fixture.matrix[0u],", StringComparison.Ordinal)
            .Replace("edit(&data.matrix,&data.matrix,", "edit(&data.matrix,&sia_alias_fixture.matrix,", StringComparison.Ordinal);
        var module = WgslReader.Parse(distinct);
        var entry = module.Functions.Single(f => f.Stage is not null); int aliases = 0;
        for (int i = 0; i < entry.Body.Statements.Count; i++) {
            if (entry.Body.Statements[i] is not Statement.Declare { Initializer: Expression.Call { Function: "nested" or "edit" } call } declaration) continue;
            entry.Body.Statements[i] = declaration with { Initializer = call with { Arguments = [call.Arguments[0], call.Arguments[0], call.Arguments[2]] } };
            aliases++;
        }
        Assert.Equal(1, aliases); Assert.Equal(1, module.Globals.RemoveAll(g => g.Name == "sia_alias_fixture"));
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module));
        binary = Layout(binary, rowMajor);
        var code = binary.Instructions.ToList(); code.Insert(1, new((ushort)Op.Capability, [capability]));
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.MemoryModel), new((ushort)Op.Extension, SpirvBinary.StringWords("SPV_KHR_variable_pointers")));
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = code };
    }

    [Fact]
    public void AuthoredAliasedMatrixHelperSourceIsRejectedBeforeNativeFixtureConstruction()
    {
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse(HelperSource));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage); Assert.Contains("alias violation", error.Message);
    }

    internal static SpirvBinary WholeHelperFixture(bool rowMajor, bool workgroup)
    {
        string pointer = workgroup ? "ptr<workgroup,mat3x2f>" : "ptr<storage,mat3x2f,read_write>";
        string globals = workgroup
            ? "@group(0) @binding(0) var<storage,read> inputs:Data; var<workgroup> data:Data;"
            : "@group(0) @binding(0) var<storage,read_write> data:Data;";
        string source = $$"""
            struct Data {indices:vec4u,matrix:mat3x2f,tail:f32,}
            {{globals}}
            @group(0) @binding(1) var<storage,read_write> output:array<f32>;
            fn edit(p:{{pointer}},q:{{pointer}},mode:u32)->f32 {
                let before=*p;
                *p=mat3x2f(vec2f(101.0,102.0),vec2f(201.0,202.0),vec2f(301.0,302.0));
                if mode==0u{return (*q)[0][0];}
                (*q)[2][1]=(*q)[2][1]+before[1][0];return (*p)[2][1];
            }
            @compute @workgroup_size(1) fn main(){
                {{(workgroup ? "data=inputs;workgroupBarrier();" : "")}}
                output[0]=edit(&data.matrix,&data.matrix,data.indices.y);
                output[1]=data.matrix[data.indices.x][0];
                output[2]=data.matrix[data.indices.x][1];output[3]=data.tail;
            }
            """;
        return PointerFixture(source, rowMajor, workgroup ? 4442u : 4441u);
    }

    internal static SpirvBinary ArrayInitializerFixture(bool rowMajor, bool zero)
    {
        string initializer = zero ? "Data()" : "Data(vec4u(0u),array<mat3x2f,2>(mat3x2f(vec2f(11.0,12.0),vec2f(21.0,22.0),vec2f(31.0,32.0)),mat3x2f(vec2f(111.0,112.0),vec2f(121.0,122.0),vec2f(131.0,132.0))),919.0)";
        string source = $$"""
            struct Data {indices:vec4u,matrix:array<mat3x2f,2>,tail:f32,}
            @group(0) @binding(0) var<storage,read> inputs:array<u32>;
            @group(0) @binding(1) var<storage,read_write> output:array<f32>;
            var<private> data:Data={{initializer}};
            @compute @workgroup_size(1) fn main(){
                output[0]=data.matrix[inputs[2]][inputs[0]][inputs[1]];
                data.matrix[inputs[2]][inputs[0]][inputs[1]]=71.0;
                output[1]=data.matrix[inputs[2]][inputs[0]][inputs[1]];output[2]=data.tail;
            }
            """;
        var binary = Layout(SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default)), rowMajor);
        uint data = binary.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "Data").Operands[0];
        uint array = binary.Instructions.Single(i => (Op)i.Opcode == Op.TypeStruct && i.Operands[0] == data).Operands[2];
        var code = binary.Instructions.Select(i => (Op)i.Opcode == Op.Decorate && i.Operands is [var id, 6, _] && id == array
            ? new SpirvInstruction(i.Opcode, [array, 6, 128]) : i).ToList();
        if (!code.Any(i => (Op)i.Opcode == Op.Decorate && i.Operands is [var id, 6, _] && id == array))
            code.Insert(code.FindIndex(i => i.Opcode is >= 19 and <= 39), new((ushort)Op.Decorate, [array, 6, 128]));
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = code };
    }

    internal static SpirvBinary SharedInitializerFixture(bool rowMajor)
    {
        const string source = """
            struct Data {indices:vec4u,first:mat3x2f,second:mat3x2f,tail:f32,}
            @group(0) @binding(0) var<storage,read> inputs:array<u32>;
            @group(0) @binding(1) var<storage,read_write> output:array<f32>;
            var<private> data=Data(vec4u(0u),
                mat3x2f(vec2f(11.0,12.0),vec2f(21.0,22.0),vec2f(31.0,32.0)),
                mat3x2f(vec2f(111.0,112.0),vec2f(121.0,122.0),vec2f(131.0,132.0)),919.0);
            @compute @workgroup_size(1) fn main(){
                output[0]=data.first[inputs[0]][inputs[1]];
                output[1]=data.second[inputs[0]][inputs[1]];
                data.second=data.first;
                output[2]=data.second[inputs[0]][inputs[1]];output[3]=data.tail;
            }
            """;
        var binary = Layout(SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default)), rowMajor);
        uint data = binary.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "Data").Operands[0];
        var code = binary.Instructions.ToList(); int firstType = code.FindIndex(i => i.Opcode is >= 19 and <= 39);
        code.Insert(firstType, new((ushort)Op.MemberDecorate, [data, 2, rowMajor ? 5u : 4u]));
        code.Insert(firstType, new((ushort)Op.MemberDecorate, [data, 2, 7, 16]));
        return new() { Version = binary.Version, Bound = binary.Bound, Instructions = code };
    }

    internal static SpirvBinary EvaluationFixture(bool rowMajor)
    {
        string source = HelperSource.Replace("@compute @workgroup_size(1)",
            "fn advance()->u32{let mode=data.indices.y;data.indices.x=(data.indices.x+1u)%3u;return mode;} @compute @workgroup_size(1)", StringComparison.Ordinal)
            .Replace("nested(column,column,data.indices.y)", "nested(column,column,advance())", StringComparison.Ordinal)
            .Replace("data.matrix[data.indices.x][", "data.matrix[(data.indices.x+2u)%3u][", StringComparison.Ordinal);
        return PointerFixture(source, rowMajor, 4441);
    }

    internal static SpirvBinary PointerProducerFixture(string kind)
    {
        static SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        var code = new List<SpirvInstruction>
        {
            I(Op.Capability, 1), I(Op.Capability, 4441), I(Op.MemoryModel, 0, 1),
            I(Op.EntryPoint, new uint[] { 5, 100 }.Concat(SpirvBinary.StringWords("main")).ToArray()),
            I(Op.ExecutionMode, 100, 17, 1, 1, 1), I(Op.Decorate, 4, 2), I(Op.MemberDecorate, 4, 0, 35, 0),
            I(Op.Decorate, 11, 34, 0), I(Op.Decorate, 11, 33, 0),
            I(Op.TypeVoid, 1), I(Op.TypeBool, 2), I(Op.TypeInt, 3, 32, 0), I(Op.TypeStruct, 4, 3),
            I(Op.TypePointer, 5, 12, 4), I(Op.TypePointer, 6, 12, 3), I(Op.TypeFunction, 7, 1),
            I(Op.TypeFunction, 8, 6), I(Op.TypePointer, 12, 7, 6), I(Op.ConstantTrue, 2, 9),
            I(Op.Constant, 3, 10, 0), I(Op.Variable, 5, 11, 12)
        };
        if (kind == "return") code.AddRange([
            I(Op.Function, 6, 20, 0, 8), I(Op.Label, 21), I(Op.AccessChain, 6, 22, 11, 10),
            I(Op.ReturnValue, 22), I(Op.FunctionEnd)]);
        code.AddRange([I(Op.Function, 1, 100, 0, 7), I(Op.Label, 101)]);
        if (kind == "load") code.Add(I(Op.Variable, 12, 33, 7));
        code.Add(I(Op.AccessChain, 6, 30, 11, 10));
        code.AddRange(kind switch
        {
            "return" => [I(Op.FunctionCall, 6, 31, 20)],
            "select" => [I(Op.Select, 6, 31, 9, 30, 30)],
            "load" => [I(Op.Store, 33, 30), I(Op.Load, 6, 31, 33)],
            "phi" => [I(Op.SelectionMerge, 104, 0), I(Op.BranchConditional, 9, 102, 103),
                I(Op.Label, 102), I(Op.Branch, 104), I(Op.Label, 103), I(Op.Branch, 104),
                I(Op.Label, 104), I(Op.Phi, 6, 31, 30, 102, 30, 103)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
        code.AddRange([I(Op.Load, 3, 32, 31), I(Op.Return), I(Op.FunctionEnd)]);
        return new() { Version = 0x10300, Bound = 105, Instructions = code };
    }

    internal static SpirvBinary InitializerFixture(bool rowMajor, bool zero = false)
    {
        string initializer = zero ? "Data()" : "Data(vec4u(0u),mat3x2f(vec2f(11.0,12.0),vec2f(21.0,22.0),vec2f(31.0,32.0)),919.0)";
        string source = $$"""
            struct Data {indices:vec4u,matrix:mat3x2f,tail:f32,}
            @group(0) @binding(0) var<storage,read> inputs:array<u32>;
            @group(0) @binding(1) var<storage,read_write> output:array<f32>;
            var<private> data:Data={{initializer}};
            @compute @workgroup_size(1) fn main(){
                output[0]=data.matrix[inputs[0]][inputs[1]];
                data.matrix[inputs[0]][inputs[1]]=71.0;
                output[1]=data.matrix[inputs[0]][inputs[1]];output[2]=data.tail;
            }
            """;
        return Layout(SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default)), rowMajor);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void AliasedProjectedPointersAndNestedEarlyReturnsRetainTheirAddresses(bool rowMajor)
    {
        var module = SpirvReader.Parse(HelperFixture(rowMajor).ToBytes());
        ModuleValidator.Validate(module);
        Assert.DoesNotContain(module.Functions, f => f.Arguments.Any(a => a.Type is ShaderType.Pointer { Space: AddressSpace.Storage }));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void PrivateMatrixConstantsAreReconstructedInTheirPhysicalLayout(bool rowMajor, bool zero)
    {
        var module = SpirvReader.Parse(InitializerFixture(rowMajor, zero).ToBytes());
        ModuleValidator.Validate(module);
        Assert.Contains(module.Globals, g => g.Space == AddressSpace.Private && g.Initializer is not null);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void WholeAliasedMatrixHelpersRetainLayoutAcrossAddressSpaces(bool rowMajor, bool workgroup)
    {
        var module = SpirvReader.Parse(WholeHelperFixture(rowMajor, workgroup).ToBytes());
        ModuleValidator.Validate(module);
        Assert.DoesNotContain(module.Functions, f => f.Arguments.Any(a => a.Type is ShaderType.Pointer { Space: AddressSpace.Storage or AddressSpace.Workgroup }));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void PrivateMatrixArrayConstantsReconstructEveryElement(bool rowMajor, bool zero)
    {
        var module = SpirvReader.Parse(ArrayInitializerFixture(rowMajor, zero).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void SharedMatrixValueTypeKeepsIndependentMemberLayouts(bool rowMajor)
    {
        var module = SpirvReader.Parse(SharedInitializerFixture(rowMajor).ToBytes());
        ModuleValidator.Validate(module);
        var data = Assert.IsType<ShaderType.Structure>(module.Globals.Single(g => g.Space == AddressSpace.Private).Type);
        Assert.NotEqual(data.Members[1].Type, data.Members[2].Type);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Fact]
    public void PrivateDerivedPointerCallsExpandBeforeNativeEmission()
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(PrivateSource), SpirvCompilationTarget.Default));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.FunctionParameter);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    internal const string PrivateSource = """
        @group(0) @binding(0) var<storage,read> inputs:array<u32>;
        @group(0) @binding(1) var<storage,read_write> output:array<f32>;
        var<private> first=array<f32,2>(11.0,12.0);var<private> second=array<f32,2>(21.0,22.0);
        fn edit(p:ptr<private,f32>,q:ptr<private,f32>,mode:u32)->f32{
            *p=91.0;if mode==0u{return *q;}*q=*q+1.0;return *p+*q;
        }
        @compute @workgroup_size(1) fn main(){
            output[0]=edit(&first[inputs[0]],&second[inputs[1]],inputs[2]);
            output[1]=first[0];output[2]=first[1];output[3]=second[0];output[4]=second[1];
        }
        """;

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ProjectedPointerIndicesAreCapturedBeforeLaterArgumentEffects(bool rowMajor)
    {
        var module = SpirvReader.Parse(EvaluationFixture(rowMajor).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Fact]
    public void ReturnedStoragePointersRetainTheirFiniteProvenance()
    {
        var module = SpirvReader.Parse(PointerProducerFixture("return").ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData("select")] [InlineData("phi")] [InlineData("load")]
    public void NativePointerChoicesNowHaveAValidLowering(string kind)
    {
        var module = SpirvReader.Parse(PointerProducerFixture(kind).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }
}
