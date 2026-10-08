using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private readonly Dictionary<uint, uint[]> functionTypes = [];
        private readonly List<RawFunction> rawFunctions = [];
        private sealed record RawFunction(uint Id, ShaderFunction Function, List<RawBlock> Blocks);
        private sealed class RawBlock(uint id)
        {
            public uint Id { get; } = id;
            public List<SpirvInstruction> Instructions { get; } = [];
            public List<SpirvInstruction> Phis { get; } = [];
            public Block Body { get; } = new();
            public SpirvInstruction? Terminator { get; set; }
            public uint? Merge { get; set; }
            public uint? Continuing { get; set; }
        }

        private void ReadFunction(ref int index)
        {
            Count(4, 4); var a = current.Operands;
            Define(a[1]);
            if (!functionTypes.TryGetValue(a[3], out var signature) || signature[0] != a[0]) throw Error("Invalid function signature.");
            var function = new ShaderFunction(Name(a[1], "f")) { ReturnType = Type(a[0]) };
            if (function.ReturnType is ShaderType.Pointer) throw Error("Pointer return values require pointer provenance specialization.");
            functions.Add(a[1], function); module.Functions.Add(function);
            var raw = new RawFunction(a[1], function, []); rawFunctions.Add(raw);
            RawBlock? block = null;
            int parameter = 1;
            while (++index < binary.Instructions.Count)
            {
                current = binary.Instructions[index]; a = current.Operands;
                Op op = (Op)current.Opcode;
                if (op == Op.FunctionEnd)
                {
                    Count(0, 0);
                    if (parameter != signature.Length || raw.Blocks.Count == 0 || block?.Terminator is null) throw Error("Incomplete function.");
                    return;
                }
                if (op is Op.Line or Op.NoLine or Op.Nop) continue;
                if (SkipDebugInstruction()) continue;
                if (op == Op.FunctionParameter)
                {
                    Count(2, 2);
                    if (block is not null || parameter >= signature.Length || signature[parameter++] != a[0]) throw Error("Invalid function parameter.");
                    Define(a[1]);
                    ShaderType argumentType = Type(a[0]);
                    // UniformConstant acceleration-structure parameters are
                    // immutable resource references. Lower them to opaque values,
                    // as WGSL and the managed writer already pass such handles.
                    if (argumentType is ShaderType.Pointer { Space: AddressSpace.Handle, Base: ShaderType.AccelerationStructure acceleration }) argumentType = acceleration;
                    var arg = new FunctionArgument(Name(a[1], "a"), argumentType);
                    function.Arguments.Add(arg);
                    Expression parameterValue = new Expression.Reference(arg.Name, arg.Type);
                    values[a[1]] = arg.Type is ShaderType.Pointer ? new Expression.Unary("*", parameterValue, arg.Type) : parameterValue;
                    continue;
                }
                if (op == Op.Label)
                {
                    Count(1, 1); Define(a[0]);
                    if (block is not null && block.Terminator is null) throw Error("Block has no terminator.");
                    block = new(a[0]); raw.Blocks.Add(block); continue;
                }
                if (block is null || block.Terminator is not null) throw Error("Instruction is outside an open basic block.");
                if (op is Op.Branch or Op.BranchConditional or Op.Switch or Op.Return or Op.ReturnValue or Op.Kill or Op.Unreachable or Op.EmitMeshTasksEXT)
                {
                    block.Terminator = current; continue;
                }
                if (op == Op.SelectionMerge) { Count(2, 2); block.Merge = a[0]; continue; }
                if (op == Op.LoopMerge) { Count(3); block.Merge = a[0]; block.Continuing = a[1]; continue; }
                if (op == Op.Phi) { Count(4); if (a.Length % 2 != 0) throw Error("Invalid OpPhi operands."); block.Phis.Add(current); }
                else block.Instructions.Add(current);
                if (HasResult(op))
                {
                    Count(2); Define(a[1]); ShaderType resultType = Type(a[0]);
                    if (resultType is ShaderType.Pointer && op is Op.Phi or Op.Load)
                        throw Error("Merged or loaded pointers require pointer provenance specialization.");
                    values[a[1]] = new Expression.Reference(Name(a[1], "r"), resultType);
                    if (op == Op.Variable)
                    {
                        if (resultType is not ShaderType.Pointer pointer || a.Length < 3 || a[2] != 7) throw Error("Invalid local variable.");
                        // Initializers are constants, available before any function is read.
                        function.Body.Statements.Add(new Statement.Declare(Name(a[1], "r"), pointer.Base, a.Length == 4 ? Value(a[3]) : null));
                    }
                    else if (resultType is not (ShaderType.Void or ShaderType.Pointer or ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure) && op != Op.SampledImage)
                        function.Body.Statements.Add(new Statement.Declare(Name(a[1], "r"), resultType, null));
                }
            }
            throw Error("Missing OpFunctionEnd.");
        }

        private static bool HasResult(Op op) => op is not (Op.Store or Op.CopyMemory or Op.ControlBarrier or Op.MemoryBarrier or Op.ImageWrite or Op.AtomicStore or Op.CooperativeMatrixStoreKHR or Op.SetMeshOutputsEXT
            or Op.RayQueryInitializeKHR or Op.RayQueryTerminateKHR or Op.RayQueryGenerateIntersectionKHR or Op.RayQueryConfirmIntersectionKHR);

        private void ResolveFunctions()
        {
            foreach (var raw in rawFunctions)
            {
                resolvingMeshFunction = raw.Id;
                foreach (var block in raw.Blocks)
                    foreach (var instruction in block.Instructions)
                    {
                        current = instruction;
                        LowerInstruction(block.Body);
                    }
                var map = raw.Blocks.ToDictionary(b => b.Id);
                var body = Region(raw.Blocks[0].Id, null, null, null, null, null, new HashSet<uint>());
                raw.Function.Body.Statements.AddRange(body.Statements);

                Block Region(uint start, uint? stop, uint? loopHeader, uint? loopMerge, uint? loopContinue, uint? switchMerge, HashSet<uint> path)
                {
                    var result = new Block();
                    uint next = start;
                    while (next != stop)
                    {
                        if (next == switchMerge || next == loopMerge) { result.Statements.Add(new Statement.Break()); break; }
                        if (next == loopContinue || next == loopHeader) { result.Statements.Add(new Statement.Continue()); break; }
                        if (!map.TryGetValue(next, out var block)) throw Error($"Branch to undefined block %{next}.");
                        if (!path.Add(next)) throw Error("Unstructured control-flow cycle requires legalization.");
                        if (block.Continuing is uint continuing && block.Merge is uint merge)
                        {
                            var loopBody = new Block();
                            loopBody.Statements.AddRange(block.Body.Statements);
                            uint? first = EmitTerminator(block, loopBody, null, next, merge, continuing, null, new(path));
                            if (first is uint bodyStart)
                                loopBody.Statements.AddRange(Region(bodyStart, null, next, merge, continuing, null, new(path)).Statements);
                            Block tail = continuing == next ? new() : Region(continuing, next, next, merge, null, null, new(path));
                            NormalizeTaskContinuing(tail, loopBody);
                            Expression? breakIf = NormalizeContinuing(tail, $"continuing_break_{raw.Id}_{continuing}");
                            result.Statements.Add(new Statement.Loop(loopBody, tail, breakIf));
                            next = merge; continue;
                        }
                        result.Statements.AddRange(block.Body.Statements);
                        uint? destination = EmitTerminator(block, result, stop, loopHeader, loopMerge, loopContinue, switchMerge, path);
                        if (destination is null) break;
                        next = destination.Value;
                    }
                    return result;
                }

                uint? EmitTerminator(RawBlock block, Block result, uint? stop, uint? loopHeader, uint? loopMerge, uint? loopContinue, uint? switchMerge, HashSet<uint> path)
                {
                    current = block.Terminator ?? throw Error("Missing terminator.");
                    var a = current.Operands;
                    switch ((Op)current.Opcode)
                    {
                        case Op.Branch:
                            Count(1, 1); Edge(block.Id, a[0], result);
                            if (a[0] == stop) return null;
                            if (a[0] == switchMerge || a[0] == loopMerge) { result.Statements.Add(new Statement.Break()); return null; }
                            if (a[0] == loopContinue || a[0] == loopHeader) { result.Statements.Add(new Statement.Continue()); return null; }
                            return a[0];
                        case Op.BranchConditional:
                            Count(3, 5);
                            uint? merge = block.Continuing is null ? block.Merge : null;
                            Block arm(uint target)
                            {
                                var armResult = new Block(); Edge(block.Id, target, armResult);
                                if (target != merge && target != stop) armResult.Statements.AddRange(Region(target, merge ?? stop, loopHeader, loopMerge, loopContinue, switchMerge, new(path)).Statements);
                                return armResult;
                            }
                            result.Statements.Add(new Statement.If(Value(a[0]), arm(a[1]), arm(a[2])));
                            return merge;
                        case Op.Switch:
                            Count(2);
                            if ((a.Length - 2) % 2 != 0) throw Error("64-bit switch selectors are not supported yet.");
                            var groups = new Dictionary<uint, List<Expression.Literal>>();
                            for (int i = 2; i < a.Length; i += 2)
                            {
                                if (!groups.TryGetValue(a[i + 1], out var literals)) groups[a[i + 1]] = literals = [];
                                literals.Add(DecodeLiteral(Value(a[0]).Type, a.AsSpan(i, 1)) as Expression.Literal ?? throw Error("Invalid switch value."));
                            }
                            groups.TryAdd(a[1], []);
                            var cases = new List<SwitchCase>();
                            foreach (var (target, literals) in groups)
                            {
                                var caseBody = new Block(); Edge(block.Id, target, caseBody);
                                if (target != block.Merge) caseBody.Statements.AddRange(Region(target, block.Merge, loopHeader, loopMerge, loopContinue, block.Merge, new(path)).Statements);
                                cases.Add(new(literals, target == a[1], caseBody));
                            }
                            result.Statements.Add(new Statement.Switch(Value(a[0]), cases)); return block.Merge;
                        case Op.Return: Count(0, 0); result.Statements.Add(new Statement.Return()); return null;
                        case Op.ReturnValue: Count(1, 1); result.Statements.Add(new Statement.Return(Value(a[0]))); return null;
                        case Op.Kill: Count(0, 0); result.Statements.Add(new Statement.Kill()); return null;
                        case Op.EmitMeshTasksEXT:
                            Count(3, 4);
                            LowerTaskEmission(result, a); return null;
                        // No defined execution can reach this terminator; like the reference frontend,
                        // omit it. It commonly terminates an otherwise empty merge block.
                        case Op.Unreachable: Count(0, 0); return null;
                        default: throw Error("Invalid block terminator.");
                    }
                }

                void Edge(uint predecessor, uint target, Block result)
                {
                    if (!map.TryGetValue(target, out var block)) throw Error($"Branch to undefined block %{target}.");
                    // Phi values are simultaneous edge copies: snapshot every source before writing any destination.
                    var copies = new List<(Expression Target, Expression Source)>();
                    foreach (var phi in block.Phis)
                    {
                        current = phi; var a = phi.Operands;
                        uint? incoming = null;
                        for (int i = 2; i < a.Length; i += 2) if (a[i + 1] == predecessor)
                        {
                            if (incoming is not null) throw Error("Duplicate phi predecessor.");
                            incoming = a[i];
                        }
                        if (incoming is null) throw Error("Missing phi predecessor.");
                        string name = $"edge_{predecessor}_{target}_{a[1]}";
                        result.Statements.Add(new Statement.Declare(name, Type(a[0]), Value(incoming.Value), false));
                        copies.Add((Value(a[1]), new Expression.Reference(name, Type(a[0]))));
                    }
                    foreach (var (destination, source) in copies) result.Statements.Add(new Statement.Store(destination, source));
                }
            }
        }

        private void LowerInstruction(Block block)
        {
            Op op = (Op)current.Opcode; uint[] a = current.Operands;
            Expression V(int i) => Value(a[i]);
            ShaderType T() => Type(a[0]);
            void Result(Expression expression)
            {
                if (T() is ShaderType.Void) { block.Statements.Add(new Statement.Evaluate(expression)); return; }
                if (T() is ShaderType.Pointer or ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure || op == Op.SampledImage) values[a[1]] = expression;
                else block.Statements.Add(new Statement.Store(Value(a[1]), expression));
            }
            switch (op)
            {
                case Op.Variable: break;
                case Op.SetMeshOutputsEXT: Count(2, 2); LowerMeshCounts(block, V(0), V(1)); break;
                case Op.ImageTexelPointer: Count(5, 5); break;
                case Op.Load:
                    var loadMemory = MemoryAccess(3);
                    if (MeshAggregateCopy(block, V(2), V(1), true, loadMemory)) break;
                    if (AtomicAggregateCopy(block, V(2), V(1), true, loadMemory)) break;
                    MarkInterfaceAggregate(V(2).Type);
                    if (T() is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure)
                    {
                        if (loadMemory is { Flags: not 0 }) throw Error("Opaque resource load requires equivalent per-access memory lowering.");
                        Result(V(2));
                    }
                    else Result(MemoryLoad(V(2), T(), loadMemory)); break;
                case Op.RayQueryInitializeKHR:
                    Count(8, 8); AddRayStructure(RayQueryTypes.Descriptor);
                    block.Statements.Add(new Statement.Evaluate(new Expression.Call("spirvRayQueryInitializeKHR",
                        [RayPointer(V(0)), V(1), V(2), V(3), V(4), V(5), V(6), V(7)], new ShaderType.Void()))); break;
                case Op.RayQueryTerminateKHR: case Op.RayQueryConfirmIntersectionKHR:
                    Count(1, 1); block.Statements.Add(new Statement.Evaluate(new Expression.Call("spirv"+op, [RayPointer(V(0))], new ShaderType.Void()))); break;
                case Op.RayQueryGenerateIntersectionKHR:
                    Count(2, 2); block.Statements.Add(new Statement.Evaluate(new Expression.Call("spirvRayQueryGenerateIntersectionKHR", [RayPointer(V(0)), V(1)], new ShaderType.Void()))); break;
                case Op.RayQueryProceedKHR:
                    Count(3, 3); Result(new Expression.Call("spirvRayQueryProceedKHR", [RayPointer(V(2))], ShaderType.Bool)); break;
                case Op.CooperativeMatrixLoadKHR:
                    var cooperativeLoad = MemoryAccess(5); Result(new Expression.Call(CooperativeMemoryName(true, a[3]), [new Expression.Unary("&", V(2), V(2).Type), V(4)], T()) { MemoryAccess = cooperativeLoad }); break;
                case Op.CooperativeMatrixStoreKHR:
                    var cooperativeStore = MemoryAccess(4); block.Statements.Add(new Statement.Evaluate(new Expression.Call(CooperativeMemoryName(false, a[2]),
                        [V(1), new Expression.Unary("&", V(0), V(0).Type), V(3)], new ShaderType.Void()) { MemoryAccess = cooperativeStore })); break;
                case Op.CooperativeMatrixMulAddKHR:
                    Count(5, 6); if (a.Length == 6 && a[5] != 0) throw Error("Unsupported cooperative multiply-add operand flags.");
                    Result(new Expression.Call("coopMultiplyAdd", [V(2), V(3), V(4)], T())); break;
                case Op.RayQueryGetRayTMinKHR: case Op.RayQueryGetRayFlagsKHR:
                    Count(3, 3); Result(new Expression.Call("spirv"+op, [RayPointer(V(2))], T())); break;
                case Op.RayQueryGetIntersectionTypeKHR: case Op.RayQueryGetIntersectionTKHR:
                case Op.RayQueryGetIntersectionInstanceCustomIndexKHR: case Op.RayQueryGetIntersectionInstanceIdKHR:
                case Op.RayQueryGetIntersectionInstanceShaderBindingTableRecordOffsetKHR: case Op.RayQueryGetIntersectionGeometryIndexKHR:
                case Op.RayQueryGetIntersectionPrimitiveIndexKHR: case Op.RayQueryGetIntersectionBarycentricsKHR:
                case Op.RayQueryGetIntersectionFrontFaceKHR: case Op.RayQueryGetIntersectionObjectToWorldKHR:
                case Op.RayQueryGetIntersectionWorldToObjectKHR: case Op.RayQueryGetIntersectionTriangleVertexPositionsKHR:
                    Count(4, 4); Result(RayIntersectionValue(op, V(2), V(3), T())); break;
                case Op.Store:
                    var storeMemory = MemoryAccess(2);
                    if (MeshAggregateCopy(block, V(0), V(1), false, storeMemory)) break;
                    if (AtomicAggregateCopy(block, V(0), V(1), false, storeMemory)) break;
                    MarkInterfaceAggregate(V(0).Type);
                    block.Statements.Add(MemoryStore(V(0), V(1), storeMemory)); break;
                case Op.CopyMemory:
                    var (targetMemory, sourceMemory) = CopyMemoryAccess(); MarkInterfaceAggregate(V(0).Type); MarkInterfaceAggregate(V(1).Type);
                    string copyName = "sia_copy_" + current.WordOffset;
                    var copyType = ((ShaderType.Pointer)V(1).Type).Base;
                    if (AtomicData(copyType) || AtomicData(((ShaderType.Pointer)V(0).Type).Base))
                    {
                        copyType = atomicDataTypes.GetValueOrDefault(copyType, copyType);
                        var temporary = new Expression.Reference(copyName, new ShaderType.Pointer(copyType, AddressSpace.Function));
                        block.Statements.Add(new Statement.Declare(copyName, copyType, null));
                        if (!AtomicAggregateCopy(block, V(1), temporary, true, sourceMemory)) block.Statements.Add(new Statement.Store(temporary, MemoryLoad(V(1), copyType, sourceMemory)));
                        if (!AtomicAggregateCopy(block, V(0), new Expression.Load(temporary), false, targetMemory)) block.Statements.Add(MemoryStore(V(0), new Expression.Load(temporary), targetMemory));
                        break;
                    }
                    var copyValue = new Expression.Reference(copyName, copyType);
                    if (MeshAggregateCopy(block, V(1), copyValue, true, sourceMemory))
                    {
                        block.Statements.Insert(0, new Statement.Declare(copyName, copyType, null));
                        if (!MeshAggregateCopy(block, V(0), copyValue, false, targetMemory)) block.Statements.Add(MemoryStore(V(0), copyValue, targetMemory));
                    }
                    else
                    {
                        var writes = new Block();
                        if (MeshAggregateCopy(writes, V(0), copyValue, false, targetMemory))
                        {
                            // A projected destination may split into many writes.
                            // Snapshot the source exactly once before any of them,
                            // including volatile reads and overlapping aliases.
                            block.Statements.Add(new Statement.Declare(copyName, copyType, MemoryLoad(V(1), copyType, sourceMemory), false));
                            block.Statements.AddRange(writes.Statements);
                        }
                        else block.Statements.Add(MemoryStore(V(0), MemoryLoad(V(1), copyType, sourceMemory), targetMemory));
                    }
                    break;
                case Op.PtrAccessChain:
                    Count(4, 4);
                    if (V(2) is not Expression.Access elementAccess || elementAccess.Base.Type is not ShaderType.Pointer { Base: ShaderType.Array })
                        throw Error("Pointer arithmetic requires normalized array-element provenance.");
                    // Normalization emits only zero/one Element steps. WGSL array
                    // indices are 32-bit; wider captured addresses remain scalar
                    // state for comparisons, and narrow only at a memory access.
                    Expression NarrowIndex(Expression value) => value.Type == ShaderType.U32 ? value : new Expression.Convert(ShaderType.U32, value);
                    Expression offsetIndex = new Expression.Binary("+", NarrowIndex(elementAccess.Index), NarrowIndex(V(3)), ShaderType.U32);
                    string arithmeticIndex = "sia_pointer_index_" + current.WordOffset + "_" + pointerCaptureCounter++;
                    block.Statements.Add(new Statement.Declare(arithmeticIndex, ShaderType.U32, offsetIndex, false));
                    values[a[1]] = Index(elementAccess.Base, new Expression.Reference(arithmeticIndex, ShaderType.U32)); break;
                case Op.AccessChain: case Op.InBoundsAccessChain:
                    Count(3);
                    Expression access = V(2);
                    for (int i = 3; i < a.Length; i++)
                    {
                        Expression index = V(i);
                        if ((matrixLayouts.Count != 0 || variablePointers) && index is not Expression.Literal)
                        {
                            // Layout projections may use this index in several
                            // scalar accesses; retain AccessChain's evaluation time.
                            string indexName = "sia_matrix_index_" + current.WordOffset + "_" + i + "_" + pointerCaptureCounter++;
                            block.Statements.Add(new Statement.Declare(indexName, index.Type, index, false));
                            if (descriptorSnapshots.TryGetValue(a[i], out string? descriptor)) descriptorSnapshotNames[indexName] = descriptor;
                            index = new Expression.Reference(indexName, index.Type);
                        }
                        access = Index(access, index);
                    }
                    values[a[1]] = access; break;
                case Op.CompositeExtract:
                    Count(4); Expression extract = V(2);
                    for (int i = 3; i < a.Length; i++) extract = Index(extract, Expression.U32(a[i]));
                    Result(extract); break;
                case Op.CompositeConstruct: Count(2); Result(new Expression.Construct(T(), a[2..].Select(Value).ToArray())); break;
                case Op.CopyObject: Count(3, 3); Result(V(2)); break;
                case Op.VectorExtractDynamic: Count(4, 4); Result(Index(V(2), V(3))); break;
                case Op.CompositeInsert:
                    Count(5);
                    block.Statements.Add(new Statement.Store(V(1), V(3)));
                    Expression inserted = V(1);
                    for (int i = 4; i < a.Length; i++) inserted = Index(inserted, Expression.U32(a[i]));
                    block.Statements.Add(new Statement.Store(inserted, V(2))); break;
                case Op.VectorInsertDynamic:
                    Count(5, 5); block.Statements.Add(new Statement.Store(V(1), V(2)));
                    block.Statements.Add(new Statement.Store(Index(V(1), V(4)), V(3))); break;
                case Op.Transpose: Count(3, 3); Result(new Expression.Call("transpose", [V(2)], T())); break;
                case Op.VectorShuffle:
                    Count(5);
                    if (V(2).Type is not ShaderType.Vector v) throw Error("Invalid vector shuffle.");
                    var parts = new List<Expression>();
                    foreach (uint component in a[4..])
                        parts.Add(component == uint.MaxValue ? new Expression.Construct(v.Component, [])
                            : component < v.Size ? Index(V(2), Expression.U32(component)) : Index(V(3), Expression.U32(component - (uint)v.Size)));
                    Result(new Expression.Construct(T(), parts)); break;
                case Op.FunctionCall:
                    Count(3);
                    if (!functions.TryGetValue(a[2], out var function)) throw Error("Undefined called function.");
                    if (a.Length - 3 != function.Arguments.Count) throw Error("Function argument count mismatch.");
                    var arguments = a[3..].Select((id, index) => function.Arguments[index].Type is ShaderType.Pointer
                        ? (Expression)new Expression.Unary("&", Value(id), function.Arguments[index].Type) : Value(id)).ToArray();
                    Result(new Expression.Call(function.Name, arguments, T())); PropagateTaskTermination(block, a[2]); break;
                case Op.SNegate:
                    Count(3, 3);
                    var negationType = SpecIntegerType(T(), true);
                    Result(SpecCast(T(), new Expression.Unary("-", SpecCast(negationType, V(2)), negationType))); break;
                case Op.FNegate: case Op.Not: case Op.LogicalNot:
                    Count(3, 3); Result(new Expression.Unary(op == Op.Not ? "~" : op == Op.LogicalNot ? "!" : "-", V(2), T())); break;
                case Op.ConvertSToF: case Op.ConvertUToF:
                    Count(3, 3); Result(new Expression.Convert(T(), SpecCast(SpecIntegerType(V(2).Type, op == Op.ConvertSToF), V(2)))); break;
                case Op.ConvertFToU: case Op.ConvertFToS:
                    Count(3, 3); Result(SpecCast(T(), new Expression.Convert(SpecIntegerType(T(), op == Op.ConvertFToS), V(2)))); break;
                case Op.FConvert: case Op.Bitcast:
                    Count(3, 3); Result(new Expression.Convert(T(), V(2), op == Op.Bitcast)); break;
                case Op.UConvert: case Op.SConvert:
                    Count(3, 3);
                    var integerInput = SpecIntegerType(V(2).Type, op == Op.SConvert);
                    var integerOutput = SpecIntegerType(T(), op == Op.SConvert);
                    if (SpecComponent(integerInput).Width == SpecComponent(integerOutput).Width
                        || (integerInput is ShaderType.Vector ci ? ci.Size : 1) != (integerOutput is ShaderType.Vector co ? co.Size : 1))
                        throw Error("Invalid integer conversion shape or width.");
                    if (op == Op.UConvert && T() != integerOutput) throw Error("Unsigned conversion requires an unsigned result.");
                    Result(SpecCast(T(), SpecCast(integerOutput, SpecCast(integerInput, V(2))))); break;
                case Op.Select:
                    Count(5, 5); Expression condition = V(2);
                    if (T() is ShaderType.Pointer)
                    {
                        if (!variablePointers) throw Error("Pointer selection requires a variable-pointer capability.");
                        pointerSelection = true;
                        string name = "sia_pointer_condition_" + current.WordOffset + "_" + pointerCaptureCounter++;
                        block.Statements.Add(new Statement.Declare(name, condition.Type, condition, false));
                        condition = new Expression.Reference(name, condition.Type);
                    }
                    var acceptPointer = V(3); var rejectPointer = V(4);
                    if (T() is ShaderType.Pointer)
                    {
                        if (IsNullPointer(acceptPointer) && !IsNullPointer(rejectPointer)) acceptPointer = new Expression.Construct(rejectPointer.Type, []);
                        if (IsNullPointer(rejectPointer) && !IsNullPointer(acceptPointer)) rejectPointer = new Expression.Construct(acceptPointer.Type, []);
                    }
                    Result(new Expression.Select(condition, acceptPointer, rejectPointer)); break;
                case Op.Dot: case Op.OuterProduct:
                    Count(4, 4); Result(new Expression.Call(op == Op.Dot ? "dot" : "outerProduct", [V(2), V(3)], T())); break;
                case Op.Any: case Op.All: case Op.IsNan: case Op.IsInf:
                    Count(3, 3); Result(new Expression.Call(op switch { Op.Any => "any", Op.All => "all", Op.IsNan => "isNan", _ => "isInf" }, [V(2)], T())); break;
                case Op.BitCount: case Op.BitReverse: case Op.QuantizeToF16:
                    Count(3, 3); Result(new Expression.Call(op == Op.BitCount ? "countOneBits" : op == Op.BitReverse ? "reverseBits" : "quantizeToF16", [V(2)], T())); break;
                case Op.BitFieldInsert:
                    Count(6, 6); Result(new Expression.Call("insertBits", [V(2), V(3), new Expression.Convert(ShaderType.U32, V(4)), new Expression.Convert(ShaderType.U32, V(5))], T())); break;
                case Op.BitFieldSExtract: case Op.BitFieldUExtract:
                    Count(5, 5); Result(new Expression.Call("extractBits", [V(2), new Expression.Convert(ShaderType.U32, V(3)), new Expression.Convert(ShaderType.U32, V(4))], T())); break;
                case Op.DPdx: case Op.DPdy: case Op.Fwidth: case Op.DPdxFine: case Op.DPdyFine:
                case Op.FwidthFine: case Op.DPdxCoarse: case Op.DPdyCoarse: case Op.FwidthCoarse:
                    Count(3, 3); Result(new Expression.Call(op switch
                    {
                        Op.DPdx => "dpdx", Op.DPdy => "dpdy", Op.Fwidth => "fwidth", Op.DPdxFine => "dpdxFine",
                        Op.DPdyFine => "dpdyFine", Op.FwidthFine => "fwidthFine", Op.DPdxCoarse => "dpdxCoarse",
                        Op.DPdyCoarse => "dpdyCoarse", _ => "fwidthCoarse"
                    }, [V(2)], T())); break;
                case Op.ExtInst:
                    Count(4);
                    if (!imports.TryGetValue(a[2], out string? import) || import != "GLSL.std.450") throw Error("Unsupported extended instruction set.");
                    if (a[3] is 79 or 80 or 81)
                    {
                        Count(a[3] == 81 ? 7 : 6, a[3] == 81 ? 7 : 6);
                        ShaderType resultType = T();
                        ShaderType conditionType = resultType is ShaderType.Vector shape ? new ShaderType.Vector(shape.Size, ShaderType.Bool) : ShaderType.Bool;
                        Expression NumberMinMax(string name, Expression left, Expression right) => new Expression.Select(
                            new Expression.Call("isNan", [left], conditionType), right,
                            new Expression.Select(new Expression.Call("isNan", [right], conditionType), left,
                                new Expression.Call(name, [left, right], resultType)));
                        Result(a[3] == 81 ? NumberMinMax("min", NumberMinMax("max", V(4), V(5)), V(6))
                            : NumberMinMax(a[3] == 79 ? "min" : "max", V(4), V(5)));
                        break;
                    }
                    if (a[3] is 35 or 36 or 51 or 52)
                    {
                        bool modf = a[3] is 35 or 36, pointerOutput = a[3] is 35 or 51;
                        Count(pointerOutput ? 6 : 5, pointerOutput ? 6 : 5);
                        Expression input = V(4); ShaderType first = input.Type;
                        ShaderType second = modf ? first : first is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.I32) : ShaderType.I32;
                        string secondName = modf ? "whole" : "exp";
                        var resultType = new ShaderType.Structure("sia_builtin_result_" + a[1], [new("fract", first), new(secondName, second)], modf ? BuiltinResultKind.Modf : BuiltinResultKind.Frexp);
                        module.Structures.Add(resultType);
                        string name = "sia_builtin_value_" + a[1];
                        block.Statements.Add(new Statement.Declare(name, resultType, new Expression.Call(modf ? "modf" : "frexp", [input], resultType), false));
                        var reference = new Expression.Reference(name, resultType);
                        var fraction = new Expression.Member(reference, "fract", first);
                        var whole = new Expression.Member(reference, secondName, second);
                        if (pointerOutput)
                        {
                            block.Statements.Add(new Statement.Store(V(5), whole)); Result(fraction);
                        }
                        else Result(new Expression.Construct(T(), [fraction, whole]));
                        break;
                    }
                    Result(new Expression.Call(GlslFunction(a[3]), a[4..].Select(Value).ToArray(), T())); break;
                case Op.ArrayLength:
                    Count(4, 4); Expression member = Index(V(2), Expression.U32(a[3]));
                    Result(new Expression.Call("arrayLength", [new Expression.Unary("&", member, member.Type)], T())); break;
                case Op.GroupNonUniformAll: case Op.GroupNonUniformAny: case Op.GroupNonUniformBallot:
                case Op.GroupNonUniformBroadcast: case Op.GroupNonUniformBroadcastFirst:
                case Op.GroupNonUniformShuffle: case Op.GroupNonUniformShuffleXor: case Op.GroupNonUniformShuffleUp: case Op.GroupNonUniformShuffleDown:
                case Op.GroupNonUniformIAdd: case Op.GroupNonUniformFAdd: case Op.GroupNonUniformIMul: case Op.GroupNonUniformFMul:
                case Op.GroupNonUniformSMin: case Op.GroupNonUniformUMin: case Op.GroupNonUniformFMin:
                case Op.GroupNonUniformSMax: case Op.GroupNonUniformUMax: case Op.GroupNonUniformFMax:
                case Op.GroupNonUniformBitwiseAnd: case Op.GroupNonUniformBitwiseOr: case Op.GroupNonUniformBitwiseXor:
                case Op.GroupNonUniformLogicalAnd: case Op.GroupNonUniformLogicalOr: case Op.GroupNonUniformLogicalXor:
                case Op.GroupNonUniformQuadBroadcast: case Op.GroupNonUniformQuadSwap:
                    Result(Subgroup(op, a)); break;
                case Op.SampledImage:
                    Count(4, 4); sampledImageValues[a[1]] = (V(2), V(3)); break;
                case Op.Image:
                    Count(3, 3);
                    if (!sampledImageValues.TryGetValue(a[2], out var sampled)) throw Error("Undefined sampled image.");
                    Result(sampled.Image); break;
                case Op.ImageFetch: case Op.ImageRead:
                    Result(ImageLoad(a)); break;
                case Op.ImageWrite:
                    block.Statements.Add(new Statement.Evaluate(ImageStore(a))); break;
                case Op.ImageSampleImplicitLod: case Op.ImageSampleExplicitLod:
                case Op.ImageSampleDrefImplicitLod: case Op.ImageSampleDrefExplicitLod:
                case Op.ImageGather: case Op.ImageDrefGather:
                    Result(ImageSample(op, a)); break;
                case Op.ImageQuerySize: case Op.ImageQuerySizeLod: case Op.ImageQueryLevels: case Op.ImageQuerySamples:
                    Result(ImageQuery(op, a)); break;
                case Op.AtomicStore:
                    Count(4, 4); block.Statements.Add(new Statement.Evaluate(AtomicCall("atomicStore", V(0), [V(3)], new ShaderType.Void(), AtomicMemory(a[1], a[2])))); break;
                case Op.AtomicLoad: case Op.AtomicExchange: case Op.AtomicCompareExchange: case Op.AtomicCompareExchangeWeak:
                case Op.AtomicIIncrement: case Op.AtomicIDecrement: case Op.AtomicIAdd: case Op.AtomicISub: case Op.AtomicFAddEXT:
                case Op.AtomicSMin: case Op.AtomicUMin: case Op.AtomicSMax: case Op.AtomicUMax: case Op.AtomicAnd: case Op.AtomicOr: case Op.AtomicXor:
                    if (a.Length > 2 && imageTexelPointers.ContainsKey(a[2])) block.Statements.Add(new Statement.Evaluate(LowerImageAtomic(op, a)));
                    else Result(LowerAtomic(op, a));
                    break;
                case Op.ControlBarrier:
                    Count(3, 3);
                    uint scope = ConstantUint(a[0]);
                    uint semantics = ConstantUint(a[2]);
                    block.Statements.Add(new Statement.Barrier((semantics & 0x40) != 0, scope != 3 && (semantics & 0x100) != 0, (semantics & 0x800) != 0, scope == 3)
                        { NativeMemory = new(ConstantUint(a[1]), semantics, scope) }); break;
                case Op.MemoryBarrier:
                    Count(2, 2); uint memoryScope = ConstantUint(a[0]), memorySemantics = ConstantUint(a[1]);
                    block.Statements.Add(new Statement.MemoryBarrier((memorySemantics & 0x40) != 0, (memorySemantics & 0x100) != 0, (memorySemantics & 0x800) != 0, memoryScope == 3)
                        { NativeMemory = new(memoryScope, memorySemantics) }); break;
                default:
                    string? operation = BinaryOperator(op);
                    if (operation is null) throw Error($"Unsupported function instruction {op} ({current.Opcode}).");
                    Count(4, 4);
                    if (op is Op.LogicalAnd or Op.LogicalOr && T() is ShaderType.Vector)
                    {
                        Expression splat = new Expression.Construct(T(), [Expression.Bool(op == Op.LogicalOr)]);
                        Result(op == Op.LogicalAnd ? new Expression.Select(V(2), V(3), splat) : new Expression.Select(V(2), splat, V(3)));
                        break;
                    }
                    Expression left = V(2), right = V(3);
                    bool? signedOperation = op switch
                    {
                        Op.SDiv or Op.SRem or Op.ShiftRightArithmetic or Op.SLessThan or Op.SLessThanEqual or Op.SGreaterThan or Op.SGreaterThanEqual => true,
                        Op.UDiv or Op.UMod or Op.ShiftRightLogical or Op.ULessThan or Op.ULessThanEqual or Op.UGreaterThan or Op.UGreaterThanEqual => false,
                        _ => null
                    };
                    ShaderType operationType = T();
                    if (signedOperation is bool signed)
                    {
                        left = SpecCast(SpecIntegerType(left.Type, signed), left);
                        if (op is not (Op.ShiftRightLogical or Op.ShiftRightArithmetic)) right = SpecCast(SpecIntegerType(right.Type, signed), right);
                        if (SpecComponent(T()).Kind != ScalarKind.Bool) operationType = SpecIntegerType(T(), signed);
                    }
                    if (op is Op.ShiftLeftLogical or Op.ShiftRightLogical or Op.ShiftRightArithmetic)
                    {
                        ShaderType shiftType = right.Type is ShaderType.Vector rv ? new ShaderType.Vector(rv.Size, ShaderType.U32) : ShaderType.U32;
                        right = new Expression.Convert(shiftType, right);
                    }
                    Expression comparison = SpecCast(T(), new Expression.Binary(operation, left, right, operationType));
                    if (op is Op.FUnordEqual or Op.FUnordLessThan or Op.FUnordLessThanEqual or Op.FUnordGreaterThan or Op.FUnordGreaterThanEqual or Op.FOrdNotEqual)
                    {
                        Expression nan = new Expression.Binary("|", new Expression.Call("isNan", [V(2)], T()), new Expression.Call("isNan", [right], T()), T());
                        comparison = op == Op.FOrdNotEqual ? new Expression.Binary("&", new Expression.Unary("!", nan, T()), comparison, T()) : new Expression.Binary("|", nan, comparison, T());
                    }
                    Result(comparison); break;
            }
        }

        private void MarkInterfaceAggregate(ShaderType type)
        {
            if (type is ShaderType.Pointer pointer) type = pointer.Base;
            if (type is ShaderType.Structure structure)
                foreach (var member in structure.Members)
                {
                    if (member.Binding?.Builtin is string builtin) accessedInterfaceBuiltins.Add(builtin);
                    MarkInterfaceAggregate(member.Type);
                }
        }

        private Expression Index(Expression expression, Expression index)
        {
            if (ProjectMeshOutput(expression, index) is { } projected) return projected;
            var pointer = expression.Type as ShaderType.Pointer;
            ShaderType type = pointer?.Base ?? expression.Type;
            ShaderType Wrap(ShaderType value) => pointer is null ? value : new ShaderType.Pointer(value, pointer.Space, pointer.Access);
            switch (type)
            {
                case ShaderType.Structure structure:
                    if (index is not Expression.Literal literal) throw Error("Structure member index must be constant.");
                    uint member = literal.Value switch { uint u => u, int i when i >= 0 => (uint)i, _ => uint.MaxValue };
                    if (member >= structure.Members.Count) throw Error("Structure member index out of bounds.");
                    var field = structure.Members[(int)member];
                    if (field.Binding?.Builtin is string builtin) accessedInterfaceBuiltins.Add(builtin);
                    return new Expression.Member(expression, field.Name, Wrap(field.Type));
                case ShaderType.Array array: return new Expression.Access(expression, index, Wrap(array.Element));
                case ShaderType.BindingArray array: return new Expression.Access(expression, index, Wrap(array.Element));
                case ShaderType.Vector vector: return new Expression.Access(expression, index, Wrap(vector.Component));
                case ShaderType.Matrix matrix: return new Expression.Access(expression, index, Wrap(new ShaderType.Vector(matrix.Rows, matrix.Component)));
                default: throw Error("Cannot index this type.");
            }
        }

        private static string? BinaryOperator(Op op) => op switch
        {
            Op.IAdd or Op.FAdd => "+", Op.ISub or Op.FSub => "-",
            Op.IMul or Op.FMul or Op.VectorTimesScalar or Op.MatrixTimesScalar or Op.VectorTimesMatrix or Op.MatrixTimesVector or Op.MatrixTimesMatrix => "*",
            Op.UDiv or Op.SDiv or Op.FDiv => "/", Op.UMod or Op.SRem or Op.FRem => "%",
            Op.IEqual or Op.FOrdEqual or Op.FUnordEqual or Op.LogicalEqual => "==", Op.INotEqual or Op.FOrdNotEqual or Op.FUnordNotEqual or Op.LogicalNotEqual => "!=",
            Op.ULessThan or Op.SLessThan or Op.FOrdLessThan or Op.FUnordLessThan => "<", Op.ULessThanEqual or Op.SLessThanEqual or Op.FOrdLessThanEqual or Op.FUnordLessThanEqual => "<=",
            Op.UGreaterThan or Op.SGreaterThan or Op.FOrdGreaterThan or Op.FUnordGreaterThan => ">", Op.UGreaterThanEqual or Op.SGreaterThanEqual or Op.FOrdGreaterThanEqual or Op.FUnordGreaterThanEqual => ">=",
            Op.BitwiseAnd => "&", Op.BitwiseOr => "|", Op.BitwiseXor => "^",
            Op.LogicalAnd => "&&", Op.LogicalOr => "||", Op.ShiftLeftLogical => "<<", Op.ShiftRightLogical or Op.ShiftRightArithmetic => ">>",
            _ => null
        };

        private string GlslFunction(uint opcode) => opcode switch
        {
            1 => "round", 2 => "round", 3 => "trunc", 4 or 5 => "abs", 6 or 7 => "sign", 8 => "floor", 9 => "ceil",
            10 => "fract", 11 => "radians", 12 => "degrees", 13 => "sin", 14 => "cos", 15 => "tan", 16 => "asin",
            17 => "acos", 18 => "atan", 19 => "sinh", 20 => "cosh", 21 => "tanh", 22 => "asinh", 23 => "acosh",
            24 => "atanh", 25 => "atan2", 26 => "pow", 27 => "exp", 28 => "log", 29 => "exp2", 30 => "log2",
            31 => "sqrt", 32 => "inverseSqrt", 33 => "determinant", 37 or 38 or 39 => "min", 40 or 41 or 42 => "max",
            43 or 44 or 45 => "clamp", 46 => "mix", 48 => "step", 49 => "smoothstep", 50 => "fma", 53 => "ldexp",
            54 => "pack4x8snorm", 55 => "pack4x8unorm", 56 => "pack2x16snorm", 57 => "pack2x16unorm",
            58 => "pack2x16float", 60 => "unpack2x16snorm", 61 => "unpack2x16unorm", 62 => "unpack2x16float",
            63 => "unpack4x8snorm", 64 => "unpack4x8unorm", 66 => "length", 67 => "distance", 68 => "cross",
            69 => "normalize", 70 => "faceForward", 71 => "reflect", 72 => "refract", 73 => "firstTrailingBit",
            74 or 75 => "firstLeadingBit", _ => throw Error($"Unsupported GLSL.std.450 instruction {opcode}.")
        };
    }
}
