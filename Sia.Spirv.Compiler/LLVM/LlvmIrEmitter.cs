using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Sia.Spirv;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.LLVM;

public sealed class LlvmIrEmitter
{
    private readonly StringBuilder _body = new();
    private readonly HashSet<LlvmValueType> _bufferTypes = [];
    private readonly HashSet<uint> _stageInputs = [];
    private readonly HashSet<uint> _stageOutputs = [];
    private readonly HashSet<uint> _flatStageInputs = [];
    private readonly HashSet<uint> _flatStageOutputs = [];
    private readonly HashSet<int> _inlineCallStack = [];
    private SpirvKernelAbi _kernelAbi;
    private SpirvShaderStage _shaderStage;
    private SpirvWorkgroupSize _currentWorkgroupSize;
    private SpirvStructLayout? _structLayout;
    private bool _readsVertexIndex;
    private bool _readsInstanceIndex;
    private bool _readsFragmentPosition;
    private bool _writesPosition;
    private bool _usesTexture2DLoad;
    private bool _usesTexture2DSampleLevel;
    private bool _usesTexture2DArrayLoad;
    private bool _usesTexture2DArraySampleLevel;
    private bool _usesBarrier;
    private bool _usesMin;
    private bool _usesMax;
    private bool _usesInverseSqrt;
    private bool _usesDiscard;
    private bool _usesSqrt;
    private bool _usesSin;
    private bool _usesCos;
    private bool _usesPow;
    private bool _usesAbs;
    private ResolvedCall? _currentCall;
    private MetadataReader _reader = null!;
    private PEReader _peReader = null!;
    private int _nextValueId;

    public LlvmIrModule Emit(
        string assemblyPath,
        SpirvKernel kernel,
        SpirvKernelAbi kernelAbi = SpirvKernelAbi.Vulkan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(kernel);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        _peReader = peReader;
        var reader = peReader.GetMetadataReader();
        _reader = reader;
        var methodHandle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(kernel.MetadataToken);
        var method = reader.GetMethodDefinition(methodHandle);
        var methodBody = peReader.GetMethodBody(method.RelativeVirtualAddress);
        var localTypes = DecodeLocalTypes(reader, methodBody.LocalSignature);

        // Fresh view per call: SpirvFrontend's own ShaderCilView is bound to
        // a MetadataReader disposed by the time Emit() runs later. Cheap to
        // recompute; reusing a disposed PEReader's memory is not safe.
        using var intrinsics = IntrinsicCatalog.Open(assemblyPath);
        var resolver = new CilCallResolver(reader, intrinsics);
        var view = new ShaderCilView(kernel.ControlFlowGraph, resolver);

        _body.Clear();
        _bufferTypes.Clear();
        _stageInputs.Clear();
        _stageOutputs.Clear();
        _flatStageInputs.Clear();
        _flatStageOutputs.Clear();
        _inlineCallStack.Clear();
        _kernelAbi = kernelAbi;
        _shaderStage = kernel.Stage;
        _currentWorkgroupSize = kernel.WorkgroupSize;
        var structLayouts = kernel.Parameters
            .Select(static parameter => parameter.StructLayout)
            .Where(static layout => layout != null)
            .DistinctBy(static layout => layout!.Name)
            .ToArray();
        if (structLayouts.Length > 1) {
            throw new InvalidDataException(
                "A kernel currently supports one distinct storage-buffer struct type.");
        }
        _structLayout = structLayouts.SingleOrDefault();
        _readsVertexIndex = false;
        _readsInstanceIndex = false;
        _readsFragmentPosition = false;
        _writesPosition = false;
        _usesTexture2DLoad = false;
        _usesTexture2DSampleLevel = false;
        _usesTexture2DArrayLoad = false;
        _usesTexture2DArraySampleLevel = false;
        _usesBarrier = false;
        _usesMin = false;
        _usesMax = false;
        _usesInverseSqrt = false;
        _usesDiscard = false;
        _usesSqrt = false;
        _usesSin = false;
        _usesCos = false;
        _usesPow = false;
        _usesAbs = false;
        _currentCall = null;
        _nextValueId = 0;

        var parameterValues = new LlvmValue[kernel.Parameters.Count];
        var entryPoint = SanitizeIdentifier(kernel.Name);
        var prologue = new StringBuilder();
        EmitParameterGlobals(kernel, prologue, parameterValues);
        EmitLocalAllocations(
            localTypes,
            methodBody.LocalVariablesInitialized,
            prologue);
        EmitBlocks(view, localTypes, parameterValues, prologue);
        if (kernel.Stage == SpirvShaderStage.Vertex && !_writesPosition) {
            throw new InvalidDataException(
                $"Vertex shader '{kernel.QualifiedName}' must call Gpu.SetPosition.");
        }

        var module = new StringBuilder();
        module.AppendLine("target triple = \"spirv1.5-vulkan1.2-compute\"");
        module.AppendLine();
        EmitMatrixTypeDeclarations(module);
        EmitStructTypeDeclaration(module);
        EmitGlobalDeclarations(kernel, module);
        module.Append("define void @").Append(entryPoint).AppendLine("() #0 {");
        module.Append(_body);
        module.AppendLine("}");
        module.AppendLine();
        EmitIntrinsicDeclarations(kernel, module);
        EmitShaderAttributes(kernel, module);
        EmitLocationMetadata(module);
        return new LlvmIrModule(module.ToString(), entryPoint);
    }

    private void EmitBlocks(
        ShaderCilView view,
        IReadOnlyList<LlvmValueType> localTypes,
        IReadOnlyList<LlvmValue> parameters,
        StringBuilder prologue)
    {
        var reachableBlocks = view.ReachableBlocks.ToArray();
        var reachableBlockIds = reachableBlocks
            .Select(static block => block.Id)
            .ToHashSet();
        var blockIdsByOffset = reachableBlocks.ToDictionary(
            static block => block.StartOffset,
            static block => block.Id);
        var predecessorIds = Enumerable.Range(0, view.Graph.Blocks.Count)
            .Select(static _ => new List<int>())
            .ToArray();
        foreach (var block in reachableBlocks) {
            foreach (var successor in block.Successors) {
                if (reachableBlockIds.Contains(successor)) {
                    predecessorIds[successor].Add(block.Id);
                }
            }
        }
        var dominators = ComputeDominators(reachableBlocks, predecessorIds);

        var incomingStacks = new Dictionary<int, List<EvaluationStackEdge>>();
        var phis = new List<EvaluationStackPhi>();
        var phiMarkers = new Dictionary<int, string>();
        var blocksById = reachableBlocks.ToDictionary(static block => block.Id);
        var emittedBlockIds = new HashSet<int>();
        var pendingBlockIds = new Queue<int>();
        pendingBlockIds.Enqueue(0);

        // Only reachable blocks are emitted: a call the emitter does not
        // recognize inside dead code must never fail an otherwise-valid
        // shader (see ShaderCilView).
        while (pendingBlockIds.TryDequeue(out var blockId)) {
            if (emittedBlockIds.Contains(blockId)) {
                continue;
            }

            var block = blocksById[blockId];
            incomingStacks.TryGetValue(block.Id, out var availableEdges);
            if (block.Id != 0 && (availableEdges == null || availableEdges.Count == 0)) {
                continue;
            }
            if (predecessorIds[block.Id].Any(predecessorId =>
                !dominators[predecessorId].Contains(block.Id) &&
                !availableEdges!.Any(edge => edge.PredecessorId == predecessorId))) {
                continue;
            }

            emittedBlockIds.Add(block.Id);
            _body.Append("bb").Append(block.Id).AppendLine(":");
            var phiMarker = $"  ; sia.stack.phi.{block.Id}";
            phiMarkers.Add(block.Id, phiMarker);
            _body.AppendLine(phiMarker);
            if (block.Id == 0) {
                _body.Append(prologue);
            }

            var stack = new Stack<LlvmValue>();
            if (block.Id != 0) {
                if (!incomingStacks.TryGetValue(block.Id, out var edges) || edges.Count == 0) {
                    throw CreateUnsupported(
                        block.StartOffset,
                        "A reachable basic block does not have an emitted predecessor.");
                }

                var depth = edges[0].Values.Count;
                if (edges.Any(edge => edge.Values.Count != depth)) {
                    throw CreateUnsupported(
                        block.StartOffset,
                        "Evaluation stack depth differs between control-flow predecessors.");
                }

                if (predecessorIds[block.Id].Count > 1) {
                    for (var position = 0; position < depth; position++) {
                        var first = edges[0].Values[position];
                        var type = edges
                            .Skip(1)
                            .Aggregate(
                                first.Type,
                                (current, edge) => MergeNumericTypes(
                                    current,
                                    edge.Values[position].Type,
                                    block.StartOffset));
                        if (edges.Any(edge => edge.Values[position].IsReference != first.IsReference)) {
                            throw CreateUnsupported(
                                block.StartOffset,
                                "Evaluation stack reference shape differs between control-flow predecessors.");
                        }

                        var result = new LlvmValue(
                            NextValue("stack"),
                            type,
                            first.IsReference);
                        phis.Add(new EvaluationStackPhi(
                            block.Id,
                            block.StartOffset,
                            position,
                            result,
                            edges));
                        stack.Push(result);
                    }
                } else {
                    foreach (var value in edges[0].Values) {
                        stack.Push(value);
                    }
                }
            }

            var terminated = false;
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++) {
                var instruction = block.Instructions[instructionIndex];
                terminated = EmitInstruction(
                    view,
                    block,
                    instructionIndex,
                    instruction.OpCode,
                    instruction.Operand,
                    instruction.Offset,
                    localTypes,
                    parameters,
                    blockIdsByOffset,
                    stack) || terminated;
            }

            var outgoingStack = stack.Reverse().ToArray();
            foreach (var successor in block.Successors) {
                if (!reachableBlockIds.Contains(successor)) {
                    continue;
                }
                if (!incomingStacks.TryGetValue(successor, out var edges)) {
                    edges = [];
                    incomingStacks.Add(successor, edges);
                }
                edges.Add(new EvaluationStackEdge(block.Id, outgoingStack));
                pendingBlockIds.Enqueue(successor);
            }

            if (!terminated) {
                if (block.Successors.Count == 1) {
                    EmitLine($"br label %bb{block.Successors[0]}");
                } else if (block.Successors.Count == 0) {
                    EmitLine("ret void");
                } else {
                    throw CreateUnsupported(
                        block.Instructions[^1].Offset,
                        "A basic block with multiple successors requires an explicit conditional branch.");
                }
            }
        }

        if (emittedBlockIds.Count != reachableBlocks.Length) {
            var missingBlock = reachableBlocks.First(block => !emittedBlockIds.Contains(block.Id));
            throw CreateUnsupported(
                missingBlock.StartOffset,
                "A reachable basic block could not be scheduled after its predecessors.");
        }

        foreach (var block in reachableBlocks) {
            var replacement = new StringBuilder();
            foreach (var phi in phis.Where(phi => phi.BlockId == block.Id)) {
                if (phi.IncomingEdges.Count != predecessorIds[block.Id].Count) {
                    throw CreateUnsupported(
                        phi.BlockOffset,
                        "Not all evaluation-stack predecessors reached a merge block.");
                }

                var llvmType = phi.Result.IsReference ? "ptr" : GetLlvmType(phi.Result.Type);
                replacement.Append("  ").Append(phi.Result.Expression)
                    .Append(" = phi ").Append(llvmType).Append(' ');
                for (var index = 0; index < phi.IncomingEdges.Count; index++) {
                    var edge = phi.IncomingEdges[index];
                    var value = edge.Values[phi.Position];
                    var mergedType = MergeNumericTypes(
                        phi.Result.Type,
                        value.Type,
                        phi.BlockOffset);
                    if (mergedType != phi.Result.Type ||
                        value.IsReference != phi.Result.IsReference) {
                        throw CreateUnsupported(
                            phi.BlockOffset,
                            "Evaluation stack type differs between control-flow predecessors.");
                    }
                    if (index != 0) {
                        replacement.Append(", ");
                    }
                    replacement.Append("[ ").Append(value.Expression)
                        .Append(", %bb").Append(edge.PredecessorId).Append(" ]");
                }
                replacement.AppendLine();
            }
            _body.Replace(phiMarkers[block.Id] + Environment.NewLine, replacement.ToString());
        }
    }

    private static IReadOnlyList<HashSet<int>> ComputeDominators(
        IReadOnlyList<CilBasicBlock> reachableBlocks,
        IReadOnlyList<List<int>> predecessorIds)
    {
        var reachableBlockIds = reachableBlocks
            .Select(static block => block.Id)
            .ToHashSet();
        var dominators = Enumerable.Range(0, predecessorIds.Count)
            .Select(id => id == 0 ? new HashSet<int> { 0 } : new HashSet<int>(reachableBlockIds))
            .ToArray();

        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in reachableBlocks.Skip(1)) {
                var predecessors = predecessorIds[block.Id]
                    .Where(reachableBlockIds.Contains)
                    .ToArray();
                var next = predecessors.Length == 0
                    ? []
                    : new HashSet<int>(dominators[predecessors[0]]);
                foreach (var predecessor in predecessors.Skip(1)) {
                    next.IntersectWith(dominators[predecessor]);
                }
                next.Add(block.Id);
                if (!dominators[block.Id].SetEquals(next)) {
                    dominators[block.Id] = next;
                    changed = true;
                }
            }
        }

        return dominators;
    }

    private bool EmitInstruction(
        ShaderCilView view,
        CilBasicBlock block,
        int instructionIndex,
        OpCode opCode,
        object? operand,
        int offset,
        IReadOnlyList<LlvmValueType> localTypes,
        IReadOnlyList<LlvmValue> parameters,
        IReadOnlyDictionary<int, int> blockIdsByOffset,
        Stack<LlvmValue> stack)
    {
        if (opCode == OpCodes.Nop) {
            return false;
        }
        if (TryGetArgumentIndex(opCode, operand, out var argumentIndex)) {
            stack.Push(parameters[argumentIndex]);
            return false;
        }
        if (TryGetArgumentAddressIndex(opCode, operand, out argumentIndex)) {
            stack.Push(parameters[argumentIndex]);
            return false;
        }
        if (TryGetLocalIndex(opCode, operand, true, out var localIndex)) {
            var type = localTypes[localIndex];
            var value = NextValue();
            EmitLine($"{value} = load {GetLlvmType(type)}, ptr %local.{localIndex}, align {GetAlignment(type)}");
            stack.Push(new LlvmValue(value, type));
            return false;
        }
        if (TryGetLocalIndex(opCode, operand, false, out localIndex)) {
            var value = Pop(stack, offset);
            var type = localTypes[localIndex];
            EnsureCompatible(type, value.Type, offset);
            EmitLine($"store {GetLlvmType(type)} {value.Expression}, ptr %local.{localIndex}, align {GetAlignment(type)}");
            return false;
        }
        if (TryGetLocalAddressIndex(opCode, operand, out localIndex)) {
            stack.Push(new LlvmValue($"%local.{localIndex}", localTypes[localIndex], true));
            return false;
        }
        if (TryGetInt32Constant(opCode, operand, out var constant)) {
            stack.Push(new LlvmValue(constant.ToString(System.Globalization.CultureInfo.InvariantCulture), LlvmValueType.Int32));
            return false;
        }
        if (opCode == OpCodes.Ldc_R4) {
            stack.Push(new LlvmValue(FormatFloat((float)operand!), LlvmValueType.Float32));
            return false;
        }
        if (opCode == OpCodes.Dup) {
            stack.Push(stack.Peek());
            return false;
        }
        if (opCode == OpCodes.Pop) {
            Pop(stack, offset);
            return false;
        }
        if (opCode == OpCodes.Not) {
            EmitNot(offset, stack);
            return false;
        }
        if (IsBinaryArithmetic(opCode)) {
            EmitBinary(opCode, offset, stack);
            return false;
        }
        if (opCode == OpCodes.Neg) {
            EmitNegate(offset, stack);
            return false;
        }
        if (opCode == OpCodes.Ceq || opCode == OpCodes.Clt || opCode == OpCodes.Clt_Un ||
            opCode == OpCodes.Cgt || opCode == OpCodes.Cgt_Un) {
            EmitComparison(opCode, offset, stack);
            return false;
        }
        if (IsConversion(opCode)) {
            EmitConversion(opCode, offset, stack);
            return false;
        }
        if (opCode == OpCodes.Call) {
            EmitCall(view, block, instructionIndex, offset, stack);
            return false;
        }
        if (opCode == OpCodes.Newobj) {
            EmitNewobj(view, block, instructionIndex, offset, stack);
            return false;
        }
        if (IsLoadIndirect(opCode)) {
            EmitLoadIndirect(opCode, offset, stack);
            return false;
        }
        if (opCode == OpCodes.Ldobj) {
            EmitLoadObject((int)operand!, offset, stack);
            return false;
        }
        if (IsStoreIndirect(opCode)) {
            EmitStoreIndirect(offset, stack);
            return false;
        }
        if (opCode == OpCodes.Stobj) {
            EmitStoreIndirect(offset, stack);
            return false;
        }
        if (opCode == OpCodes.Br || opCode == OpCodes.Br_S) {
            EmitLine($"br label %bb{blockIdsByOffset[(int)operand!]}");
            return true;
        }
        if (opCode == OpCodes.Brtrue || opCode == OpCodes.Brtrue_S ||
            opCode == OpCodes.Brfalse || opCode == OpCodes.Brfalse_S) {
            var condition = ToBoolean(Pop(stack, offset), offset);
            var target = blockIdsByOffset[(int)operand!];
            var fallthrough = GetFallthroughBlock(offset, blockIdsByOffset);
            if (opCode == OpCodes.Brfalse || opCode == OpCodes.Brfalse_S) {
                (target, fallthrough) = (fallthrough, target);
            }
            EmitLine($"br i1 {condition.Expression}, label %bb{target}, label %bb{fallthrough}");
            return true;
        }
        if (IsRelationalBranch(opCode)) {
            var condition = EmitBranchComparison(opCode, offset, stack);
            var target = blockIdsByOffset[(int)operand!];
            var fallthrough = GetFallthroughBlock(offset, blockIdsByOffset);
            EmitLine($"br i1 {condition.Expression}, label %bb{target}, label %bb{fallthrough}");
            return true;
        }
        if (opCode == OpCodes.Switch) {
            EmitSwitch((int[])operand!, offset, blockIdsByOffset, stack);
            return true;
        }
        if (opCode == OpCodes.Ret) {
            EmitLine("ret void");
            return true;
        }

        throw CreateUnsupported(offset, $"CIL opcode '{opCode.Name}' is not supported by the LLVM backend.");
    }

    private void EmitParameterGlobals(
        SpirvKernel kernel,
        StringBuilder prologue,
        IList<LlvmValue> values)
    {
        var binding = 0;
        var pushConstantIndex = 0;
        var hasPushConstants = kernel.Parameters.Any(
            static parameter => parameter.Kind == SpirvKernelParameterKind.PushConstant);
        string? pushConstantValue = null;
        string? parameterHandle = null;
        if (hasPushConstants && _kernelAbi == SpirvKernelAbi.Vulkan) {
            pushConstantValue = NextValue("push.constants");
            prologue.Append("  ").Append(pushConstantValue)
                .Append(" = load %sia.push.constants, ptr addrspace(13) @sia.push.constants, align 4")
                .AppendLine();
        } else if (hasPushConstants) {
            parameterHandle = NextValue("parameters");
            prologue.Append("  ").Append(parameterHandle).Append(" = call ")
                .Append(GetParameterTargetType()).Append(" @llvm.spv.resource.handlefrombinding.")
                .Append(GetParameterMangling()).Append("(i32 0, i32 ")
                .Append(kernel.Parameters.Count(static parameter => parameter.IsResource))
                .AppendLine(", i32 1, i32 0, ptr nonnull @.str.parameters)");
        }

        foreach (var parameter in kernel.Parameters) {
            if (parameter.Kind == SpirvKernelParameterKind.SampledTexture2D) {
                var value = NextValue(SanitizeIdentifier(parameter.Name));
                prologue.Append("  ").Append(value).Append(" = call ")
                    .Append(GetTexture2DTargetType()).Append(" @llvm.spv.resource.handlefrombinding.")
                    .Append(GetTexture2DMangling()).Append("(i32 0, i32 ").Append(binding)
                    .Append(", i32 1, i32 0, ptr nonnull @.str.")
                    .Append(parameter.Position).AppendLine(")");
                values[parameter.Position] = new LlvmValue(value, LlvmValueType.Texture2DFloat);
                binding++;
            } else if (parameter.Kind == SpirvKernelParameterKind.SampledTexture2DArray) {
                var value = NextValue(SanitizeIdentifier(parameter.Name));
                prologue.Append("  ").Append(value).Append(" = call ")
                    .Append(GetTexture2DArrayTargetType())
                    .Append(" @llvm.spv.resource.handlefrombinding.")
                    .Append(GetTexture2DArrayMangling()).Append("(i32 0, i32 ").Append(binding)
                    .Append(", i32 1, i32 0, ptr nonnull @.str.")
                    .Append(parameter.Position).AppendLine(")");
                values[parameter.Position] = new LlvmValue(
                    value, LlvmValueType.Texture2DArrayFloat);
                binding++;
            } else if (parameter.Kind == SpirvKernelParameterKind.Sampler) {
                var value = NextValue(SanitizeIdentifier(parameter.Name));
                prologue.Append("  ").Append(value).Append(" = call ")
                    .Append(GetSamplerTargetType())
                    .Append(" @llvm.spv.resource.handlefrombinding.")
                    .Append(GetSamplerMangling()).Append("(i32 0, i32 ").Append(binding)
                    .Append(", i32 1, i32 0, ptr nonnull @.str.")
                    .Append(parameter.Position).AppendLine(")");
                values[parameter.Position] = new LlvmValue(value, LlvmValueType.Sampler);
                binding++;
            } else if (parameter.Kind is SpirvKernelParameterKind.ReadOnlyStorageBuffer or
                      SpirvKernelParameterKind.StorageBuffer) {
                var type = GetBufferType(
                    parameter.ScalarType,
                    parameter.Kind == SpirvKernelParameterKind.ReadOnlyStorageBuffer);
                _bufferTypes.Add(type);
                var value = NextValue(SanitizeIdentifier(parameter.Name));
                var targetType = GetBufferTargetType(type);
                var mangling = GetBufferMangling(type);
                prologue.Append("  ").Append(value).Append(" = call ")
                    .Append(targetType).Append(" @llvm.spv.resource.handlefrombinding.")
                    .Append(mangling).Append("(i32 0, i32 ").Append(binding)
                    .Append(", i32 1, i32 0, ptr nonnull @.str.")
                    .Append(parameter.Position).AppendLine(")");
                values[parameter.Position] = new LlvmValue(value, type);
                binding++;
            } else if (parameter.Kind == SpirvKernelParameterKind.WorkgroupMemory) {
                var type = GetWorkgroupType(parameter.ScalarType);
                values[parameter.Position] = new LlvmValue(
                    $"@sia.workgroup.{parameter.Position}",
                    type);
            } else {
                var type = GetScalarType(parameter.ScalarType);
                string value;
                if (_kernelAbi == SpirvKernelAbi.Vulkan) {
                    value = NextValue(SanitizeIdentifier(parameter.Name));
                    prologue.Append("  ").Append(value).Append(" = extractvalue %sia.push.constants ")
                        .Append(pushConstantValue).Append(", ").Append(pushConstantIndex).AppendLine();
                } else {
                    var identifier = SanitizeIdentifier(parameter.Name);
                    var pointer = NextValue($"{identifier}.pointer");
                    var word = NextValue($"{identifier}.word");
                    prologue.Append("  ").Append(pointer).Append(" = call ptr addrspace(11) ")
                        .Append("@llvm.spv.resource.getpointer.p11.").Append(GetParameterMangling())
                        .Append('(').Append(GetParameterTargetType()).Append(' ').Append(parameterHandle)
                        .Append(", i32 ").Append(pushConstantIndex).AppendLine(")");
                    prologue.Append("  ").Append(word).Append(" = load i32, ptr addrspace(11) ")
                        .Append(pointer).AppendLine(", align 4");
                    if (type == LlvmValueType.Float32) {
                        value = NextValue(identifier);
                        prologue.Append("  ").Append(value).Append(" = bitcast i32 ")
                            .Append(word).AppendLine(" to float");
                    } else {
                        value = word;
                    }
                }
                values[parameter.Position] = new LlvmValue(value, type);
                pushConstantIndex++;
            }
        }
    }

    private void EmitLocalAllocations(
        IReadOnlyList<LlvmValueType> localTypes,
        bool initializeLocals,
        StringBuilder prologue)
    {
        for (var index = 0; index < localTypes.Count; index++) {
            var type = localTypes[index];
            prologue.Append("  %local.").Append(index).Append(" = alloca ")
                .Append(GetLlvmType(type)).Append(", align ").Append(GetAlignment(type)).AppendLine();
            if (initializeLocals) {
                prologue.Append("  store ").Append(GetLlvmType(type))
                    .Append(" zeroinitializer, ptr %local.").Append(index)
                    .Append(", align ").Append(GetAlignment(type)).AppendLine();
            }
        }
    }

    private void EmitGlobalDeclarations(SpirvKernel kernel, StringBuilder module)
    {
        var pushConstants = kernel.Parameters
            .Where(static parameter => parameter.Kind == SpirvKernelParameterKind.PushConstant)
            .ToArray();
        if (pushConstants.Length != 0 && _kernelAbi == SpirvKernelAbi.Vulkan) {
            module.Append("%sia.push.constants = type { ");
            module.Append(string.Join(", ", pushConstants.Select(parameter =>
                GetLlvmType(GetScalarType(parameter.ScalarType)))));
            module.AppendLine(" }");
            module.AppendLine("@sia.push.constants = external addrspace(13) global %sia.push.constants");
            module.AppendLine();
        }

        EmitStageGlobalDeclarations(module);

        foreach (var parameter in kernel.Parameters.Where(static parameter =>
            parameter.Kind == SpirvKernelParameterKind.WorkgroupMemory)) {
            var type = GetWorkgroupType(parameter.ScalarType);
            var elementType = GetWorkgroupElementType(type);
            var length = checked((int)(
                kernel.WorkgroupSize.X * kernel.WorkgroupSize.Y * kernel.WorkgroupSize.Z));
            module.Append("@sia.workgroup.").Append(parameter.Position)
                .Append(" = internal addrspace(3) global [").Append(length).Append(" x ")
                .Append(GetLlvmType(elementType)).Append("] zeroinitializer, align ")
                .Append(GetAlignment(elementType)).AppendLine();
        }
        if (kernel.Parameters.Any(static parameter =>
            parameter.Kind == SpirvKernelParameterKind.WorkgroupMemory)) {
            module.AppendLine();
        }

        foreach (var parameter in kernel.Parameters.Where(
            static parameter => parameter.IsResource)) {
            EmitResourceName(module, $".str.{parameter.Position}", parameter.Name);
        }
        if (pushConstants.Length != 0 && _kernelAbi == SpirvKernelAbi.WebGpu) {
            EmitResourceName(module, ".str.parameters", "sia.parameters");
        }
        if (kernel.Parameters.Count != 0) {
            module.AppendLine();
        }
    }

    private void EmitStageGlobalDeclarations(StringBuilder module)
    {
        if (_readsVertexIndex) {
            module.AppendLine(
                "@__spirv_BuiltInVertexIndex = external hidden addrspace(7) global i32");
        }
        if (_readsInstanceIndex) {
            module.AppendLine(
                "@__spirv_BuiltInInstanceIndex = external hidden addrspace(7) global i32");
        }
        if (_readsFragmentPosition) {
            module.AppendLine(
                "@__spirv_BuiltInFragCoord = external hidden addrspace(7) global <4 x float>");
        }
        if (_writesPosition) {
            module.AppendLine(
                "@__spirv_BuiltInPosition = external hidden addrspace(8) global <4 x float>");
        }
        foreach (var location in _stageInputs.Order()) {
            module.Append("@sia.input.location.").Append(location)
                .Append(" = external hidden addrspace(7) global <4 x float>, !spirv.Decorations !")
                .Append(GetLocationMetadataId(location)).AppendLine();
        }
        foreach (var location in _stageOutputs.Order()) {
            module.Append("@sia.output.location.").Append(location)
                .Append(" = external hidden addrspace(8) global <4 x float>, !spirv.Decorations !")
                .Append(GetLocationMetadataId(location)).AppendLine();
        }
        if (_readsVertexIndex || _readsInstanceIndex || _readsFragmentPosition ||
            _writesPosition || _stageInputs.Count != 0 || _stageOutputs.Count != 0) {
            module.AppendLine();
        }
    }

    private void EmitIntrinsicDeclarations(SpirvKernel kernel, StringBuilder module)
    {
        if (kernel.Stage == SpirvShaderStage.Compute) {
            module.AppendLine("declare i32 @llvm.spv.thread.id.i32(i32)");
            module.AppendLine("declare i32 @llvm.spv.thread.id.in.group.i32(i32)");
            module.AppendLine("declare i32 @llvm.spv.group.id.i32(i32)");
        }
        foreach (var type in _bufferTypes
            .OrderBy(static type => type)
            .DistinctBy(GetBufferMangling)) {
            var targetType = GetBufferTargetType(type);
            var mangling = GetBufferMangling(type);
            module.Append("declare ").Append(targetType)
                .Append(" @llvm.spv.resource.handlefrombinding.").Append(mangling)
                .AppendLine("(i32, i32, i32, i32, ptr)");
            module.Append("declare ptr addrspace(11) @llvm.spv.resource.getpointer.p11.")
                .Append(mangling).Append('(').Append(targetType).AppendLine(", i32)");
        }
        if (kernel.Parameters.Any(static parameter =>
            parameter.Kind == SpirvKernelParameterKind.SampledTexture2D)) {
            module.Append("declare ").Append(GetTexture2DTargetType())
                .Append(" @llvm.spv.resource.handlefrombinding.").Append(GetTexture2DMangling())
                .AppendLine("(i32, i32, i32, i32, ptr)");
        }
        if (kernel.Parameters.Any(static parameter =>
            parameter.Kind == SpirvKernelParameterKind.SampledTexture2DArray)) {
            module.Append("declare ").Append(GetTexture2DArrayTargetType())
                .Append(" @llvm.spv.resource.handlefrombinding.")
                .Append(GetTexture2DArrayMangling())
                .AppendLine("(i32, i32, i32, i32, ptr)");
        }
        if (kernel.Parameters.Any(static parameter =>
            parameter.Kind == SpirvKernelParameterKind.Sampler)) {
            module.Append("declare ").Append(GetSamplerTargetType())
                .Append(" @llvm.spv.resource.handlefrombinding.")
                .Append(GetSamplerMangling())
                .AppendLine("(i32, i32, i32, i32, ptr)");
        }
        if (_usesTexture2DLoad) {
            module.Append("declare <4 x float> @llvm.spv.resource.load.level.")
                .Append(GetTexture2DLoadMangling()).Append('(')
                .Append(GetTexture2DTargetType())
                .AppendLine(", <2 x i32>, i32, <2 x i32>)");
        }
        if (_usesTexture2DSampleLevel) {
            module.Append("declare <4 x float> @llvm.spv.resource.samplelevel.")
                .Append(GetTexture2DSampleLevelMangling()).Append('(')
                .Append(GetTexture2DTargetType()).Append(", ")
                .Append(GetSamplerTargetType())
                .AppendLine(", <2 x float>, float, <2 x i32>)");
        }
        if (_usesTexture2DArrayLoad) {
            module.Append("declare <4 x float> @llvm.spv.resource.load.level.")
                .Append(GetTexture2DArrayLoadMangling()).Append('(')
                .Append(GetTexture2DArrayTargetType())
                .AppendLine(", <3 x i32>, i32, <2 x i32>)");
        }
        if (_usesTexture2DArraySampleLevel) {
            module.Append("declare <4 x float> @llvm.spv.resource.samplelevel.")
                .Append(GetTexture2DArraySampleLevelMangling()).Append('(')
                .Append(GetTexture2DArrayTargetType()).Append(", ")
                .Append(GetSamplerTargetType())
                .AppendLine(", <3 x float>, float, <2 x i32>)");
        }
        if (_usesBarrier) {
            module.AppendLine(
                "declare void @llvm.spv.group.memory.barrier.with.group.sync()");
        }
        if (_usesMin) {
            module.AppendLine("declare float @llvm.minnum.f32(float, float)");
        }
        if (_usesMax) {
            module.AppendLine("declare float @llvm.maxnum.f32(float, float)");
        }
        if (_usesInverseSqrt) {
            module.AppendLine("declare float @llvm.spv.rsqrt.f32(float)");
        }
        if (_usesDiscard) {
            module.AppendLine("declare void @llvm.spv.discard()");
        }
        if (_usesSqrt) {
            module.AppendLine("declare float @llvm.sqrt.f32(float)");
        }
        if (_usesSin) {
            module.AppendLine("declare float @llvm.sin.f32(float)");
        }
        if (_usesCos) {
            module.AppendLine("declare float @llvm.cos.f32(float)");
        }
        if (_usesPow) {
            module.AppendLine("declare float @llvm.pow.f32(float, float)");
        }
        if (_usesAbs) {
            module.AppendLine("declare float @llvm.fabs.f32(float)");
        }
        if (_kernelAbi == SpirvKernelAbi.WebGpu &&
            kernel.Parameters.Any(static parameter =>
                parameter.Kind == SpirvKernelParameterKind.PushConstant) &&
            !_bufferTypes.Contains(LlvmValueType.ReadOnlyBufferUInt32)) {
            var targetType = GetParameterTargetType();
            var mangling = GetParameterMangling();
            module.Append("declare ").Append(targetType)
                .Append(" @llvm.spv.resource.handlefrombinding.").Append(mangling)
                .AppendLine("(i32, i32, i32, i32, ptr)");
            module.Append("declare ptr addrspace(11) @llvm.spv.resource.getpointer.p11.")
                .Append(mangling).Append('(').Append(targetType).AppendLine(", i32)");
        }
        module.AppendLine();
    }

    private static void EmitShaderAttributes(SpirvKernel kernel, StringBuilder module)
    {
        if (kernel.Stage == SpirvShaderStage.Compute) {
            module.Append("attributes #0 = { \"hlsl.numthreads\"=\"")
                .Append(kernel.WorkgroupSize.X).Append(',')
                .Append(kernel.WorkgroupSize.Y).Append(',')
                .Append(kernel.WorkgroupSize.Z)
                .AppendLine("\" \"hlsl.shader\"=\"compute\" }");
            return;
        }

        var stage = kernel.Stage == SpirvShaderStage.Vertex ? "vertex" : "pixel";
        module.Append("attributes #0 = { \"hlsl.shader\"=\"")
            .Append(stage).AppendLine("\" }");
    }

    private void EmitLocationMetadata(StringBuilder module)
    {
        var locations = _stageInputs.Concat(_stageOutputs).Distinct().Order().ToArray();
        if (locations.Length == 0) {
            return;
        }

        module.AppendLine();
        var nextMetadataId = 0;
        foreach (var location in locations) {
            var decorationsId = nextMetadataId++;
            var locationId = nextMetadataId++;
            module.Append('!').Append(decorationsId).Append(" = !{!")
                .Append(locationId);
            if (IsFlatLocation(location)) {
                module.Append(", !").Append(nextMetadataId);
            }
            module.AppendLine("}");
            module.Append('!').Append(locationId).Append(" = !{i32 30, i32 ")
                .Append(location).AppendLine("}");
            if (IsFlatLocation(location)) {
                module.Append('!').Append(nextMetadataId++).AppendLine(" = !{i32 14}");
            }
        }
    }

    private int GetLocationMetadataId(uint location)
    {
        var locations = _stageInputs.Concat(_stageOutputs).Distinct().Order().ToArray();
        var metadataId = 0;
        foreach (var candidate in locations) {
            if (candidate == location) {
                return metadataId;
            }
            metadataId += IsFlatLocation(candidate) ? 3 : 2;
        }
        throw new InvalidOperationException($"Location {location} was not registered.");
    }

    private bool IsFlatLocation(uint location) =>
        _flatStageInputs.Contains(location) || _flatStageOutputs.Contains(location);

    private delegate void IntrinsicHandler(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack);

    // Every supported shader intrinsic, including structurally recognized
    // Sia.Math calls, is dispatched through this table.
    private static readonly FrozenDictionary<IntrinsicKind, IntrinsicHandler> _intrinsics =
        new Dictionary<IntrinsicKind, IntrinsicHandler> {
            [IntrinsicKind.GlobalInvocationId] = EmitInvocationBuiltin,
            [IntrinsicKind.LocalInvocationId] = EmitInvocationBuiltin,
            [IntrinsicKind.WorkGroupId] = EmitInvocationBuiltin,
            [IntrinsicKind.Barrier] = EmitBarrier,
            [IntrinsicKind.VertexIndex] = EmitVertexIndexBuiltin,
            [IntrinsicKind.InstanceIndex] = EmitInstanceIndexBuiltin,
            [IntrinsicKind.GetInput] = EmitRasterInput,
            [IntrinsicKind.GetFlatInput] = EmitRasterInput,
            [IntrinsicKind.GetFragmentPosition] = EmitFragmentPositionBuiltin,
            [IntrinsicKind.AsFloat] = EmitAsFloat,
            [IntrinsicKind.UnpackHalf] = EmitUnpackHalf,
            [IntrinsicKind.InverseSqrt] = EmitMathUnary,
            [IntrinsicKind.Select] = EmitSelect,
            [IntrinsicKind.Discard] = EmitDiscard,
            [IntrinsicKind.SetPosition] = EmitSetPosition,
            [IntrinsicKind.SetOutput] = EmitRasterOutput,
            [IntrinsicKind.SetFlatOutput] = EmitRasterOutput,
            [IntrinsicKind.BufferIndex] = EmitBufferIndex,
            [IntrinsicKind.AtomicAdd] = EmitAtomic,
            [IntrinsicKind.AtomicExchange] = EmitAtomic,
            [IntrinsicKind.Texture2DLoad] = EmitTexture2DLoad,
            [IntrinsicKind.Texture2DSampleLevel] = EmitTexture2DSampleLevel,
            [IntrinsicKind.Texture2DArrayLoad] = EmitTexture2DArrayLoad,
            [IntrinsicKind.Texture2DArraySampleLevel] = EmitTexture2DArraySampleLevel,
            [IntrinsicKind.Sqrt] = EmitMathUnary,
            [IntrinsicKind.Sin] = EmitMathUnary,
            [IntrinsicKind.Cos] = EmitMathUnary,
            [IntrinsicKind.Pow] = EmitMathPow,
            [IntrinsicKind.Abs] = EmitMathUnary,
            [IntrinsicKind.MathConstruct] = EmitMathConstruct,
            [IntrinsicKind.MathGetComponent] = EmitMathGetComponent,
            [IntrinsicKind.MathAdd] = EmitMathArithmetic,
            [IntrinsicKind.MathSubtract] = EmitMathArithmetic,
            [IntrinsicKind.MathMultiply] = EmitMathArithmetic,
            [IntrinsicKind.MathDivide] = EmitMathArithmetic,
            [IntrinsicKind.MathNegate] = EmitMathNegate,
            [IntrinsicKind.MathDot] = EmitMathDot,
            [IntrinsicKind.MathCross] = EmitMathCross,
            [IntrinsicKind.MathNormalize] = EmitMathNormalize,
            [IntrinsicKind.MathMin] = EmitMathMinMax,
            [IntrinsicKind.MathMax] = EmitMathMinMax,
            [IntrinsicKind.MathClamp] = EmitMathClamp,
            [IntrinsicKind.MathSaturate] = EmitMathSaturate,
            [IntrinsicKind.MathReflect] = EmitMathReflect,
            [IntrinsicKind.MathAny] = EmitMathBooleanReduction,
            [IntrinsicKind.MathAll] = EmitMathBooleanReduction,
            [IntrinsicKind.MathMul] = EmitMathMul,
            [IntrinsicKind.MathTranspose] = EmitMathTranspose,
        }.ToFrozenDictionary();

    private void EmitCall(
        ShaderCilView view,
        CilBasicBlock block,
        int instructionIndex,
        int offset,
        Stack<LlvmValue> stack)
    {
        var call = view.ResolveCall(block, instructionIndex);
        var arguments = new LlvmValue[call.ParameterCount];
        for (var index = arguments.Length - 1; index >= 0; index--) {
            arguments[index] = Pop(stack, offset);
        }
        var instance = call.IsInstance ? Pop(stack, offset) : default;

        // UInt3.get_X/Y/Z are ordinary record-struct getters with no
        // [SpirvIntrinsic] to recover — matched on identity, not IntrinsicKind.
        if (call.DeclaringType == "Sia.Spirv.UInt3" &&
            call.Name is "get_X" or "get_Y" or "get_Z") {
            EmitVectorComponent(instance, call.Name, offset, stack);
            return;
        }
        if (call.Intrinsic is { } kind && _intrinsics.TryGetValue(kind, out var handler)) {
            _currentCall = call;
            try {
                handler(this, kind, instance, arguments, offset, stack);
            } finally {
                _currentCall = null;
            }
            return;
        }
        if (!call.IsInstance && TryEmitInlineCall(view, call, arguments, offset, stack)) {
            return;
        }
        throw CreateUnsupported(
            offset,
            $"Call to '{call.DeclaringType}.{call.Name}' is not a supported GPU intrinsic.");
    }

    private bool TryEmitInlineCall(
        ShaderCilView callerView,
        ResolvedCall call,
        IReadOnlyList<LlvmValue> arguments,
        int callOffset,
        Stack<LlvmValue> callerStack)
    {
        var handle = MetadataTokens.EntityHandle(call.MetadataToken);
        if (handle.Kind != HandleKind.MethodDefinition) {
            return false;
        }
        if (!_inlineCallStack.Add(call.MetadataToken)) {
            throw CreateUnsupported(
                callOffset,
                $"Recursive helper call '{call.DeclaringType}.{call.Name}' is not supported.");
        }

        try {
            var methodHandle = (MethodDefinitionHandle)handle;
            var method = _reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) {
                return false;
            }
            var body = _peReader.GetMethodBody(method.RelativeVirtualAddress);
            var localTypes = DecodeLocalTypes(_reader, body.LocalSignature);
            if (localTypes.Count != 0) {
                throw CreateUnsupported(
                    callOffset,
                    $"Inline helper '{call.DeclaringType}.{call.Name}' cannot declare locals yet.");
            }

            var il = body.GetILBytes() ?? throw CreateUnsupported(
                callOffset,
                $"Inline helper '{call.DeclaringType}.{call.Name}' has no CIL body.");
            var instructions = CilInstructionDecoder.Decode(il);
            var graph = CilControlFlowGraph.Create(instructions, il.Length);
            if (graph.Blocks.Count != 1) {
                throw CreateUnsupported(
                    callOffset,
                    $"Inline helper '{call.DeclaringType}.{call.Name}' must be straight-line CIL.");
            }
            var helperView = new ShaderCilView(graph, callerView.Resolver);
            new CilStackAnalyzer(_reader, methodHandle).Validate(helperView);
            var helperStack = new Stack<LlvmValue>();
            var block = graph.Blocks[0];
            for (var index = 0; index < block.Instructions.Count; index++) {
                var instruction = block.Instructions[index];
                if (instruction.OpCode == OpCodes.Ret) {
                    if (call.ReturnsVoid) {
                        if (helperStack.Count != 0) {
                            throw CreateUnsupported(
                                callOffset,
                                "Void inline helper left values on the evaluation stack.");
                        }
                    } else {
                        callerStack.Push(Pop(helperStack, instruction.Offset));
                    }
                    return true;
                }
                var terminated = EmitInstruction(
                    helperView,
                    block,
                    index,
                    instruction.OpCode,
                    instruction.Operand,
                    instruction.Offset,
                    localTypes,
                    arguments,
                    new Dictionary<int, int>(),
                    helperStack);
                if (terminated) {
                    throw CreateUnsupported(
                        callOffset,
                        $"Inline helper '{call.DeclaringType}.{call.Name}' contains control flow.");
                }
            }
            throw CreateUnsupported(
                callOffset,
                $"Inline helper '{call.DeclaringType}.{call.Name}' does not return.");
        } finally {
            _inlineCallStack.Remove(call.MetadataToken);
        }
    }

    // newobj has no receiver to pop, so it can't share EmitCall's instance
    // handling. Only recognized Sia.Math value constructors reach here.
    private void EmitNewobj(
        ShaderCilView view,
        CilBasicBlock block,
        int instructionIndex,
        int offset,
        Stack<LlvmValue> stack)
    {
        var call = view.ResolveCall(block, instructionIndex);
        var arguments = new LlvmValue[call.ParameterCount];
        for (var index = arguments.Length - 1; index >= 0; index--) {
            arguments[index] = Pop(stack, offset);
        }
        if (call.Intrinsic is { } kind && _intrinsics.TryGetValue(kind, out var handler)) {
            _currentCall = call;
            try {
                handler(this, kind, default, arguments, offset, stack);
            } finally {
                _currentCall = null;
            }
            return;
        }
        throw CreateUnsupported(
            offset,
            $"Constructing '{call.DeclaringType}' is not supported inside a SPIR-V kernel.");
    }

    private void EmitVectorComponent(
        LlvmValue instance,
        string methodName,
        int offset,
        Stack<LlvmValue> stack)
    {
        var vector = instance;
        if (vector.IsReference) {
            var loaded = NextValue();
            EmitLine($"{loaded} = load <3 x i32>, ptr {vector.Expression}, align 4");
            vector = new LlvmValue(loaded, LlvmValueType.UInt3);
        }
        var component = methodName[^1] - 'X';
        var result = NextValue();
        EmitLine($"{result} = extractelement <3 x i32> {vector.Expression}, i32 {component}");
        stack.Push(new LlvmValue(result, LlvmValueType.UInt32));
    }

    private static void EmitInvocationBuiltin(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureShaderStage(SpirvShaderStage.Compute, kind.ToString(), offset);
        stack.Push(emitter.EmitBuiltinVector(kind, offset));
    }

    private static void EmitBarrier(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureShaderStage(SpirvShaderStage.Compute, kind.ToString(), offset);
        emitter.EmitLine(
            "call void @llvm.spv.group.memory.barrier.with.group.sync()");
        emitter._usesBarrier = true;
    }

    private static void EmitVertexIndexBuiltin(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureShaderStage(SpirvShaderStage.Vertex, kind.ToString(), offset);
        emitter._readsVertexIndex = true;
        stack.Push(emitter.EmitInputScalar("@__spirv_BuiltInVertexIndex"));
    }

    private static void EmitInstanceIndexBuiltin(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureShaderStage(SpirvShaderStage.Vertex, kind.ToString(), offset);
        emitter._readsInstanceIndex = true;
        stack.Push(emitter.EmitInputScalar("@__spirv_BuiltInInstanceIndex"));
    }

    private static void EmitRasterInput(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureRasterizationStage(kind.ToString(), offset);
        var location = GetConstantIndex(arguments[0], uint.MaxValue, "location", offset);
        var component = GetConstantIndex(arguments[1], 3, "component", offset);
        emitter._stageInputs.Add(location);
        if (kind == IntrinsicKind.GetFlatInput) {
            emitter._flatStageInputs.Add(location);
        }
        stack.Push(emitter.EmitInputComponent($"@sia.input.location.{location}", component));
    }

    private static void EmitFragmentPositionBuiltin(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureShaderStage(SpirvShaderStage.Fragment, kind.ToString(), offset);
        var component = GetConstantIndex(arguments[0], 3, "component", offset);
        emitter._readsFragmentPosition = true;
        stack.Push(emitter.EmitInputComponent("@__spirv_BuiltInFragCoord", component));
    }

    private static void EmitAsFloat(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var input = emitter.LoadValue(arguments[0]);
        var resultType = GetType(emitter.GetCurrentCall().Signature.ReturnType);
        var valid = resultType == LlvmValueType.Float32 &&
            input.Type is LlvmValueType.Int32 or LlvmValueType.UInt32 ||
            TryGetVectorLength(resultType, out var resultLength) &&
            TryGetScalarVector(input.Type, out var inputScalar, out var inputLength) &&
            inputScalar is LlvmValueType.Int32 or LlvmValueType.UInt32 &&
            inputLength == resultLength;
        if (!valid) {
            throw CreateUnsupported(offset, "math.asfloat requires matching i32/u32 and f32 shapes.");
        }
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = bitcast {GetLlvmType(input.Type)} {input.Expression} to {GetLlvmType(resultType)}");
        stack.Push(new LlvmValue(result, resultType));
    }

    private static void EmitUnpackHalf(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var input = emitter.LoadValue(arguments[0]);
        string result;
        LlvmValueType resultType;
        if (input.Type == LlvmValueType.UInt32) {
            result = emitter.EmitUnpackHalfScalar(input.Expression);
            resultType = LlvmValueType.Float32;
        } else if (TryGetScalarVector(input.Type, out var scalarType, out var length) &&
                scalarType == LlvmValueType.UInt32) {
            resultType = GetVectorType(length);
            var components = new string[length];
            for (var index = 0; index < length; index++) {
                components[index] = emitter.EmitUnpackHalfScalar(
                    emitter.ExtractVectorElement(input.Expression, input.Type, index));
            }
            result = emitter.EmitVector(components);
        } else {
            throw CreateUnsupported(offset, "math.f16tof32 requires a u32 scalar or vector.");
        }
        stack.Push(new LlvmValue(result, resultType));
    }

    private static void EmitInverseSqrt(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.spv.rsqrt.f32(float {arguments[0].Expression})");
        emitter._usesInverseSqrt = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitSelect(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var whenFalse = emitter.LoadValue(arguments[0]);
        var whenTrue = emitter.LoadValue(arguments[1]);
        if (whenFalse.Type != whenTrue.Type) {
            throw CreateUnsupported(offset, "math.select values must have matching types.");
        }
        var condition = emitter.LoadValue(arguments[2]);
        string conditionType;
        string conditionExpression;
        if (condition.Type is LlvmValueType.Boolean or LlvmValueType.Int32 or LlvmValueType.UInt32) {
            var scalarCondition = emitter.ToBoolean(condition, offset);
            conditionType = "i1";
            conditionExpression = scalarCondition.Expression;
        } else if (TryGetScalarVector(condition.Type, out var conditionScalar, out var conditionLength) &&
                conditionScalar == LlvmValueType.Boolean &&
                TryGetScalarVector(whenFalse.Type, out _, out var valueLength) &&
                conditionLength == valueLength) {
            conditionType = GetLlvmType(condition.Type);
            conditionExpression = condition.Expression;
        } else {
            throw CreateUnsupported(offset, "math.select condition must be bool or a matching bool vector.");
        }
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = select {conditionType} {conditionExpression}, " +
            $"{GetLlvmType(whenTrue.Type)} {whenTrue.Expression}, {GetLlvmType(whenFalse.Type)} {whenFalse.Expression}");
        stack.Push(new LlvmValue(result, whenFalse.Type));
    }

    private static void EmitDiscard(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureShaderStage(SpirvShaderStage.Fragment, kind.ToString(), offset);
        emitter.EmitLine("call void @llvm.spv.discard()");
        emitter._usesDiscard = true;
    }

    private static void EmitSetPosition(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureShaderStage(SpirvShaderStage.Vertex, kind.ToString(), offset);
        emitter._writesPosition = true;
        if (emitter._kernelAbi == SpirvKernelAbi.WebGpu) {
            EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
            var invertedY = emitter.NextValue();
            emitter.EmitLine($"{invertedY} = fneg float {arguments[1].Expression}");
            var webGpuPosition = arguments.ToArray();
            webGpuPosition[1] = new LlvmValue(invertedY, LlvmValueType.Float32);
            emitter.EmitOutputVector("@__spirv_BuiltInPosition", webGpuPosition, offset);
            return;
        }
        emitter.EmitOutputVector("@__spirv_BuiltInPosition", arguments, offset);
    }

    private static void EmitRasterOutput(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        emitter.EnsureRasterizationStage(kind.ToString(), offset);
        var location = GetConstantIndex(arguments[0], uint.MaxValue, "location", offset);
        emitter._stageOutputs.Add(location);
        if (kind == IntrinsicKind.SetFlatOutput) {
            emitter._flatStageOutputs.Add(location);
        }
        emitter.EmitOutputVector($"@sia.output.location.{location}", arguments.Skip(1).ToArray(), offset);
    }

    private static void EmitBufferIndex(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        if (!IsBuffer(instance.Type) && !IsWorkgroupMemory(instance.Type)) {
            throw CreateUnsupported(offset, "Indexed memory access requires a storage-buffer or workgroup-memory receiver.");
        }
        var index = arguments[0];
        var pointer = emitter.EmitIndexedPointer(instance, index, offset);
        stack.Push(pointer);
    }

    private static void EmitAtomic(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        if (!IsBuffer(instance.Type) && !IsWorkgroupMemory(instance.Type)) {
            throw CreateUnsupported(offset, "Atomic access requires a storage-buffer or workgroup-memory receiver.");
        }
        var pointer = emitter.EmitIndexedPointer(instance, arguments[0], offset);
        if (pointer.Type is not (LlvmValueType.Int32 or LlvmValueType.UInt32)) {
            throw CreateUnsupported(offset, "Atomic operations require int or uint elements.");
        }
        var addressType = GetPointerType(pointer.AddressSpace);
        var synchronizationScope = pointer.AddressSpace == 3 ? "workgroup" : "device";
        EnsureCompatible(pointer.Type, arguments[1].Type, offset);
        var result = emitter.NextValue();
        var operation = kind == IntrinsicKind.AtomicAdd ? "add" : "xchg";
        emitter.EmitLine($"{result} = atomicrmw {operation} {addressType} {pointer.Expression}, i32 {arguments[1].Expression} syncscope(\"{synchronizationScope}\") monotonic");
        stack.Push(new LlvmValue(result, pointer.Type));
    }

    private LlvmValue EmitIndexedPointer(LlvmValue instance, LlvmValue index, int offset)
    {
        EnsureInteger(index, "index", offset);
        var pointer = NextValue();
        if (IsBuffer(instance.Type)) {
            if (GetBufferElementType(instance.Type) == LlvmValueType.Struct) {
                var layout = _structLayout ?? throw new InvalidOperationException(
                    "Struct buffer used without a decoded layout.");
                var scaledIndex = NextValue();
                EmitLine($"{scaledIndex} = mul i32 {index.Expression}, {layout.ArrayStride / 4}");
                return new LlvmValue(
                    string.Empty,
                    LlvmValueType.Struct,
                    true,
                    11,
                    instance.Expression,
                    scaledIndex,
                    instance.Type);
            }
            var targetType = GetBufferTargetType(instance.Type);
            var mangling = GetBufferMangling(instance.Type);
            EmitLine($"{pointer} = call ptr addrspace(11) @llvm.spv.resource.getpointer.p11.{mangling}({targetType} {instance.Expression}, i32 {index.Expression})");
            return new LlvmValue(pointer, GetBufferElementType(instance.Type), true, 11);
        }

        var elementType = GetWorkgroupElementType(instance.Type);
        var length = checked((int)(_currentWorkgroupSize.X * _currentWorkgroupSize.Y * _currentWorkgroupSize.Z));
        EmitLine($"{pointer} = getelementptr inbounds [{length} x {GetLlvmType(elementType)}], ptr addrspace(3) {instance.Expression}, i32 0, i32 {index.Expression}");
        return new LlvmValue(pointer, elementType, true, 3);
    }

    private static void EmitTexture2DLoad(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        if (instance.Type != LlvmValueType.Texture2DFloat) {
            throw CreateUnsupported(offset, "Texture2D.Load requires a texture receiver.");
        }
        EnsureInteger(arguments[0], "x", offset);
        EnsureInteger(arguments[1], "y", offset);
        var component = GetConstantIndex(arguments[^1], 3, "component", offset);
        var level = arguments.Count == 4 ? arguments[2] : new LlvmValue("0", LlvmValueType.Int32);
        EnsureInteger(level, "level", offset);
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <2 x i32> poison, i32 {arguments[0].Expression}, i32 0");
        var coordinates = emitter.NextValue();
        emitter.EmitLine($"{coordinates} = insertelement <2 x i32> {first}, i32 {arguments[1].Expression}, i32 1");
        var texel = emitter.NextValue();
        emitter.EmitLine($"{texel} = call <4 x float> @llvm.spv.resource.load.level.{GetTexture2DLoadMangling()}({GetTexture2DTargetType()} {instance.Expression}, <2 x i32> {coordinates}, i32 {level.Expression}, <2 x i32> zeroinitializer)");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <4 x float> {texel}, i32 {component}");
        emitter._usesTexture2DLoad = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitTexture2DSampleLevel(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        if (instance.Type != LlvmValueType.Texture2DFloat ||
            arguments[0].Type != LlvmValueType.Sampler) {
            throw CreateUnsupported(offset, "Texture2D.SampleLevel requires texture and sampler receivers.");
        }
        EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[2].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[3].Type, offset);
        var component = GetConstantIndex(arguments[4], 3, "component", offset);
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <2 x float> poison, float {arguments[1].Expression}, i32 0");
        var coordinates = emitter.NextValue();
        emitter.EmitLine($"{coordinates} = insertelement <2 x float> {first}, float {arguments[2].Expression}, i32 1");
        var texel = emitter.NextValue();
        emitter.EmitLine($"{texel} = call <4 x float> @llvm.spv.resource.samplelevel.{GetTexture2DSampleLevelMangling()}({GetTexture2DTargetType()} {instance.Expression}, {GetSamplerTargetType()} {arguments[0].Expression}, <2 x float> {coordinates}, float {arguments[3].Expression}, <2 x i32> zeroinitializer)");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <4 x float> {texel}, i32 {component}");
        emitter._usesTexture2DSampleLevel = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitTexture2DArrayLoad(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        if (instance.Type != LlvmValueType.Texture2DArrayFloat) {
            throw CreateUnsupported(offset, "Texture2DArray.Load requires a texture-array receiver.");
        }
        EnsureInteger(arguments[0], "x", offset);
        EnsureInteger(arguments[1], "y", offset);
        EnsureInteger(arguments[2], "layer", offset);
        var component = GetConstantIndex(arguments[^1], 3, "component", offset);
        var level = arguments.Count == 5 ? arguments[3] : new LlvmValue("0", LlvmValueType.Int32);
        EnsureInteger(level, "level", offset);
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <3 x i32> poison, i32 {arguments[0].Expression}, i32 0");
        var second = emitter.NextValue();
        emitter.EmitLine($"{second} = insertelement <3 x i32> {first}, i32 {arguments[1].Expression}, i32 1");
        var coordinates = emitter.NextValue();
        emitter.EmitLine($"{coordinates} = insertelement <3 x i32> {second}, i32 {arguments[2].Expression}, i32 2");
        var texel = emitter.NextValue();
        emitter.EmitLine($"{texel} = call <4 x float> @llvm.spv.resource.load.level.{GetTexture2DArrayLoadMangling()}({GetTexture2DArrayTargetType()} {instance.Expression}, <3 x i32> {coordinates}, i32 {level.Expression}, <2 x i32> zeroinitializer)");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <4 x float> {texel}, i32 {component}");
        emitter._usesTexture2DArrayLoad = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitTexture2DArraySampleLevel(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        if (instance.Type != LlvmValueType.Texture2DArrayFloat) {
            throw CreateUnsupported(offset, "Texture2DArray.SampleLevel requires a texture-array receiver.");
        }
        EnsureCompatible(LlvmValueType.Sampler, arguments[0].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[2].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[3].Type, offset);
        var component = GetConstantIndex(arguments[^1], 3, "component", offset);
        var level = arguments.Count == 6
            ? arguments[4]
            : new LlvmValue("0.000000e+00", LlvmValueType.Float32);
        EnsureCompatible(LlvmValueType.Float32, level.Type, offset);
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <3 x float> poison, float {arguments[1].Expression}, i32 0");
        var second = emitter.NextValue();
        emitter.EmitLine($"{second} = insertelement <3 x float> {first}, float {arguments[2].Expression}, i32 1");
        var coordinates = emitter.NextValue();
        emitter.EmitLine($"{coordinates} = insertelement <3 x float> {second}, float {arguments[3].Expression}, i32 2");
        var texel = emitter.NextValue();
        emitter.EmitLine($"{texel} = call <4 x float> @llvm.spv.resource.samplelevel.{GetTexture2DArraySampleLevelMangling()}({GetTexture2DArrayTargetType()} {instance.Expression}, {GetSamplerTargetType()} {arguments[0].Expression}, <3 x float> {coordinates}, float {level.Expression}, <2 x i32> zeroinitializer)");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <4 x float> {texel}, i32 {component}");
        emitter._usesTexture2DArraySampleLevel = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitSqrt(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.sqrt.f32(float {arguments[0].Expression})");
        emitter._usesSqrt = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitSin(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.sin.f32(float {arguments[0].Expression})");
        emitter._usesSin = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitCos(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.cos.f32(float {arguments[0].Expression})");
        emitter._usesCos = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitPow(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.pow.f32(float {arguments[0].Expression}, float {arguments[1].Expression})");
        emitter._usesPow = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitAbs(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.fabs.f32(float {arguments[0].Expression})");
        emitter._usesAbs = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitMathConstruct(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var call = emitter.GetCurrentCall();
        var type = GetType(call.Name == ".ctor"
            ? new KernelType(call.DeclaringType)
            : call.Signature.ReturnType);
        var values = arguments.Select(argument => emitter.LoadValue(argument)).ToArray();
        string result;
        if (TryGetScalarVector(type, out _, out var length)) {
            if (values.Length == 1) {
                result = emitter.EmitVectorBroadcast(type, values[0].Expression);
            } else {
                result = emitter.EmitVector(
                    type,
                    values.Select(static value => value.Expression).ToArray());
            }
        } else if (TryGetMatrixShape(type, out var rows, out var columns)) {
            var columnValues = new string[columns];
            if (values.Length == 1) {
                var column = emitter.EmitVectorBroadcast(values[0].Expression, rows);
                Array.Fill(columnValues, column);
            } else if (values.Length == columns &&
                    values.All(value => value.Type == GetVectorType(rows))) {
                for (var column = 0; column < columns; column++) {
                    columnValues[column] = values[column].Expression;
                }
            } else if (values.Length == rows * columns) {
                for (var column = 0; column < columns; column++) {
                    var components = new string[rows];
                    for (var row = 0; row < rows; row++) {
                        components[row] = values[row * columns + column].Expression;
                    }
                    columnValues[column] = emitter.EmitVector(components);
                }
            } else {
                throw CreateUnsupported(offset, $"Unsupported constructor shape for {call.DeclaringType}.");
            }
            result = emitter.EmitMatrix(type, columnValues);
        } else {
            throw CreateUnsupported(offset, $"Unsupported Sia.Math constructor type {type}.");
        }

        if (instance.IsReference) {
            emitter.EmitLine($"store {GetLlvmType(type)} {result}, ptr {instance.Expression}, align {GetAlignment(type)}");
        } else {
            stack.Push(new LlvmValue(result, type));
        }
    }

    private static void EmitMathGetComponent(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var vector = emitter.LoadValue(instance);
        if (!TryGetScalarVector(vector.Type, out var scalarType, out var length)) {
            throw CreateUnsupported(offset, $"Component access requires a Sia.Math vector, found {vector.Type}.");
        }
        var component = emitter.GetCurrentCall().Name[^1] switch {
            'x' => 0,
            'y' => 1,
            'z' => 2,
            'w' => 3,
            _ => -1
        };
        if (component < 0 || component >= length) {
            throw CreateUnsupported(offset, "Vector component is outside the vector shape.");
        }
        stack.Push(new LlvmValue(
            emitter.ExtractVectorElement(vector.Expression, vector.Type, component),
            scalarType));
    }

    private static void EmitMathArithmetic(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var left = emitter.LoadValue(arguments[0]);
        var right = emitter.LoadValue(arguments[1]);
        var resultType = GetType(emitter.GetCurrentCall().Signature.ReturnType);
        var scalarType = TryGetScalarVector(resultType, out var vectorScalarType, out _)
            ? vectorScalarType
            : LlvmValueType.Float32;
        var operation = (kind, scalarType) switch {
            (IntrinsicKind.MathAdd, LlvmValueType.Float32) => "fadd",
            (IntrinsicKind.MathSubtract, LlvmValueType.Float32) => "fsub",
            (IntrinsicKind.MathMultiply, LlvmValueType.Float32) => "fmul",
            (IntrinsicKind.MathDivide, LlvmValueType.Float32) => "fdiv",
            (IntrinsicKind.MathAdd, _) => "add",
            (IntrinsicKind.MathSubtract, _) => "sub",
            (IntrinsicKind.MathMultiply, _) => "mul",
            (IntrinsicKind.MathDivide, LlvmValueType.UInt32) => "udiv",
            (IntrinsicKind.MathDivide, _) => "sdiv",
            _ => throw CreateUnsupported(offset, $"Arithmetic is not supported for {resultType}.")
        };
        var result = emitter.EmitElementwiseBinary(operation, left, right, resultType, offset);
        stack.Push(new LlvmValue(result, resultType));
    }

    private static void EmitMathNegate(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var value = emitter.LoadValue(arguments[0]);
        string result;
        if (TryGetScalarVector(value.Type, out var scalarType, out _)) {
            result = emitter.NextValue();
            if (scalarType == LlvmValueType.Float32) {
                emitter.EmitLine($"{result} = fneg {GetLlvmType(value.Type)} {value.Expression}");
            } else if (scalarType == LlvmValueType.Int32) {
                emitter.EmitLine($"{result} = sub {GetLlvmType(value.Type)} zeroinitializer, {value.Expression}");
            } else {
                throw CreateUnsupported(offset, $"Negation is not supported for {value.Type}.");
            }
        } else if (TryGetMatrixShape(value.Type, out var rows, out var columns)) {
            var outputColumns = new string[columns];
            for (var column = 0; column < columns; column++) {
                var source = emitter.ExtractMatrixColumn(value.Expression, value.Type, column);
                var negated = emitter.NextValue();
                emitter.EmitLine($"{negated} = fneg <{rows} x float> {source}");
                outputColumns[column] = negated;
            }
            result = emitter.EmitMatrix(value.Type, outputColumns);
        } else {
            throw CreateUnsupported(offset, $"Negation is not supported for {value.Type}.");
        }
        stack.Push(new LlvmValue(result, value.Type));
    }

    private static void EmitMathUnary(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var value = emitter.LoadValue(arguments[0]);
        if (value.Type == LlvmValueType.Float32) {
            stack.Push(new LlvmValue(
                emitter.EmitScalarUnary(kind, value.Expression),
                LlvmValueType.Float32));
            return;
        }
        if (!TryGetVectorLength(value.Type, out var length)) {
            throw CreateUnsupported(offset, $"math.{emitter.GetCurrentCall().Name} does not support {value.Type}.");
        }
        var components = new string[length];
        for (var index = 0; index < length; index++) {
            components[index] = emitter.EmitScalarUnary(
                kind,
                emitter.ExtractVectorElement(value.Expression, length, index));
        }
        stack.Push(new LlvmValue(emitter.EmitVector(components), value.Type));
    }

    private static void EmitMathPow(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var left = emitter.LoadValue(arguments[0]);
        var right = emitter.LoadValue(arguments[1]);
        if (left.Type == LlvmValueType.Float32) {
            stack.Push(new LlvmValue(
                emitter.EmitScalarPow(left.Expression, right.Expression),
                LlvmValueType.Float32));
            return;
        }
        if (!TryGetVectorLength(left.Type, out var length) || right.Type != left.Type) {
            throw CreateUnsupported(offset, "math.pow arguments must have matching float shapes.");
        }
        var components = new string[length];
        for (var index = 0; index < length; index++) {
            components[index] = emitter.EmitScalarPow(
                emitter.ExtractVectorElement(left.Expression, length, index),
                emitter.ExtractVectorElement(right.Expression, length, index));
        }
        stack.Push(new LlvmValue(emitter.EmitVector(components), left.Type));
    }

    private static void EmitMathMinMax(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var left = emitter.LoadValue(arguments[0]);
        var right = emitter.LoadValue(arguments[1]);
        if (left.Type != right.Type) {
            throw CreateUnsupported(offset, "math.min/max arguments must have matching float shapes.");
        }
        var minimum = kind == IntrinsicKind.MathMin;
        if (left.Type == LlvmValueType.Float32) {
            stack.Push(new LlvmValue(
                emitter.EmitScalarMinMax(minimum, left.Expression, right.Expression),
                left.Type));
            return;
        }
        if (!TryGetVectorLength(left.Type, out var length)) {
            throw CreateUnsupported(offset, $"math.min/max does not support {left.Type}.");
        }
        var components = new string[length];
        for (var index = 0; index < length; index++) {
            components[index] = emitter.EmitScalarMinMax(
                minimum,
                emitter.ExtractVectorElement(left.Expression, length, index),
                emitter.ExtractVectorElement(right.Expression, length, index));
        }
        stack.Push(new LlvmValue(emitter.EmitVector(components), left.Type));
    }

    private static void EmitMathClamp(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var value = emitter.LoadValue(arguments[0]);
        var minimum = emitter.LoadValue(arguments[1]);
        var maximum = emitter.LoadValue(arguments[2]);
        if (value.Type != minimum.Type || value.Type != maximum.Type) {
            throw CreateUnsupported(offset, "math.clamp arguments must have matching float shapes.");
        }
        if (value.Type == LlvmValueType.Float32) {
            var lower = emitter.EmitScalarMinMax(false, value.Expression, minimum.Expression);
            stack.Push(new LlvmValue(
                emitter.EmitScalarMinMax(true, lower, maximum.Expression),
                value.Type));
            return;
        }
        if (!TryGetVectorLength(value.Type, out var length)) {
            throw CreateUnsupported(offset, $"math.clamp does not support {value.Type}.");
        }
        var components = new string[length];
        for (var index = 0; index < length; index++) {
            var lower = emitter.EmitScalarMinMax(
                false,
                emitter.ExtractVectorElement(value.Expression, length, index),
                emitter.ExtractVectorElement(minimum.Expression, length, index));
            components[index] = emitter.EmitScalarMinMax(
                true,
                lower,
                emitter.ExtractVectorElement(maximum.Expression, length, index));
        }
        stack.Push(new LlvmValue(emitter.EmitVector(components), value.Type));
    }

    private static void EmitMathSaturate(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var value = emitter.LoadValue(arguments[0]);
        if (value.Type == LlvmValueType.Float32) {
            var lower = emitter.EmitScalarMinMax(false, value.Expression, "0.000000e+00");
            stack.Push(new LlvmValue(
                emitter.EmitScalarMinMax(true, lower, "1.000000e+00"),
                value.Type));
            return;
        }
        if (!TryGetVectorLength(value.Type, out var length)) {
            throw CreateUnsupported(offset, $"math.saturate does not support {value.Type}.");
        }
        var components = new string[length];
        for (var index = 0; index < length; index++) {
            var lower = emitter.EmitScalarMinMax(
                false,
                emitter.ExtractVectorElement(value.Expression, length, index),
                "0.000000e+00");
            components[index] = emitter.EmitScalarMinMax(true, lower, "1.000000e+00");
        }
        stack.Push(new LlvmValue(emitter.EmitVector(components), value.Type));
    }

    private static void EmitMathDot(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var left = emitter.LoadValue(arguments[0]);
        var right = emitter.LoadValue(arguments[1]);
        if (left.Type != right.Type || !TryGetVectorLength(left.Type, out var length)) {
            throw CreateUnsupported(offset, "math.dot arguments must be matching float vectors.");
        }
        stack.Push(new LlvmValue(
            emitter.EmitDot(left.Expression, right.Expression, length),
            LlvmValueType.Float32));
    }

    private static void EmitMathCross(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var left = emitter.LoadValue(arguments[0]);
        var right = emitter.LoadValue(arguments[1]);
        var ax = emitter.ExtractVectorElement(left.Expression, 3, 0);
        var ay = emitter.ExtractVectorElement(left.Expression, 3, 1);
        var az = emitter.ExtractVectorElement(left.Expression, 3, 2);
        var bx = emitter.ExtractVectorElement(right.Expression, 3, 0);
        var by = emitter.ExtractVectorElement(right.Expression, 3, 1);
        var bz = emitter.ExtractVectorElement(right.Expression, 3, 2);
        stack.Push(new LlvmValue(emitter.EmitVector([
            emitter.EmitScalarBinary("fsub", emitter.EmitScalarBinary("fmul", ay, bz), emitter.EmitScalarBinary("fmul", az, by)),
            emitter.EmitScalarBinary("fsub", emitter.EmitScalarBinary("fmul", az, bx), emitter.EmitScalarBinary("fmul", ax, bz)),
            emitter.EmitScalarBinary("fsub", emitter.EmitScalarBinary("fmul", ax, by), emitter.EmitScalarBinary("fmul", ay, bx))
        ]), LlvmValueType.Float32x3));
    }

    private static void EmitMathNormalize(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var vector = emitter.LoadValue(arguments[0]);
        if (!TryGetVectorLength(vector.Type, out var length)) {
            throw CreateUnsupported(offset, "math.normalize requires a float vector.");
        }
        var lengthSquared = emitter.EmitDot(vector.Expression, vector.Expression, length);
        var inverseLength = emitter.EmitScalarUnary(IntrinsicKind.InverseSqrt, lengthSquared);
        var broadcast = emitter.EmitVectorBroadcast(inverseLength, length);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fmul <{length} x float> {vector.Expression}, {broadcast}");
        stack.Push(new LlvmValue(result, vector.Type));
    }

    private static void EmitMathReflect(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var incident = emitter.LoadValue(arguments[0]);
        var normal = emitter.LoadValue(arguments[1]);
        if (incident.Type != normal.Type || !TryGetVectorLength(incident.Type, out var length)) {
            throw CreateUnsupported(offset, "math.reflect arguments must be matching float vectors.");
        }
        var dot = emitter.EmitDot(incident.Expression, normal.Expression, length);
        var doubled = emitter.EmitScalarBinary("fmul", dot, "2.000000e+00");
        var scaled = emitter.NextValue();
        emitter.EmitLine($"{scaled} = fmul <{length} x float> {normal.Expression}, {emitter.EmitVectorBroadcast(doubled, length)}");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fsub <{length} x float> {incident.Expression}, {scaled}");
        stack.Push(new LlvmValue(result, incident.Type));
    }

    private static void EmitMathBooleanReduction(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var vector = emitter.LoadValue(arguments[0]);
        if (!TryGetScalarVector(vector.Type, out var scalarType, out var length) ||
            scalarType != LlvmValueType.Boolean) {
            throw CreateUnsupported(offset, "math.any/all requires a boolean vector.");
        }
        var operation = kind == IntrinsicKind.MathAny ? "or" : "and";
        var result = emitter.ExtractVectorElement(vector.Expression, vector.Type, 0);
        for (var index = 1; index < length; index++) {
            var next = emitter.NextValue();
            emitter.EmitLine($"{next} = {operation} i1 {result}, {emitter.ExtractVectorElement(vector.Expression, vector.Type, index)}");
            result = next;
        }
        stack.Push(new LlvmValue(result, LlvmValueType.Boolean));
    }

    private static void EmitMathMul(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var left = emitter.LoadValue(arguments[0]);
        var right = emitter.LoadValue(arguments[1]);
        var resultType = GetType(emitter.GetCurrentCall().Signature.ReturnType);
        string result;
        if (TryGetMatrixShape(left.Type, out var leftRows, out var leftColumns) &&
            TryGetVectorLength(right.Type, out var rightLength)) {
            if (leftColumns != rightLength) {
                throw CreateUnsupported(offset, "Matrix and vector shapes are incompatible for math.mul.");
            }
            result = emitter.EmitMatrixVectorMultiply(left, right.Expression, leftRows, leftColumns);
        } else if (TryGetVectorLength(left.Type, out var leftLength) &&
                TryGetMatrixShape(right.Type, out var rightRows, out var rightColumns)) {
            if (leftLength != rightRows) {
                throw CreateUnsupported(offset, "Vector and matrix shapes are incompatible for math.mul.");
            }
            var components = new string[rightColumns];
            for (var column = 0; column < rightColumns; column++) {
                components[column] = emitter.EmitDot(
                    left.Expression,
                    emitter.ExtractMatrixColumn(right.Expression, right.Type, column),
                    rightRows);
            }
            result = emitter.EmitVector(components);
        } else if (TryGetMatrixShape(left.Type, out leftRows, out leftColumns) &&
                TryGetMatrixShape(right.Type, out rightRows, out rightColumns)) {
            if (leftColumns != rightRows) {
                throw CreateUnsupported(offset, "Matrix shapes are incompatible for math.mul.");
            }
            var columns = new string[rightColumns];
            for (var column = 0; column < rightColumns; column++) {
                columns[column] = emitter.EmitMatrixVectorMultiply(
                    left,
                    emitter.ExtractMatrixColumn(right.Expression, right.Type, column),
                    leftRows,
                    leftColumns);
            }
            result = emitter.EmitMatrix(resultType, columns);
        } else {
            throw CreateUnsupported(offset, "math.mul requires a matrix/vector combination.");
        }
        stack.Push(new LlvmValue(result, resultType));
    }

    private static void EmitMathTranspose(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var matrix = emitter.LoadValue(arguments[0]);
        if (!TryGetMatrixShape(matrix.Type, out var rows, out var columns)) {
            throw CreateUnsupported(offset, "math.transpose requires a float matrix.");
        }
        var resultType = GetMatrixType(columns, rows);
        var outputColumns = new string[rows];
        for (var outputColumn = 0; outputColumn < rows; outputColumn++) {
            var components = new string[columns];
            for (var outputRow = 0; outputRow < columns; outputRow++) {
                var sourceColumn = emitter.ExtractMatrixColumn(
                    matrix.Expression,
                    matrix.Type,
                    outputRow);
                components[outputRow] = emitter.ExtractVectorElement(
                    sourceColumn,
                    rows,
                    outputColumn);
            }
            outputColumns[outputColumn] = emitter.EmitVector(components);
        }
        stack.Push(new LlvmValue(emitter.EmitMatrix(resultType, outputColumns), resultType));
    }

    private ResolvedCall GetCurrentCall() => _currentCall ??
        throw new InvalidOperationException("A Sia.Math intrinsic was emitted without call metadata.");

    private LlvmValue LoadValue(LlvmValue value)
    {
        if (!value.IsReference) {
            return value;
        }
        var loaded = NextValue();
        EmitLine($"{loaded} = load {GetLlvmType(value.Type)}, {GetPointerType(value.AddressSpace)} {value.Expression}, align {GetAlignment(value.Type)}");
        return new LlvmValue(loaded, value.Type);
    }

    private string EmitVectorBroadcast(string scalar, int length)
    {
        var components = Enumerable.Repeat(scalar, length).ToArray();
        return EmitVector(components);
    }

    private string EmitVectorBroadcast(LlvmValueType type, string scalar)
    {
        if (!TryGetScalarVector(type, out _, out var length)) {
            throw new InvalidOperationException($"{type} is not a scalar vector.");
        }
        return EmitVector(type, Enumerable.Repeat(scalar, length).ToArray());
    }

    private string EmitVector(IReadOnlyList<string> components)
    {
        return EmitVector(GetVectorType(components.Count), components);
    }

    private string EmitVector(LlvmValueType type, IReadOnlyList<string> components)
    {
        if (!TryGetScalarVector(type, out var scalarType, out var length) ||
            components.Count != length) {
            throw new InvalidOperationException($"Invalid vector construction for {type}.");
        }
        var llvmType = GetLlvmType(type);
        var scalarLlvmType = GetLlvmType(scalarType);
        var value = "poison";
        for (var index = 0; index < components.Count; index++) {
            var next = NextValue();
            EmitLine($"{next} = insertelement {llvmType} {value}, {scalarLlvmType} {components[index]}, i32 {index}");
            value = next;
        }
        return value;
    }

    private string ExtractVectorElement(string vector, int length, int index)
    {
        var result = NextValue();
        EmitLine($"{result} = extractelement <{length} x float> {vector}, i32 {index}");
        return result;
    }

    private string ExtractVectorElement(string vector, LlvmValueType type, int index)
    {
        if (!TryGetScalarVector(type, out var scalarType, out var length) || index >= length) {
            throw new InvalidOperationException($"Invalid vector component {index} for {type}.");
        }
        var result = NextValue();
        EmitLine($"{result} = extractelement {GetLlvmType(type)} {vector}, i32 {index}");
        return result;
    }

    private string EmitMatrix(LlvmValueType type, IReadOnlyList<string> columns)
    {
        if (!TryGetMatrixShape(type, out var rows, out var columnCount) ||
            columns.Count != columnCount) {
            throw new InvalidOperationException($"Invalid matrix construction for {type}.");
        }
        var matrixType = GetLlvmType(type);
        var value = "poison";
        for (var column = 0; column < columns.Count; column++) {
            var next = NextValue();
            EmitLine($"{next} = insertvalue {matrixType} {value}, <{rows} x float> {columns[column]}, {column}");
            value = next;
        }
        return value;
    }

    private string ExtractMatrixColumn(string matrix, LlvmValueType type, int column)
    {
        if (!TryGetMatrixShape(type, out var rows, out _)) {
            throw new InvalidOperationException($"{type} is not a matrix.");
        }
        var result = NextValue();
        EmitLine($"{result} = extractvalue {GetLlvmType(type)} {matrix}, {column}");
        return result;
    }

    private string EmitElementwiseBinary(
        string operation,
        LlvmValue left,
        LlvmValue right,
        LlvmValueType resultType,
        int offset)
    {
        if (TryGetScalarVector(resultType, out var scalarType, out _)) {
            var leftValue = left.Type == scalarType
                ? EmitVectorBroadcast(resultType, left.Expression)
                : left.Expression;
            var rightValue = right.Type == scalarType
                ? EmitVectorBroadcast(resultType, right.Expression)
                : right.Expression;
            if (left.Type != scalarType && left.Type != resultType ||
                right.Type != scalarType && right.Type != resultType) {
                throw CreateUnsupported(offset, "Vector arithmetic operands have incompatible shapes.");
            }
            var result = NextValue();
            EmitLine($"{result} = {operation} {GetLlvmType(resultType)} {leftValue}, {rightValue}");
            return result;
        }
        if (TryGetMatrixShape(resultType, out var rows, out var columns)) {
            var resultColumns = new string[columns];
            for (var column = 0; column < columns; column++) {
                var leftValue = left.Type == LlvmValueType.Float32
                    ? EmitVectorBroadcast(left.Expression, rows)
                    : ExtractMatrixColumn(left.Expression, left.Type, column);
                var rightValue = right.Type == LlvmValueType.Float32
                    ? EmitVectorBroadcast(right.Expression, rows)
                    : ExtractMatrixColumn(right.Expression, right.Type, column);
                var result = NextValue();
                EmitLine($"{result} = {operation} <{rows} x float> {leftValue}, {rightValue}");
                resultColumns[column] = result;
            }
            return EmitMatrix(resultType, resultColumns);
        }
        throw CreateUnsupported(offset, $"Sia.Math arithmetic does not support {resultType}.");
    }

    private string EmitScalarUnary(IntrinsicKind kind, string operand)
    {
        var intrinsic = kind switch {
            IntrinsicKind.Sqrt => "llvm.sqrt.f32",
            IntrinsicKind.Sin => "llvm.sin.f32",
            IntrinsicKind.Cos => "llvm.cos.f32",
            IntrinsicKind.Abs => "llvm.fabs.f32",
            IntrinsicKind.InverseSqrt => "llvm.spv.rsqrt.f32",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        _usesSqrt |= kind == IntrinsicKind.Sqrt;
        _usesSin |= kind == IntrinsicKind.Sin;
        _usesCos |= kind == IntrinsicKind.Cos;
        _usesAbs |= kind == IntrinsicKind.Abs;
        _usesInverseSqrt |= kind == IntrinsicKind.InverseSqrt;
        var result = NextValue();
        EmitLine($"{result} = call float @{intrinsic}(float {operand})");
        return result;
    }

    private string EmitUnpackHalfScalar(string value)
    {
        var half = EmitIntegerBinary("and", value, "65535");
        var sign = EmitIntegerBinary("and", half, "32768");
        var exponent = EmitIntegerBinary("and", half, "31744");
        var mantissa = EmitIntegerBinary("and", half, "1023");

        var mantissaFloat = NextValue();
        EmitLine($"{mantissaFloat} = uitofp i32 {mantissa} to float");
        var subnormal = EmitScalarBinary("fmul", mantissaFloat, "0x3E70000000000000");
        var negativeSubnormal = NextValue();
        EmitLine($"{negativeSubnormal} = fneg float {subnormal}");
        var isNegative = EmitIntegerComparison("ne", sign, "0");
        var signedSubnormal = EmitScalarSelect(isNegative, subnormal, negativeSubnormal);

        var signBits = EmitIntegerBinary("shl", sign, "16");
        var exponentValue = EmitIntegerBinary("lshr", exponent, "10");
        var biasedExponent = EmitIntegerBinary("add", exponentValue, "112");
        var exponentBits = EmitIntegerBinary("shl", biasedExponent, "23");
        var mantissaBits = EmitIntegerBinary("shl", mantissa, "13");
        var normalBits = EmitIntegerBinary(
            "or",
            EmitIntegerBinary("or", signBits, exponentBits),
            mantissaBits);
        var specialBits = EmitIntegerBinary(
            "or",
            EmitIntegerBinary("or", signBits, "2139095040"),
            mantissaBits);
        var isSpecial = EmitIntegerComparison("eq", exponent, "31744");
        var finiteOrSpecial = EmitIntegerSelect(isSpecial, normalBits, specialBits);
        var normalOrSpecial = NextValue();
        EmitLine($"{normalOrSpecial} = bitcast i32 {finiteOrSpecial} to float");

        var isSubnormal = EmitIntegerComparison("eq", exponent, "0");
        var result = EmitScalarSelect(isSubnormal, normalOrSpecial, signedSubnormal);
        return result;
    }

    private string EmitIntegerBinary(string operation, string left, string right)
    {
        var result = NextValue();
        EmitLine($"{result} = {operation} i32 {left}, {right}");
        return result;
    }

    private string EmitIntegerComparison(string predicate, string left, string right)
    {
        var result = NextValue();
        EmitLine($"{result} = icmp {predicate} i32 {left}, {right}");
        return result;
    }

    private string EmitIntegerSelect(string condition, string whenFalse, string whenTrue)
    {
        var result = NextValue();
        EmitLine($"{result} = select i1 {condition}, i32 {whenTrue}, i32 {whenFalse}");
        return result;
    }

    private string EmitScalarSelect(string condition, string whenFalse, string whenTrue)
    {
        var result = NextValue();
        EmitLine($"{result} = select i1 {condition}, float {whenTrue}, float {whenFalse}");
        return result;
    }

    private string EmitScalarPow(string left, string right)
    {
        var result = NextValue();
        EmitLine($"{result} = call float @llvm.pow.f32(float {left}, float {right})");
        _usesPow = true;
        return result;
    }

    private string EmitScalarMinMax(bool minimum, string left, string right)
    {
        var intrinsic = minimum ? "minnum" : "maxnum";
        var result = NextValue();
        EmitLine($"{result} = call float @llvm.{intrinsic}.f32(float {left}, float {right})");
        _usesMin |= minimum;
        _usesMax |= !minimum;
        return result;
    }

    private string EmitScalarBinary(string operation, string left, string right)
    {
        var result = NextValue();
        EmitLine($"{result} = {operation} float {left}, {right}");
        return result;
    }

    private string EmitDot(string left, string right, int length)
    {
        var product = NextValue();
        EmitLine($"{product} = fmul <{length} x float> {left}, {right}");
        var sum = ExtractVectorElement(product, length, 0);
        for (var index = 1; index < length; index++) {
            sum = EmitScalarBinary("fadd", sum, ExtractVectorElement(product, length, index));
        }
        return sum;
    }

    private string EmitMatrixVectorMultiply(
        LlvmValue matrix,
        string vector,
        int rows,
        int columns)
    {
        var output = new string[rows];
        for (var row = 0; row < rows; row++) {
            string? sum = null;
            for (var column = 0; column < columns; column++) {
                var matrixColumn = ExtractMatrixColumn(matrix.Expression, matrix.Type, column);
                var cell = ExtractVectorElement(matrixColumn, rows, row);
                var vectorElement = ExtractVectorElement(vector, columns, column);
                var product = EmitScalarBinary("fmul", cell, vectorElement);
                sum = sum is null ? product : EmitScalarBinary("fadd", sum, product);
            }
            output[row] = sum!;
        }
        return EmitVector(output);
    }

    private LlvmValue EmitInputScalar(string global)
    {
        var result = NextValue();
        EmitLine($"{result} = load i32, ptr addrspace(7) {global}, align 4");
        return new LlvmValue(result, LlvmValueType.UInt32);
    }

    private LlvmValue EmitInputComponent(string global, uint component)
    {
        var vector = NextValue();
        EmitLine($"{vector} = load <4 x float>, ptr addrspace(7) {global}, align 16");
        var result = NextValue();
        EmitLine($"{result} = extractelement <4 x float> {vector}, i32 {component}");
        return new LlvmValue(result, LlvmValueType.Float32);
    }

    private void EmitOutputVector(
        string global,
        IReadOnlyList<LlvmValue> components,
        int offset)
    {
        if (components.Count != 4) {
            throw CreateUnsupported(offset, "A raster output must contain four float components.");
        }
        foreach (var component in components) {
            EnsureCompatible(LlvmValueType.Float32, component.Type, offset);
        }

        var first = NextValue();
        EmitLine($"{first} = insertelement <4 x float> poison, float {components[0].Expression}, i32 0");
        var second = NextValue();
        EmitLine($"{second} = insertelement <4 x float> {first}, float {components[1].Expression}, i32 1");
        var third = NextValue();
        EmitLine($"{third} = insertelement <4 x float> {second}, float {components[2].Expression}, i32 2");
        var fourth = NextValue();
        EmitLine($"{fourth} = insertelement <4 x float> {third}, float {components[3].Expression}, i32 3");
        EmitLine($"store <4 x float> {fourth}, ptr addrspace(8) {global}, align 16");
    }

    private static uint GetConstantIndex(
        LlvmValue value,
        uint maximum,
        string name,
        int offset)
    {
        if (value.Type is not (LlvmValueType.Int32 or LlvmValueType.UInt32) ||
            !uint.TryParse(
                value.Expression,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var result) ||
            result > maximum) {
            throw CreateUnsupported(
                offset,
                $"Raster {name} must be a compile-time uint constant from 0 through {maximum}; " +
                $"found {value.Type} '{value.Expression}'.");
        }
        return result;
    }

    private void EnsureShaderStage(
        SpirvShaderStage expected,
        string intrinsic,
        int offset)
    {
        if (_shaderStage != expected) {
            throw CreateUnsupported(
                offset,
                $"Gpu.{intrinsic} requires a {expected.ToString().ToLowerInvariant()} shader.");
        }
    }

    private void EnsureRasterizationStage(string intrinsic, int offset)
    {
        if (_shaderStage is not (SpirvShaderStage.Vertex or SpirvShaderStage.Fragment)) {
            throw CreateUnsupported(
                offset,
                $"Gpu.{intrinsic} requires a vertex or fragment shader.");
        }
    }

    private LlvmValue EmitBuiltinVector(IntrinsicKind kind, int offset)
    {
        var intrinsic = kind switch {
            IntrinsicKind.GlobalInvocationId => "llvm.spv.thread.id.i32",
            IntrinsicKind.LocalInvocationId => "llvm.spv.thread.id.in.group.i32",
            IntrinsicKind.WorkGroupId => "llvm.spv.group.id.i32",
            _ => throw CreateUnsupported(offset, $"GPU builtin '{kind}' is not supported.")
        };
        var components = new string[3];
        for (var index = 0; index < components.Length; index++) {
            components[index] = NextValue();
            EmitLine($"{components[index]} = call i32 @{intrinsic}(i32 {index})");
        }
        var first = NextValue();
        EmitLine($"{first} = insertelement <3 x i32> poison, i32 {components[0]}, i32 0");
        var second = NextValue();
        EmitLine($"{second} = insertelement <3 x i32> {first}, i32 {components[1]}, i32 1");
        var third = NextValue();
        EmitLine($"{third} = insertelement <3 x i32> {second}, i32 {components[2]}, i32 2");
        return new LlvmValue(third, LlvmValueType.UInt3);
    }

    private void EmitLoadIndirect(OpCode opCode, int offset, Stack<LlvmValue> stack)
    {
        var pointer = Pop(stack, offset);
        if (!pointer.IsReference) {
            throw CreateUnsupported(offset, "Indirect load requires a managed reference produced by StorageBuffer<T>.");
        }
        var type = opCode == OpCodes.Ldind_R4 ? LlvmValueType.Float32 : pointer.Type;
        var value = NextValue();
        EmitLine($"{value} = load {GetLlvmType(type)}, {GetPointerType(pointer.AddressSpace)} {pointer.Expression}, align {GetAlignment(type)}");
        stack.Push(new LlvmValue(value, type));
    }

    private void EmitLoadObject(int token, int offset, Stack<LlvmValue> stack)
    {
        var decodedType = DecodeType(token);
        var type = decodedType.Name == _structLayout?.Name
            ? LlvmValueType.Struct
            : GetType(decodedType);
        var pointer = Pop(stack, offset);
        if (!pointer.IsReference) {
            throw CreateUnsupported(offset, "Object load requires a managed reference produced by StorageBuffer<T>.");
        }
        EnsureCompatible(pointer.Type, type, offset);
        if (type == LlvmValueType.Struct) {
            stack.Push(EmitLoadStruct(pointer));
            return;
        }
        var value = NextValue();
        EmitLine($"{value} = load {GetLlvmType(type)}, {GetPointerType(pointer.AddressSpace)} {pointer.Expression}, align {GetAlignment(type)}");
        stack.Push(new LlvmValue(value, type));
    }

    private KernelType DecodeType(int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch {
            HandleKind.TypeDefinition => new KernelType(
                MetadataNames.GetTypeName(_reader, (TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => new KernelType(
                MetadataNames.GetTypeName(_reader, (TypeReferenceHandle)handle)),
            HandleKind.TypeSpecification => _reader.GetTypeSpecification(
                    (TypeSpecificationHandle)handle)
                .DecodeSignature(new KernelTypeProvider(), genericContext: null),
            _ => throw new InvalidDataException($"Token 0x{token:x8} is not a type.")
        };
    }

    private void EmitStoreIndirect(int offset, Stack<LlvmValue> stack)
    {
        var value = Pop(stack, offset);
        var pointer = Pop(stack, offset);
        if (!pointer.IsReference) {
            throw CreateUnsupported(offset, "Indirect store requires a managed reference produced by StorageBuffer<T>.");
        }
        EnsureCompatible(pointer.Type, value.Type, offset);
        if (pointer.Type == LlvmValueType.Struct) {
            EmitStoreStruct(pointer, value);
            return;
        }
        EmitLine($"store {GetLlvmType(pointer.Type)} {value.Expression}, {GetPointerType(pointer.AddressSpace)} {pointer.Expression}, align {GetAlignment(pointer.Type)}");
    }

    private LlvmValue EmitLoadStruct(LlvmValue pointer)
    {
        var layout = _structLayout ?? throw new InvalidOperationException(
            "Struct load used without a decoded layout.");
        var aggregate = "poison";
        for (var fieldIndex = 0; fieldIndex < layout.Fields.Count; fieldIndex++) {
            var field = layout.Fields[fieldIndex];
            var fieldType = GetScalarType(field.Type);
            var componentCount = TryGetScalarVector(fieldType, out var scalarType, out var length)
                ? length
                : 1;
            if (componentCount == 1) {
                scalarType = fieldType;
            }
            var components = new string[componentCount];
            for (var component = 0; component < componentCount; component++) {
                var wordPointer = EmitStructWordPointer(
                    pointer,
                    field.Offset / 4 + component);
                var word = NextValue();
                EmitLine($"{word} = load i32, ptr addrspace(11) {wordPointer}, align 4");
                if (scalarType == LlvmValueType.Float32) {
                    var converted = NextValue();
                    EmitLine($"{converted} = bitcast i32 {word} to float");
                    components[component] = converted;
                } else {
                    components[component] = word;
                }
            }
            var fieldValue = componentCount == 1
                ? components[0]
                : EmitVector(fieldType, components);
            var next = NextValue();
            EmitLine($"{next} = insertvalue %sia.struct {aggregate}, {GetLlvmType(fieldType)} {fieldValue}, {fieldIndex}");
            aggregate = next;
        }
        return new LlvmValue(aggregate, LlvmValueType.Struct);
    }

    private void EmitStoreStruct(LlvmValue pointer, LlvmValue value)
    {
        var layout = _structLayout ?? throw new InvalidOperationException(
            "Struct store used without a decoded layout.");
        for (var fieldIndex = 0; fieldIndex < layout.Fields.Count; fieldIndex++) {
            var field = layout.Fields[fieldIndex];
            var fieldType = GetScalarType(field.Type);
            var fieldValue = NextValue();
            EmitLine($"{fieldValue} = extractvalue %sia.struct {value.Expression}, {fieldIndex}");
            var componentCount = TryGetScalarVector(fieldType, out var scalarType, out var length)
                ? length
                : 1;
            if (componentCount == 1) {
                scalarType = fieldType;
            }
            for (var component = 0; component < componentCount; component++) {
                var componentValue = componentCount == 1
                    ? fieldValue
                    : ExtractVectorElement(fieldValue, fieldType, component);
                if (scalarType == LlvmValueType.Float32) {
                    var converted = NextValue();
                    EmitLine($"{converted} = bitcast float {componentValue} to i32");
                    componentValue = converted;
                }
                var wordPointer = EmitStructWordPointer(
                    pointer,
                    field.Offset / 4 + component);
                EmitLine($"store i32 {componentValue}, ptr addrspace(11) {wordPointer}, align 4");
            }
        }
    }

    private string EmitStructWordPointer(LlvmValue reference, int wordOffset)
    {
        if (reference.ResourceExpression == null ||
            reference.ElementIndexExpression == null ||
            !IsBuffer(reference.ResourceType)) {
            throw new InvalidOperationException("Struct reference has no storage-buffer origin.");
        }
        var index = reference.ElementIndexExpression;
        if (wordOffset != 0) {
            var offsetIndex = NextValue();
            EmitLine($"{offsetIndex} = add i32 {index}, {wordOffset}");
            index = offsetIndex;
        }
        var pointer = NextValue();
        EmitLine($"{pointer} = call ptr addrspace(11) @llvm.spv.resource.getpointer.p11.{GetBufferMangling(reference.ResourceType)}({GetBufferTargetType(reference.ResourceType)} {reference.ResourceExpression}, i32 {index})");
        return pointer;
    }

    private void EmitBinary(OpCode opCode, int offset, Stack<LlvmValue> stack)
    {
        var right = Pop(stack, offset);
        var left = Pop(stack, offset);
        if (IsShift(opCode)) {
            EmitShift(opCode, left, right, offset, stack);
            return;
        }
        var type = MergeNumericTypes(left.Type, right.Type, offset);
        var instruction = GetBinaryInstruction(opCode, type, offset);
        var result = NextValue();
        EmitLine($"{result} = {instruction} {GetLlvmType(type)} {left.Expression}, {right.Expression}");
        stack.Push(new LlvmValue(result, type));
    }

    private void EmitShift(
        OpCode opCode,
        LlvmValue value,
        LlvmValue count,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureIntegerOperand(value, "Shift value", offset);
        EnsureIntegerOperand(count, "Shift count", offset);

        string maskedCount;
        if (int.TryParse(
            count.Expression,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var constantCount)) {
            maskedCount = (constantCount & 31).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        } else {
            maskedCount = NextValue("shift.count");
            EmitLine($"{maskedCount} = and i32 {count.Expression}, 31");
        }

        var result = NextValue();
        EmitLine(
            $"{result} = {GetBinaryInstruction(opCode, value.Type, offset)} i32 " +
            $"{value.Expression}, {maskedCount}");
        stack.Push(new LlvmValue(result, value.Type));
    }

    private void EmitNot(int offset, Stack<LlvmValue> stack)
    {
        var value = Pop(stack, offset);
        var constant = value.Type switch {
            LlvmValueType.Boolean => "true",
            LlvmValueType.Int32 or LlvmValueType.UInt32 => "-1",
            _ => throw CreateUnsupported(offset, $"Cannot complement a value of type {value.Type}.")
        };
        var result = NextValue();
        EmitLine($"{result} = xor {GetLlvmType(value.Type)} {value.Expression}, {constant}");
        stack.Push(new LlvmValue(result, value.Type));
    }

    private void EmitNegate(int offset, Stack<LlvmValue> stack)
    {
        var value = Pop(stack, offset);
        if (value.Type is not (LlvmValueType.Float32 or LlvmValueType.Int32 or LlvmValueType.UInt32)) {
            throw CreateUnsupported(offset, $"Cannot negate a value of type {value.Type}.");
        }
        var result = NextValue();
        if (value.Type == LlvmValueType.Float32) {
            EmitLine($"{result} = fneg float {value.Expression}");
        } else {
            EmitLine($"{result} = sub {GetLlvmType(value.Type)} 0, {value.Expression}");
        }
        stack.Push(new LlvmValue(result, value.Type));
    }

    private void EmitComparison(OpCode opCode, int offset, Stack<LlvmValue> stack)
    {
        var right = Pop(stack, offset);
        var left = Pop(stack, offset);
        var type = MergeNumericTypes(left.Type, right.Type, offset);
        var predicate = GetComparisonPredicate(opCode, type);
        var instruction = type == LlvmValueType.Float32 ? "fcmp" : "icmp";
        var result = NextValue();
        EmitLine($"{result} = {instruction} {predicate} {GetLlvmType(type)} {left.Expression}, {right.Expression}");
        stack.Push(new LlvmValue(result, LlvmValueType.Boolean));
    }

    private LlvmValue EmitBranchComparison(OpCode opCode, int offset, Stack<LlvmValue> stack)
    {
        var right = Pop(stack, offset);
        var left = Pop(stack, offset);
        var type = MergeNumericTypes(left.Type, right.Type, offset);
        var predicate = GetBranchPredicate(opCode, type);
        var instruction = type == LlvmValueType.Float32 ? "fcmp" : "icmp";
        var result = NextValue();
        EmitLine($"{result} = {instruction} {predicate} {GetLlvmType(type)} {left.Expression}, {right.Expression}");
        return new LlvmValue(result, LlvmValueType.Boolean);
    }

    private void EmitConversion(OpCode opCode, int offset, Stack<LlvmValue> stack)
    {
        var source = Pop(stack, offset);
        var target = opCode == OpCodes.Conv_R4 || opCode == OpCodes.Conv_R_Un
            ? LlvmValueType.Float32
            : opCode == OpCodes.Conv_U4
                ? LlvmValueType.UInt32
                : LlvmValueType.Int32;
        if (source.Type == target ||
            source.Type is LlvmValueType.Int32 or LlvmValueType.UInt32 &&
            target is LlvmValueType.Int32 or LlvmValueType.UInt32) {
            stack.Push(source with { Type = target });
            return;
        }

        var operation = (source.Type, target) switch {
            (LlvmValueType.Float32, LlvmValueType.Int32) => "fptosi",
            (LlvmValueType.Float32, LlvmValueType.UInt32) => "fptoui",
            (LlvmValueType.Int32, LlvmValueType.Float32) => "sitofp",
            (LlvmValueType.UInt32, LlvmValueType.Float32) => "uitofp",
            _ => throw CreateUnsupported(offset, $"Conversion from {source.Type} to {target} is not supported.")
        };
        var value = NextValue();
        EmitLine($"{value} = {operation} {GetLlvmType(source.Type)} {source.Expression} to {GetLlvmType(target)}");
        stack.Push(new LlvmValue(value, target));
    }

    private LlvmValue ToBoolean(LlvmValue value, int offset)
    {
        if (value.Type == LlvmValueType.Boolean) {
            return value;
        }
        if (value.Type is not (LlvmValueType.Int32 or LlvmValueType.UInt32)) {
            throw CreateUnsupported(offset, $"Value of type {value.Type} cannot be used as a branch condition.");
        }
        var result = NextValue();
        EmitLine($"{result} = icmp ne i32 {value.Expression}, 0");
        return new LlvmValue(result, LlvmValueType.Boolean);
    }

    private void EmitSwitch(
        IReadOnlyList<int> targets,
        int offset,
        IReadOnlyDictionary<int, int> blockIdsByOffset,
        Stack<LlvmValue> stack)
    {
        var selector = Pop(stack, offset);
        EnsureIntegerOperand(selector, "Switch selector", offset);
        var fallthrough = GetFallthroughBlock(offset, blockIdsByOffset);
        EmitLine($"switch i32 {selector.Expression}, label %bb{fallthrough} [");
        for (var index = 0; index < targets.Count; index++) {
            EmitLine($"  i32 {index}, label %bb{blockIdsByOffset[targets[index]]}");
        }
        EmitLine("]");
    }

    private static IReadOnlyList<LlvmValueType> DecodeLocalTypes(
        MetadataReader reader,
        StandaloneSignatureHandle signatureHandle)
    {
        if (signatureHandle.IsNil) {
            return [];
        }
        var types = reader.GetStandaloneSignature(signatureHandle)
            .DecodeLocalSignature(new KernelTypeProvider(), genericContext: null);
        return types.Select(GetType).ToArray();
    }

    private static LlvmValueType GetType(KernelType type) => type.Name switch {
        "System.Void" => LlvmValueType.Void,
        "System.Boolean" => LlvmValueType.Boolean,
        "System.Int32" => LlvmValueType.Int32,
        "System.UInt32" => LlvmValueType.UInt32,
        "System.Single" => LlvmValueType.Float32,
        "Sia.Spirv.UInt3" => LlvmValueType.UInt3,
        "Sia.Math.bool2" => LlvmValueType.Booleanx2,
        "Sia.Math.bool3" => LlvmValueType.Booleanx3,
        "Sia.Math.bool4" => LlvmValueType.Booleanx4,
        "Sia.Math.int2" => LlvmValueType.Int32x2,
        "Sia.Math.int3" => LlvmValueType.Int32x3,
        "Sia.Math.int4" => LlvmValueType.Int32x4,
        "Sia.Math.uint2" => LlvmValueType.UInt32x2,
        "Sia.Math.uint3" => LlvmValueType.UInt32x3,
        "Sia.Math.uint4" => LlvmValueType.UInt32x4,
        "Sia.Math.float2" => LlvmValueType.Float32x2,
        "Sia.Math.float3" => LlvmValueType.Float32x3,
        "Sia.Math.float4" => LlvmValueType.Float32x4,
        "Sia.Math.float2x2" => LlvmValueType.Float32x2x2,
        "Sia.Math.float2x3" => LlvmValueType.Float32x2x3,
        "Sia.Math.float2x4" => LlvmValueType.Float32x2x4,
        "Sia.Math.float3x2" => LlvmValueType.Float32x3x2,
        "Sia.Math.float3x3" => LlvmValueType.Float32x3x3,
        "Sia.Math.float3x4" => LlvmValueType.Float32x3x4,
        "Sia.Math.float4x2" => LlvmValueType.Float32x4x2,
        "Sia.Math.float4x3" => LlvmValueType.Float32x4x3,
        "Sia.Math.float4x4" => LlvmValueType.Float32x4x4,
        "Sia.Spirv.Texture2D" => LlvmValueType.Texture2DFloat,
        "Sia.Spirv.Texture2DArray" => LlvmValueType.Texture2DArrayFloat,
        "Sia.Spirv.Sampler" => LlvmValueType.Sampler,
        _ => throw new InvalidDataException($"CIL type '{type.Name}' is not supported by the LLVM backend.")
    };

    private static LlvmValueType GetScalarType(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 => LlvmValueType.Int32,
        SpirvScalarType.UInt32 => LlvmValueType.UInt32,
        SpirvScalarType.Float32 => LlvmValueType.Float32,
        SpirvScalarType.Int32x2 => LlvmValueType.Int32x2,
        SpirvScalarType.Int32x3 => LlvmValueType.Int32x3,
        SpirvScalarType.Int32x4 => LlvmValueType.Int32x4,
        SpirvScalarType.UInt32x2 => LlvmValueType.UInt32x2,
        SpirvScalarType.UInt32x3 => LlvmValueType.UInt32x3,
        SpirvScalarType.UInt32x4 => LlvmValueType.UInt32x4,
        SpirvScalarType.Float32x2 => LlvmValueType.Float32x2,
        SpirvScalarType.Float32x3 => LlvmValueType.Float32x3,
        SpirvScalarType.Float32x4 => LlvmValueType.Float32x4,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static LlvmValueType GetBufferType(SpirvScalarType type, bool readOnly) =>
        (type, readOnly) switch {
            (SpirvScalarType.Int32, true) => LlvmValueType.ReadOnlyBufferInt32,
            (SpirvScalarType.UInt32, true) => LlvmValueType.ReadOnlyBufferUInt32,
            (SpirvScalarType.Float32, true) => LlvmValueType.ReadOnlyBufferFloat32,
            (SpirvScalarType.Int32x2, true) => LlvmValueType.ReadOnlyBufferInt32x2,
            (SpirvScalarType.Int32x3, true) => LlvmValueType.ReadOnlyBufferInt32x3,
            (SpirvScalarType.Int32x4, true) => LlvmValueType.ReadOnlyBufferInt32x4,
            (SpirvScalarType.UInt32x2, true) => LlvmValueType.ReadOnlyBufferUInt32x2,
            (SpirvScalarType.UInt32x3, true) => LlvmValueType.ReadOnlyBufferUInt32x3,
            (SpirvScalarType.UInt32x4, true) => LlvmValueType.ReadOnlyBufferUInt32x4,
            (SpirvScalarType.Float32x2, true) => LlvmValueType.ReadOnlyBufferFloat32x2,
            (SpirvScalarType.Float32x3, true) => LlvmValueType.ReadOnlyBufferFloat32x3,
            (SpirvScalarType.Float32x4, true) => LlvmValueType.ReadOnlyBufferFloat32x4,
            (SpirvScalarType.Struct, true) => LlvmValueType.ReadOnlyBufferStruct,
            (SpirvScalarType.Int32, false) => LlvmValueType.BufferInt32,
            (SpirvScalarType.UInt32, false) => LlvmValueType.BufferUInt32,
            (SpirvScalarType.Float32, false) => LlvmValueType.BufferFloat32,
            (SpirvScalarType.Int32x2, false) => LlvmValueType.BufferInt32x2,
            (SpirvScalarType.Int32x3, false) => LlvmValueType.BufferInt32x3,
            (SpirvScalarType.Int32x4, false) => LlvmValueType.BufferInt32x4,
            (SpirvScalarType.UInt32x2, false) => LlvmValueType.BufferUInt32x2,
            (SpirvScalarType.UInt32x3, false) => LlvmValueType.BufferUInt32x3,
            (SpirvScalarType.UInt32x4, false) => LlvmValueType.BufferUInt32x4,
            (SpirvScalarType.Float32x2, false) => LlvmValueType.BufferFloat32x2,
            (SpirvScalarType.Float32x3, false) => LlvmValueType.BufferFloat32x3,
            (SpirvScalarType.Float32x4, false) => LlvmValueType.BufferFloat32x4,
            (SpirvScalarType.Struct, false) => LlvmValueType.BufferStruct,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

    private static LlvmValueType GetWorkgroupType(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 => LlvmValueType.WorkgroupInt32,
        SpirvScalarType.UInt32 => LlvmValueType.WorkgroupUInt32,
        SpirvScalarType.Float32 => LlvmValueType.WorkgroupFloat32,
        _ => throw new InvalidDataException(
            $"Workgroup-memory element type '{type}' is not supported; use int, uint, or float.")
    };

    private static LlvmValueType GetWorkgroupElementType(LlvmValueType type) => type switch {
        LlvmValueType.WorkgroupInt32 => LlvmValueType.Int32,
        LlvmValueType.WorkgroupUInt32 => LlvmValueType.UInt32,
        LlvmValueType.WorkgroupFloat32 => LlvmValueType.Float32,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static LlvmValueType GetBufferElementType(LlvmValueType type) => type switch {
        LlvmValueType.ReadOnlyBufferInt32 => LlvmValueType.Int32,
        LlvmValueType.ReadOnlyBufferUInt32 => LlvmValueType.UInt32,
        LlvmValueType.ReadOnlyBufferFloat32 => LlvmValueType.Float32,
        LlvmValueType.ReadOnlyBufferInt32x2 => LlvmValueType.Int32x2,
        LlvmValueType.ReadOnlyBufferInt32x3 => LlvmValueType.Int32x3,
        LlvmValueType.ReadOnlyBufferInt32x4 => LlvmValueType.Int32x4,
        LlvmValueType.ReadOnlyBufferUInt32x2 => LlvmValueType.UInt32x2,
        LlvmValueType.ReadOnlyBufferUInt32x3 => LlvmValueType.UInt32x3,
        LlvmValueType.ReadOnlyBufferUInt32x4 => LlvmValueType.UInt32x4,
        LlvmValueType.ReadOnlyBufferFloat32x2 => LlvmValueType.Float32x2,
        LlvmValueType.ReadOnlyBufferFloat32x3 => LlvmValueType.Float32x3,
        LlvmValueType.ReadOnlyBufferFloat32x4 => LlvmValueType.Float32x4,
        LlvmValueType.ReadOnlyBufferStruct => LlvmValueType.Struct,
        LlvmValueType.BufferInt32 => LlvmValueType.Int32,
        LlvmValueType.BufferUInt32 => LlvmValueType.UInt32,
        LlvmValueType.BufferFloat32 => LlvmValueType.Float32,
        LlvmValueType.BufferInt32x2 => LlvmValueType.Int32x2,
        LlvmValueType.BufferInt32x3 => LlvmValueType.Int32x3,
        LlvmValueType.BufferInt32x4 => LlvmValueType.Int32x4,
        LlvmValueType.BufferUInt32x2 => LlvmValueType.UInt32x2,
        LlvmValueType.BufferUInt32x3 => LlvmValueType.UInt32x3,
        LlvmValueType.BufferUInt32x4 => LlvmValueType.UInt32x4,
        LlvmValueType.BufferFloat32x2 => LlvmValueType.Float32x2,
        LlvmValueType.BufferFloat32x3 => LlvmValueType.Float32x3,
        LlvmValueType.BufferFloat32x4 => LlvmValueType.Float32x4,
        LlvmValueType.BufferStruct => LlvmValueType.Struct,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string GetBufferTargetType(LlvmValueType type) =>
        $"target(\"spirv.VulkanBuffer\", [0 x {GetBufferStorageType(type)}], 12, {(IsReadOnlyBuffer(type) ? 0 : 1)})";

    private static string GetBufferMangling(LlvmValueType type) =>
        $"tspirv.VulkanBuffer_a0{GetTypeMangling(GetBufferStorageElementType(type))}_12_{(IsReadOnlyBuffer(type) ? 0 : 1)}t";

    private static LlvmValueType GetBufferStorageElementType(LlvmValueType type) =>
        GetBufferElementType(type) == LlvmValueType.Struct
            ? LlvmValueType.UInt32
            : GetBufferElementType(type);

    private static string GetBufferStorageType(LlvmValueType type) =>
        GetLlvmType(GetBufferStorageElementType(type));

    private static string GetTypeMangling(LlvmValueType type)
    {
        if (TryGetScalarVector(type, out var scalarType, out var length)) {
            return $"v{length}{GetTypeMangling(scalarType)}";
        }
        return type == LlvmValueType.Float32 ? "f32" : "i32";
    }

    private static string GetParameterTargetType() =>
        "target(\"spirv.VulkanBuffer\", [0 x i32], 12, 0)";

    private static string GetParameterMangling() =>
        "tspirv.VulkanBuffer_a0i32_12_0t";

    private static string GetTexture2DTargetType() =>
        "target(\"spirv.Image\", float, 1, 2, 0, 0, 1, 0)";

    private static string GetTexture2DMangling() =>
        "tspirv.Image_f32_1_2_0_0_1_0t";

    private static string GetTexture2DLoadMangling() =>
        "v4f32.tspirv.Image_f32_1_2_0_0_1_0t.v2i32.i32.v2i32";

    private static string GetTexture2DSampleLevelMangling() =>
        "v4f32.tspirv.Image_f32_1_2_0_0_1_0t.tspirv.Samplert.v2f32.f32.v2i32";

    private static string GetTexture2DArrayTargetType() =>
        "target(\"spirv.Image\", float, 1, 2, 1, 0, 1, 0)";

    private static string GetTexture2DArrayMangling() =>
        "tspirv.Image_f32_1_2_1_0_1_0t";

    private static string GetTexture2DArrayLoadMangling() =>
        "v4f32.tspirv.Image_f32_1_2_1_0_1_0t.v3i32.i32.v2i32";

    private static string GetTexture2DArraySampleLevelMangling() =>
        "v4f32.tspirv.Image_f32_1_2_1_0_1_0t.tspirv.Samplert.v3f32.f32.v2i32";

    private static string GetSamplerTargetType() =>
        "target(\"spirv.Sampler\")";

    private static string GetSamplerMangling() =>
        "tspirv.Samplert";

    private static void EmitResourceName(StringBuilder module, string identifier, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        module.Append('@').Append(identifier)
            .Append(" = private unnamed_addr constant [").Append(bytes.Length + 1)
            .Append(" x i8] c\"");
        foreach (var value in bytes) {
            module.Append('\\').Append(value.ToString("X2"));
        }
        module.AppendLine("\\00\", align 1");
    }

    private static bool IsBuffer(LlvmValueType type) => type is
        LlvmValueType.ReadOnlyBufferInt32 or
        LlvmValueType.ReadOnlyBufferUInt32 or
        LlvmValueType.ReadOnlyBufferFloat32 or
        LlvmValueType.BufferInt32 or
        LlvmValueType.BufferUInt32 or
        LlvmValueType.BufferFloat32 or
        LlvmValueType.ReadOnlyBufferInt32x2 or LlvmValueType.ReadOnlyBufferInt32x3 or
        LlvmValueType.ReadOnlyBufferInt32x4 or LlvmValueType.ReadOnlyBufferUInt32x2 or
        LlvmValueType.ReadOnlyBufferUInt32x3 or LlvmValueType.ReadOnlyBufferUInt32x4 or
        LlvmValueType.ReadOnlyBufferFloat32x2 or LlvmValueType.ReadOnlyBufferFloat32x3 or
        LlvmValueType.ReadOnlyBufferFloat32x4 or LlvmValueType.BufferInt32x2 or
        LlvmValueType.BufferInt32x3 or LlvmValueType.BufferInt32x4 or
        LlvmValueType.BufferUInt32x2 or LlvmValueType.BufferUInt32x3 or
        LlvmValueType.BufferUInt32x4 or LlvmValueType.BufferFloat32x2 or
        LlvmValueType.BufferFloat32x3 or LlvmValueType.BufferFloat32x4 or
        LlvmValueType.ReadOnlyBufferStruct or LlvmValueType.BufferStruct;

    private static bool IsReadOnlyBuffer(LlvmValueType type) => type is
        LlvmValueType.ReadOnlyBufferInt32 or
        LlvmValueType.ReadOnlyBufferUInt32 or
        LlvmValueType.ReadOnlyBufferFloat32 or
        LlvmValueType.ReadOnlyBufferInt32x2 or LlvmValueType.ReadOnlyBufferInt32x3 or
        LlvmValueType.ReadOnlyBufferInt32x4 or LlvmValueType.ReadOnlyBufferUInt32x2 or
        LlvmValueType.ReadOnlyBufferUInt32x3 or LlvmValueType.ReadOnlyBufferUInt32x4 or
        LlvmValueType.ReadOnlyBufferFloat32x2 or LlvmValueType.ReadOnlyBufferFloat32x3 or
        LlvmValueType.ReadOnlyBufferFloat32x4 or
        LlvmValueType.ReadOnlyBufferStruct;

    private static bool IsWorkgroupMemory(LlvmValueType type) => type is
        LlvmValueType.WorkgroupInt32 or
        LlvmValueType.WorkgroupUInt32 or
        LlvmValueType.WorkgroupFloat32;

    private static string GetPointerType(int addressSpace) =>
        addressSpace == 0 ? "ptr" : $"ptr addrspace({addressSpace})";

    private static string GetLlvmType(LlvmValueType type) => type switch {
        LlvmValueType.Void => "void",
        LlvmValueType.Boolean => "i1",
        LlvmValueType.Int32 or LlvmValueType.UInt32 => "i32",
        LlvmValueType.Float32 => "float",
        LlvmValueType.UInt3 => "<3 x i32>",
        _ when TryGetScalarVector(type, out var scalarType, out var length) =>
            $"<{length} x {GetLlvmType(scalarType)}>",
        _ when TryGetMatrixShape(type, out var rows, out var columns) =>
            $"%sia.matrix.float{rows}x{columns}",
        LlvmValueType.Texture2DFloat => GetTexture2DTargetType(),
        LlvmValueType.Texture2DArrayFloat => GetTexture2DArrayTargetType(),
        LlvmValueType.Sampler => GetSamplerTargetType(),
        LlvmValueType.Struct => "%sia.struct",
        _ when IsBuffer(type) => GetBufferTargetType(type),
        _ when IsWorkgroupMemory(type) =>
            throw new InvalidOperationException("Workgroup-memory handles are not first-class LLVM values."),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static void EmitMatrixTypeDeclarations(StringBuilder module)
    {
        for (var rows = 2; rows <= 4; rows++) {
            for (var columns = 2; columns <= 4; columns++) {
                module.Append("%sia.matrix.float").Append(rows).Append('x').Append(columns)
                    .Append(" = type { ");
                for (var column = 0; column < columns; column++) {
                    if (column != 0) {
                        module.Append(", ");
                    }
                    module.Append('<').Append(rows).Append(" x float>");
                }
                module.AppendLine(" }");
            }
        }
        module.AppendLine();
    }

    private void EmitStructTypeDeclaration(StringBuilder module)
    {
        if (_structLayout == null) {
            return;
        }
        module.Append("%sia.struct = type { ")
            .Append(string.Join(", ", _structLayout.Fields.Select(field =>
                GetLlvmType(GetScalarType(field.Type)))))
            .AppendLine(" }");
        module.AppendLine();
    }

    private static int GetAlignment(LlvmValueType type)
    {
        if (TryGetScalarVector(type, out var scalarType, out var length)) {
            if (scalarType == LlvmValueType.Boolean) {
                return 1;
            }
            return length == 2 ? 8 : 16;
        }
        return type is LlvmValueType.UInt3 or LlvmValueType.Struct ||
            TryGetMatrixShape(type, out _, out _) ? 16 : 4;
    }

    private static bool TryGetVectorLength(LlvmValueType type, out int length)
    {
        length = type switch {
            LlvmValueType.Float32x2 => 2,
            LlvmValueType.Float32x3 => 3,
            LlvmValueType.Float32x4 => 4,
            _ => 0
        };
        return length != 0;
    }

    private static bool TryGetScalarVector(
        LlvmValueType type,
        out LlvmValueType scalarType,
        out int length)
    {
        (scalarType, length) = type switch {
            LlvmValueType.Booleanx2 => (LlvmValueType.Boolean, 2),
            LlvmValueType.Booleanx3 => (LlvmValueType.Boolean, 3),
            LlvmValueType.Booleanx4 => (LlvmValueType.Boolean, 4),
            LlvmValueType.Int32x2 => (LlvmValueType.Int32, 2),
            LlvmValueType.Int32x3 => (LlvmValueType.Int32, 3),
            LlvmValueType.Int32x4 => (LlvmValueType.Int32, 4),
            LlvmValueType.UInt32x2 => (LlvmValueType.UInt32, 2),
            LlvmValueType.UInt32x3 => (LlvmValueType.UInt32, 3),
            LlvmValueType.UInt32x4 => (LlvmValueType.UInt32, 4),
            LlvmValueType.Float32x2 => (LlvmValueType.Float32, 2),
            LlvmValueType.Float32x3 => (LlvmValueType.Float32, 3),
            LlvmValueType.Float32x4 => (LlvmValueType.Float32, 4),
            _ => (LlvmValueType.Void, 0)
        };
        return length != 0;
    }

    private static LlvmValueType GetVectorType(int length) => length switch {
        2 => LlvmValueType.Float32x2,
        3 => LlvmValueType.Float32x3,
        4 => LlvmValueType.Float32x4,
        _ => throw new ArgumentOutOfRangeException(nameof(length))
    };

    private static bool TryGetMatrixShape(
        LlvmValueType type,
        out int rows,
        out int columns)
    {
        (rows, columns) = type switch {
            LlvmValueType.Float32x2x2 => (2, 2),
            LlvmValueType.Float32x2x3 => (2, 3),
            LlvmValueType.Float32x2x4 => (2, 4),
            LlvmValueType.Float32x3x2 => (3, 2),
            LlvmValueType.Float32x3x3 => (3, 3),
            LlvmValueType.Float32x3x4 => (3, 4),
            LlvmValueType.Float32x4x2 => (4, 2),
            LlvmValueType.Float32x4x3 => (4, 3),
            LlvmValueType.Float32x4x4 => (4, 4),
            _ => (0, 0)
        };
        return rows != 0;
    }

    private static LlvmValueType GetMatrixType(int rows, int columns) => (rows, columns) switch {
        (2, 2) => LlvmValueType.Float32x2x2,
        (2, 3) => LlvmValueType.Float32x2x3,
        (2, 4) => LlvmValueType.Float32x2x4,
        (3, 2) => LlvmValueType.Float32x3x2,
        (3, 3) => LlvmValueType.Float32x3x3,
        (3, 4) => LlvmValueType.Float32x3x4,
        (4, 2) => LlvmValueType.Float32x4x2,
        (4, 3) => LlvmValueType.Float32x4x3,
        (4, 4) => LlvmValueType.Float32x4x4,
        _ => throw new ArgumentOutOfRangeException(nameof(rows))
    };

    private static LlvmValueType MergeNumericTypes(
        LlvmValueType left,
        LlvmValueType right,
        int offset)
    {
        if (left == right) {
            return left;
        }
        if (left is LlvmValueType.Int32 or LlvmValueType.UInt32 &&
            right is LlvmValueType.Int32 or LlvmValueType.UInt32) {
            return left == LlvmValueType.UInt32 || right == LlvmValueType.UInt32
                ? LlvmValueType.UInt32
                : LlvmValueType.Int32;
        }
        if ((left == LlvmValueType.Boolean && right is LlvmValueType.Int32 or LlvmValueType.UInt32) ||
            (right == LlvmValueType.Boolean && left is LlvmValueType.Int32 or LlvmValueType.UInt32)) {
            return LlvmValueType.Boolean;
        }
        throw CreateUnsupported(offset, $"Operands of type {left} and {right} are incompatible.");
    }

    private static void EnsureCompatible(LlvmValueType expected, LlvmValueType actual, int offset)
    {
        if (expected == actual ||
            expected is LlvmValueType.Int32 or LlvmValueType.UInt32 &&
            actual is LlvmValueType.Int32 or LlvmValueType.UInt32) {
            return;
        }
        // CIL has no bool-constant opcode — true/false are ldc.i4.1/0,
        // tagged Int32. The store's LLVM type comes from `expected`, so
        // accepting Int32/UInt32 here never emits a mismatched type.
        if (expected == LlvmValueType.Boolean && actual is LlvmValueType.Int32 or LlvmValueType.UInt32) {
            return;
        }
        throw CreateUnsupported(offset, $"Expected {expected}, but found {actual}.");
    }

    private static void EnsureInteger(LlvmValue value, string name, int offset)
    {
        if (value.Type is LlvmValueType.Int32 or LlvmValueType.UInt32) {
            return;
        }
        throw CreateUnsupported(offset, $"Texture coordinate '{name}' must be a 32-bit integer.");
    }

    private static bool TryGetArgumentIndex(OpCode opCode, object? operand, out int index)
    {
        index = opCode == OpCodes.Ldarg_0 ? 0 :
            opCode == OpCodes.Ldarg_1 ? 1 :
            opCode == OpCodes.Ldarg_2 ? 2 :
            opCode == OpCodes.Ldarg_3 ? 3 :
            opCode == OpCodes.Ldarg || opCode == OpCodes.Ldarg_S ? Convert.ToInt32(operand) : -1;
        return index >= 0;
    }

    private static bool TryGetArgumentAddressIndex(OpCode opCode, object? operand, out int index)
    {
        index = opCode == OpCodes.Ldarga || opCode == OpCodes.Ldarga_S ? Convert.ToInt32(operand) : -1;
        return index >= 0;
    }

    private static bool TryGetLocalIndex(
        OpCode opCode,
        object? operand,
        bool load,
        out int index)
    {
        index = load
            ? opCode == OpCodes.Ldloc_0 ? 0 : opCode == OpCodes.Ldloc_1 ? 1 :
                opCode == OpCodes.Ldloc_2 ? 2 : opCode == OpCodes.Ldloc_3 ? 3 :
                opCode == OpCodes.Ldloc || opCode == OpCodes.Ldloc_S ? Convert.ToInt32(operand) : -1
            : opCode == OpCodes.Stloc_0 ? 0 : opCode == OpCodes.Stloc_1 ? 1 :
                opCode == OpCodes.Stloc_2 ? 2 : opCode == OpCodes.Stloc_3 ? 3 :
                opCode == OpCodes.Stloc || opCode == OpCodes.Stloc_S ? Convert.ToInt32(operand) : -1;
        return index >= 0;
    }

    private static bool TryGetLocalAddressIndex(OpCode opCode, object? operand, out int index)
    {
        index = opCode == OpCodes.Ldloca || opCode == OpCodes.Ldloca_S ? Convert.ToInt32(operand) : -1;
        return index >= 0;
    }

    private static bool TryGetInt32Constant(OpCode opCode, object? operand, out int value)
    {
        if (opCode == OpCodes.Ldc_I4_M1) value = -1;
        else if (opCode == OpCodes.Ldc_I4_0) value = 0;
        else if (opCode == OpCodes.Ldc_I4_1) value = 1;
        else if (opCode == OpCodes.Ldc_I4_2) value = 2;
        else if (opCode == OpCodes.Ldc_I4_3) value = 3;
        else if (opCode == OpCodes.Ldc_I4_4) value = 4;
        else if (opCode == OpCodes.Ldc_I4_5) value = 5;
        else if (opCode == OpCodes.Ldc_I4_6) value = 6;
        else if (opCode == OpCodes.Ldc_I4_7) value = 7;
        else if (opCode == OpCodes.Ldc_I4_8) value = 8;
        else if (opCode == OpCodes.Ldc_I4 || opCode == OpCodes.Ldc_I4_S) {
            value = Convert.ToInt32(operand);
        } else {
            value = default;
            return false;
        }
        return true;
    }

    private static bool IsBinaryArithmetic(OpCode opCode) => opCode == OpCodes.Add ||
        opCode == OpCodes.Sub || opCode == OpCodes.Mul || opCode == OpCodes.Div ||
        opCode == OpCodes.Div_Un || opCode == OpCodes.Rem || opCode == OpCodes.Rem_Un ||
        opCode == OpCodes.And || opCode == OpCodes.Or || opCode == OpCodes.Xor ||
        opCode == OpCodes.Shl || opCode == OpCodes.Shr || opCode == OpCodes.Shr_Un;

    private static bool IsShift(OpCode opCode) => opCode == OpCodes.Shl ||
        opCode == OpCodes.Shr || opCode == OpCodes.Shr_Un;

    private static string GetBinaryInstruction(OpCode opCode, LlvmValueType type, int offset)
    {
        var floating = type == LlvmValueType.Float32;
        var integer = type is LlvmValueType.Int32 or LlvmValueType.UInt32;
        var bitwise = integer || type == LlvmValueType.Boolean;
        if (opCode == OpCodes.Add && floating) return "fadd";
        if (opCode == OpCodes.Add && integer) return "add";
        if (opCode == OpCodes.Sub && floating) return "fsub";
        if (opCode == OpCodes.Sub && integer) return "sub";
        if (opCode == OpCodes.Mul && floating) return "fmul";
        if (opCode == OpCodes.Mul && integer) return "mul";
        if (opCode == OpCodes.Div && floating) return "fdiv";
        if (opCode == OpCodes.Div && integer) return "sdiv";
        if (opCode == OpCodes.Div_Un && integer) return "udiv";
        if (opCode == OpCodes.Rem && floating) return "frem";
        if (opCode == OpCodes.Rem && integer) return "srem";
        if (opCode == OpCodes.Rem_Un && integer) return "urem";
        if (opCode == OpCodes.And && bitwise) return "and";
        if (opCode == OpCodes.Or && bitwise) return "or";
        if (opCode == OpCodes.Xor && bitwise) return "xor";
        if (opCode == OpCodes.Shl && integer) return "shl";
        if (opCode == OpCodes.Shr && integer) return "ashr";
        if (opCode == OpCodes.Shr_Un && integer) return "lshr";
        throw CreateUnsupported(offset, $"Binary opcode '{opCode.Name}' is not supported.");
    }

    private static bool IsConversion(OpCode opCode) => opCode == OpCodes.Conv_I4 ||
        opCode == OpCodes.Conv_U4 || opCode == OpCodes.Conv_R4 || opCode == OpCodes.Conv_R_Un;

    private static bool IsLoadIndirect(OpCode opCode) => opCode == OpCodes.Ldind_I4 ||
        opCode == OpCodes.Ldind_U4 || opCode == OpCodes.Ldind_R4;

    private static bool IsStoreIndirect(OpCode opCode) => opCode == OpCodes.Stind_I4 ||
        opCode == OpCodes.Stind_R4;

    private static bool IsRelationalBranch(OpCode opCode) => opCode.FlowControl == FlowControl.Cond_Branch &&
        opCode != OpCodes.Brtrue && opCode != OpCodes.Brtrue_S &&
        opCode != OpCodes.Brfalse && opCode != OpCodes.Brfalse_S &&
        opCode != OpCodes.Switch;

    private static string GetComparisonPredicate(OpCode opCode, LlvmValueType type)
    {
        if (opCode == OpCodes.Ceq) return type == LlvmValueType.Float32 ? "oeq" : "eq";
        if (opCode == OpCodes.Clt_Un) return type == LlvmValueType.Float32 ? "ult" : "ult";
        if (opCode == OpCodes.Cgt_Un) return type == LlvmValueType.Float32 ? "ugt" : "ugt";
        if (opCode == OpCodes.Clt) return type == LlvmValueType.Float32 ? "olt" : "slt";
        return type == LlvmValueType.Float32 ? "ogt" : "sgt";
    }

    private static string GetBranchPredicate(OpCode opCode, LlvmValueType type)
    {
        var unsigned = opCode == OpCodes.Blt_Un || opCode == OpCodes.Blt_Un_S ||
            opCode == OpCodes.Ble_Un || opCode == OpCodes.Ble_Un_S ||
            opCode == OpCodes.Bgt_Un || opCode == OpCodes.Bgt_Un_S ||
            opCode == OpCodes.Bge_Un || opCode == OpCodes.Bge_Un_S;
        var floating = type == LlvmValueType.Float32;
        if (opCode == OpCodes.Beq || opCode == OpCodes.Beq_S) return floating ? "oeq" : "eq";
        if (opCode == OpCodes.Bne_Un || opCode == OpCodes.Bne_Un_S) return floating ? "une" : "ne";
        if (opCode.Name!.StartsWith("blt", StringComparison.Ordinal)) {
            return floating ? unsigned ? "ult" : "olt" : unsigned ? "ult" : "slt";
        }
        if (opCode.Name.StartsWith("ble", StringComparison.Ordinal)) {
            return floating ? unsigned ? "ule" : "ole" : unsigned ? "ule" : "sle";
        }
        if (opCode.Name.StartsWith("bgt", StringComparison.Ordinal)) {
            return floating ? unsigned ? "ugt" : "ogt" : unsigned ? "ugt" : "sgt";
        }
        return floating ? unsigned ? "uge" : "oge" : unsigned ? "uge" : "sge";
    }

    private static void EnsureIntegerOperand(LlvmValue value, string name, int offset)
    {
        if (value.Type is LlvmValueType.Int32 or LlvmValueType.UInt32) {
            return;
        }
        throw CreateUnsupported(offset, $"{name} must be a 32-bit integer, but found {value.Type}.");
    }

    private static int GetFallthroughBlock(int offset, IReadOnlyDictionary<int, int> blockIdsByOffset)
    {
        var next = blockIdsByOffset.Keys.Where(candidate => candidate > offset).DefaultIfEmpty(-1).Min();
        if (next < 0) {
            throw CreateUnsupported(offset, "Conditional branch does not have a fallthrough block.");
        }
        return blockIdsByOffset[next];
    }

    private static LlvmValue Pop(Stack<LlvmValue> stack, int offset)
    {
        if (!stack.TryPop(out var value)) {
            throw CreateUnsupported(offset, "The CIL evaluation stack is empty.");
        }
        return value;
    }

    private string NextValue(string? hint = null)
    {
        var id = _nextValueId++;
        return hint == null ? $"%v{id}" : $"%{hint}.{id}";
    }

    private void EmitLine(string line) => _body.Append("  ").AppendLine(line);

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value) {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '.'
                ? character
                : '_');
        }
        return builder.Length == 0 ? "kernel" : builder.ToString();
    }

    private static string FormatFloat(float value) =>
        $"0x{BitConverter.DoubleToUInt64Bits(value):X16}";

    private sealed record EvaluationStackEdge(
        int PredecessorId,
        IReadOnlyList<LlvmValue> Values);

    private sealed record EvaluationStackPhi(
        int BlockId,
        int BlockOffset,
        int Position,
        LlvmValue Result,
        IReadOnlyList<EvaluationStackEdge> IncomingEdges);

    private static InvalidDataException CreateUnsupported(int offset, string message) =>
        new($"{message} (IL_{offset:x4})");
}
