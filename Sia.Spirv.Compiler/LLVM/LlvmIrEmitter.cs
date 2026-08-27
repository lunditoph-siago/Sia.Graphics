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
    private SpirvKernelAbi _kernelAbi;
    private SpirvShaderStage _shaderStage;
    private bool _readsVertexIndex;
    private bool _readsInstanceIndex;
    private bool _readsFragmentPosition;
    private bool _writesPosition;
    private bool _usesTexture2DLoad;
    private bool _usesTexture2DArrayLoad;
    private bool _usesTexture2DArraySampleLevel;
    private bool _usesUnpackHalf;
    private bool _usesMin;
    private bool _usesMax;
    private bool _usesInverseSqrt;
    private bool _usesDiscard;
    private bool _usesSqrt;
    private bool _usesSin;
    private bool _usesCos;
    private bool _usesPow;
    private bool _usesAbs;
    private bool _usesFloat3Min;
    private bool _usesFloat3Max;
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
        var reader = peReader.GetMetadataReader();
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
        _kernelAbi = kernelAbi;
        _shaderStage = kernel.Stage;
        _readsVertexIndex = false;
        _readsInstanceIndex = false;
        _readsFragmentPosition = false;
        _writesPosition = false;
        _usesTexture2DLoad = false;
        _usesTexture2DArrayLoad = false;
        _usesTexture2DArraySampleLevel = false;
        _usesUnpackHalf = false;
        _usesMin = false;
        _usesMax = false;
        _usesInverseSqrt = false;
        _usesDiscard = false;
        _usesSqrt = false;
        _usesSin = false;
        _usesCos = false;
        _usesPow = false;
        _usesAbs = false;
        _usesFloat3Min = false;
        _usesFloat3Max = false;
        _nextValueId = 0;

        var parameterValues = new LlvmValue[kernel.Parameters.Count];
        var entryPoint = SanitizeIdentifier(kernel.Name);
        var prologue = new StringBuilder();
        EmitParameterGlobals(kernel, prologue, parameterValues);
        EmitLocalAllocations(localTypes, prologue);
        EmitBlocks(view, localTypes, parameterValues, prologue);
        if (kernel.Stage == SpirvShaderStage.Vertex && !_writesPosition) {
            throw new InvalidDataException(
                $"Vertex shader '{kernel.QualifiedName}' must call Gpu.SetPosition.");
        }

        var module = new StringBuilder();
        module.AppendLine("target triple = \"spirv64-unknown-vulkan1.2\"");
        module.AppendLine();
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
        var blockIdsByOffset = view.Graph.Blocks.ToDictionary(
            static block => block.StartOffset,
            static block => block.Id);

        // Only reachable blocks are emitted: a call the emitter does not
        // recognize inside dead code must never fail an otherwise-valid
        // shader (see ShaderCilView).
        foreach (var block in view.ReachableBlocks) {
            _body.Append("bb").Append(block.Id).AppendLine(":");
            if (block.Id == 0) {
                _body.Append(prologue);
            }

            var stack = new Stack<LlvmValue>();
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

            if (!terminated) {
                if (block.Successors.Count == 1) {
                    EmitLine($"br label %bb{block.Successors[0]}");
                }
                else if (block.Successors.Count == 0) {
                    EmitLine("ret void");
                }
                else {
                    throw CreateUnsupported(
                        block.Instructions[^1].Offset,
                        "A basic block with multiple successors requires an explicit conditional branch.");
                }
            }
        }
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
        if (IsStoreIndirect(opCode)) {
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
        }
        else if (hasPushConstants) {
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
            }
            else if (parameter.Kind == SpirvKernelParameterKind.SampledTexture2DArray) {
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
            }
            else if (parameter.Kind == SpirvKernelParameterKind.Sampler) {
                var value = NextValue(SanitizeIdentifier(parameter.Name));
                prologue.Append("  ").Append(value).Append(" = call ")
                    .Append(GetSamplerTargetType())
                    .Append(" @llvm.spv.resource.handlefrombinding.")
                    .Append(GetSamplerMangling()).Append("(i32 0, i32 ").Append(binding)
                    .Append(", i32 1, i32 0, ptr nonnull @.str.")
                    .Append(parameter.Position).AppendLine(")");
                values[parameter.Position] = new LlvmValue(value, LlvmValueType.Sampler);
                binding++;
            }
            else if (parameter.Kind is SpirvKernelParameterKind.ReadOnlyStorageBuffer or
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
            }
            else {
                var type = GetScalarType(parameter.ScalarType);
                string value;
                if (_kernelAbi == SpirvKernelAbi.Vulkan) {
                    value = NextValue(SanitizeIdentifier(parameter.Name));
                    prologue.Append("  ").Append(value).Append(" = extractvalue %sia.push.constants ")
                        .Append(pushConstantValue).Append(", ").Append(pushConstantIndex).AppendLine();
                }
                else {
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
                    }
                    else {
                        value = word;
                    }
                }
                values[parameter.Position] = new LlvmValue(value, type);
                pushConstantIndex++;
            }
        }
    }

    private void EmitLocalAllocations(IReadOnlyList<LlvmValueType> localTypes, StringBuilder prologue)
    {
        for (var index = 0; index < localTypes.Count; index++) {
            var type = localTypes[index];
            prologue.Append("  %local.").Append(index).Append(" = alloca ")
                .Append(GetLlvmType(type)).Append(", align ").Append(GetAlignment(type)).AppendLine();
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
        foreach (var type in _bufferTypes.OrderBy(static type => type)) {
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
        if (_usesUnpackHalf) {
            module.AppendLine("declare <2 x float> @llvm.spv.unpackhalf2x16.v2f32(i32)");
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
        if (_usesFloat3Min) {
            module.AppendLine("declare <3 x float> @llvm.minnum.v3f32(<3 x float>, <3 x float>)");
        }
        if (_usesFloat3Max) {
            module.AppendLine("declare <3 x float> @llvm.maxnum.v3f32(<3 x float>, <3 x float>)");
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

    // Every supported Gpu.*/Texture2D*/StorageBuffer<T> intrinsic, keyed by
    // IntrinsicKind (recovered via [SpirvIntrinsic]) — the only place a new
    // intrinsic needs a case, never a declaring-type/method-name string match.
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
            [IntrinsicKind.Min] = EmitMinMax,
            [IntrinsicKind.Max] = EmitMinMax,
            [IntrinsicKind.InverseSqrt] = EmitInverseSqrt,
            [IntrinsicKind.Saturate] = EmitSaturate,
            [IntrinsicKind.LessThan] = EmitRelational,
            [IntrinsicKind.LessThanOrEqual] = EmitRelational,
            [IntrinsicKind.GreaterThan] = EmitRelational,
            [IntrinsicKind.GreaterThanOrEqual] = EmitRelational,
            [IntrinsicKind.Equal] = EmitRelational,
            [IntrinsicKind.Select] = EmitSelect,
            [IntrinsicKind.Discard] = EmitDiscard,
            [IntrinsicKind.SetPosition] = EmitSetPosition,
            [IntrinsicKind.SetOutput] = EmitRasterOutput,
            [IntrinsicKind.SetFlatOutput] = EmitRasterOutput,
            [IntrinsicKind.BufferIndex] = EmitBufferIndex,
            [IntrinsicKind.Texture2DLoad] = EmitTexture2DLoad,
            [IntrinsicKind.Texture2DArrayLoad] = EmitTexture2DArrayLoad,
            [IntrinsicKind.Texture2DArraySampleLevel] = EmitTexture2DArraySampleLevel,
            [IntrinsicKind.Sqrt] = EmitSqrt,
            [IntrinsicKind.Sin] = EmitSin,
            [IntrinsicKind.Cos] = EmitCos,
            [IntrinsicKind.Pow] = EmitPow,
            [IntrinsicKind.Abs] = EmitAbs,
            [IntrinsicKind.Float3Construct] = EmitFloat3Construct,
            [IntrinsicKind.Float3Broadcast] = EmitFloat3Broadcast,
            [IntrinsicKind.Float3GetX] = EmitFloat3GetComponent,
            [IntrinsicKind.Float3GetY] = EmitFloat3GetComponent,
            [IntrinsicKind.Float3GetZ] = EmitFloat3GetComponent,
            [IntrinsicKind.Float3Add] = EmitFloat3Binary,
            [IntrinsicKind.Float3Subtract] = EmitFloat3Binary,
            [IntrinsicKind.Float3MultiplyVector] = EmitFloat3Binary,
            [IntrinsicKind.Float3DivideVector] = EmitFloat3Binary,
            [IntrinsicKind.Float3MultiplyScalar] = EmitFloat3ScalarBinary,
            [IntrinsicKind.Float3DivideScalar] = EmitFloat3ScalarBinary,
            [IntrinsicKind.Float3Negate] = EmitFloat3Negate,
            [IntrinsicKind.Float3Dot] = EmitFloat3Dot,
            [IntrinsicKind.Float3Cross] = EmitFloat3Cross,
            [IntrinsicKind.Float3Normalize] = EmitFloat3Normalize,
            [IntrinsicKind.Float3Min] = EmitFloat3MinMax,
            [IntrinsicKind.Float3Max] = EmitFloat3MinMax,
            [IntrinsicKind.Float3Reflect] = EmitFloat3Reflect,
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
            handler(this, kind, instance, arguments, offset, stack);
            return;
        }
        throw CreateUnsupported(
            offset,
            $"Call to '{call.DeclaringType}.{call.Name}' is not a supported GPU intrinsic.");
    }

    // newobj has no receiver to pop, so it can't share EmitCall's instance
    // handling. Only float3 constructors reach here — everything else is
    // already rejected by SpirvLegalityAnalyzer.
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
            handler(this, kind, default, arguments, offset, stack);
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
        throw CreateUnsupported(
            offset,
            "Gpu.Barrier is reserved but not implemented in the first LLVM backend slice.");
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
        EnsureCompatible(LlvmValueType.UInt32, arguments[0].Type, offset);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = bitcast i32 {arguments[0].Expression} to float");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitUnpackHalf(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.UInt32, arguments[0].Type, offset);
        var component = GetConstantIndex(arguments[1], 1, "component", offset);
        var unpacked = emitter.NextValue();
        emitter.EmitLine($"{unpacked} = call <2 x float> @llvm.spv.unpackhalf2x16.v2f32(i32 {arguments[0].Expression})");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <2 x float> {unpacked}, i32 {component}");
        emitter._usesUnpackHalf = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitMinMax(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
        var intrinsic = kind == IntrinsicKind.Min ? "minnum" : "maxnum";
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.{intrinsic}.f32(float {arguments[0].Expression}, float {arguments[1].Expression})");
        emitter._usesMin |= kind == IntrinsicKind.Min;
        emitter._usesMax |= kind == IntrinsicKind.Max;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
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

    private static void EmitSaturate(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        var lower = emitter.NextValue();
        emitter.EmitLine($"{lower} = call float @llvm.maxnum.f32(float {arguments[0].Expression}, float 0.000000e+00)");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call float @llvm.minnum.f32(float {lower}, float 1.000000e+00)");
        emitter._usesMin = true;
        emitter._usesMax = true;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitRelational(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        if (kind == IntrinsicKind.Equal && arguments[0].Type == LlvmValueType.UInt32) {
            EnsureCompatible(LlvmValueType.UInt32, arguments[1].Type, offset);
            var integerComparison = emitter.NextValue();
            emitter.EmitLine($"{integerComparison} = icmp eq i32 {arguments[0].Expression}, {arguments[1].Expression}");
            var integerResult = emitter.NextValue();
            emitter.EmitLine($"{integerResult} = zext i1 {integerComparison} to i32");
            stack.Push(new LlvmValue(integerResult, LlvmValueType.UInt32));
            return;
        }
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
        var predicate = kind switch {
            IntrinsicKind.LessThan => "olt",
            IntrinsicKind.LessThanOrEqual => "ole",
            IntrinsicKind.GreaterThan => "ogt",
            IntrinsicKind.GreaterThanOrEqual => "oge",
            _ => "oeq"
        };
        var comparison = emitter.NextValue();
        emitter.EmitLine($"{comparison} = fcmp {predicate} float {arguments[0].Expression}, {arguments[1].Expression}");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = zext i1 {comparison} to i32");
        stack.Push(new LlvmValue(result, LlvmValueType.UInt32));
    }

    private static void EmitSelect(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        EnsureCompatible(LlvmValueType.Float32, arguments[0].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
        EnsureCompatible(LlvmValueType.UInt32, arguments[2].Type, offset);
        var condition = emitter.NextValue();
        emitter.EmitLine($"{condition} = icmp ne i32 {arguments[2].Expression}, 0");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = select i1 {condition}, float {arguments[1].Expression}, float {arguments[0].Expression}");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
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
        if (!IsBuffer(instance.Type)) {
            throw CreateUnsupported(offset, "StorageBuffer<T>.this[] requires a storage-buffer receiver.");
        }
        var index = arguments[0];
        var pointer = emitter.NextValue();
        var targetType = GetBufferTargetType(instance.Type);
        var mangling = GetBufferMangling(instance.Type);
        emitter.EmitLine($"{pointer} = call ptr addrspace(11) @llvm.spv.resource.getpointer.p11.{mangling}({targetType} {instance.Expression}, i32 {index.Expression})");
        stack.Push(new LlvmValue(pointer, GetBufferElementType(instance.Type), true));
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
        emitter.EnsureShaderStage(SpirvShaderStage.Fragment, kind.ToString(), offset);
        EnsureInteger(arguments[0], "x", offset);
        EnsureInteger(arguments[1], "y", offset);
        var component = GetConstantIndex(arguments[2], 3, "component", offset);
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <2 x i32> poison, i32 {arguments[0].Expression}, i32 0");
        var coordinates = emitter.NextValue();
        emitter.EmitLine($"{coordinates} = insertelement <2 x i32> {first}, i32 {arguments[1].Expression}, i32 1");
        var texel = emitter.NextValue();
        emitter.EmitLine($"{texel} = call <4 x float> @llvm.spv.resource.load.level.{GetTexture2DLoadMangling()}({GetTexture2DTargetType()} {instance.Expression}, <2 x i32> {coordinates}, i32 0, <2 x i32> zeroinitializer)");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <4 x float> {texel}, i32 {component}");
        emitter._usesTexture2DLoad = true;
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
        emitter.EnsureShaderStage(SpirvShaderStage.Fragment, kind.ToString(), offset);
        EnsureInteger(arguments[0], "x", offset);
        EnsureInteger(arguments[1], "y", offset);
        EnsureInteger(arguments[2], "layer", offset);
        var component = GetConstantIndex(arguments[3], 3, "component", offset);
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <3 x i32> poison, i32 {arguments[0].Expression}, i32 0");
        var second = emitter.NextValue();
        emitter.EmitLine($"{second} = insertelement <3 x i32> {first}, i32 {arguments[1].Expression}, i32 1");
        var coordinates = emitter.NextValue();
        emitter.EmitLine($"{coordinates} = insertelement <3 x i32> {second}, i32 {arguments[2].Expression}, i32 2");
        var texel = emitter.NextValue();
        emitter.EmitLine($"{texel} = call <4 x float> @llvm.spv.resource.load.level.{GetTexture2DArrayLoadMangling()}({GetTexture2DArrayTargetType()} {instance.Expression}, <3 x i32> {coordinates}, i32 0, <2 x i32> zeroinitializer)");
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
        emitter.EnsureShaderStage(SpirvShaderStage.Fragment, kind.ToString(), offset);
        EnsureCompatible(LlvmValueType.Sampler, arguments[0].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[1].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[2].Type, offset);
        EnsureCompatible(LlvmValueType.Float32, arguments[3].Type, offset);
        var component = GetConstantIndex(arguments[4], 3, "component", offset);
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <3 x float> poison, float {arguments[1].Expression}, i32 0");
        var second = emitter.NextValue();
        emitter.EmitLine($"{second} = insertelement <3 x float> {first}, float {arguments[2].Expression}, i32 1");
        var coordinates = emitter.NextValue();
        emitter.EmitLine($"{coordinates} = insertelement <3 x float> {second}, float {arguments[3].Expression}, i32 2");
        var texel = emitter.NextValue();
        emitter.EmitLine($"{texel} = call <4 x float> @llvm.spv.resource.samplelevel.{GetTexture2DArraySampleLevelMangling()}({GetTexture2DArrayTargetType()} {instance.Expression}, {GetSamplerTargetType()} {arguments[0].Expression}, <3 x float> {coordinates}, float 0.000000e+00, <2 x i32> zeroinitializer)");
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

    // Broadcasts a scalar SSA value into a <3 x float>. Shared by the
    // Sia.Math.float3(float) constructor and the vec3-op-scalar handlers.
    private static string EmitFloat3BroadcastValue(LlvmIrEmitter emitter, string scalarExpression)
    {
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <3 x float> poison, float {scalarExpression}, i32 0");
        var second = emitter.NextValue();
        emitter.EmitLine($"{second} = insertelement <3 x float> {first}, float {scalarExpression}, i32 1");
        var third = emitter.NextValue();
        emitter.EmitLine($"{third} = insertelement <3 x float> {second}, float {scalarExpression}, i32 2");
        return third;
    }

    private static void EmitFloat3Construct(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <3 x float> poison, float {arguments[0].Expression}, i32 0");
        var second = emitter.NextValue();
        emitter.EmitLine($"{second} = insertelement <3 x float> {first}, float {arguments[1].Expression}, i32 1");
        var third = emitter.NextValue();
        emitter.EmitLine($"{third} = insertelement <3 x float> {second}, float {arguments[2].Expression}, i32 2");
        EmitFloat3ConstructorResult(emitter, instance, third, stack);
    }

    private static void EmitFloat3Broadcast(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var result = EmitFloat3BroadcastValue(emitter, arguments[0].Expression);
        EmitFloat3ConstructorResult(emitter, instance, result, stack);
    }

    // EmitNewobj only fires for a float3 used as an intermediate value.
    // `var v = new float3(...)` takes the local's address instead and calls
    // the ctor in place via EmitCall with an IsReference instance, which
    // must store into the receiver rather than push a value.
    private static void EmitFloat3ConstructorResult(
        LlvmIrEmitter emitter,
        LlvmValue instance,
        string value,
        Stack<LlvmValue> stack)
    {
        if (instance.IsReference) {
            emitter.EmitLine($"store <3 x float> {value}, ptr {instance.Expression}, align 16");
            return;
        }
        stack.Push(new LlvmValue(value, LlvmValueType.Float32x3));
    }

    private static void EmitFloat3GetComponent(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var component = kind switch {
            IntrinsicKind.Float3GetX => 0,
            IntrinsicKind.Float3GetY => 1,
            _ => 2
        };
        var vector = instance;
        if (vector.IsReference) {
            var loaded = emitter.NextValue();
            emitter.EmitLine($"{loaded} = load <3 x float>, ptr {vector.Expression}, align 16");
            vector = new LlvmValue(loaded, LlvmValueType.Float32x3);
        }
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <3 x float> {vector.Expression}, i32 {component}");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    private static void EmitFloat3Binary(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var op = kind switch {
            IntrinsicKind.Float3Add => "fadd",
            IntrinsicKind.Float3Subtract => "fsub",
            IntrinsicKind.Float3MultiplyVector => "fmul",
            IntrinsicKind.Float3DivideVector => "fdiv",
            _ => throw CreateUnsupported(offset, $"Unexpected float3 binary kind '{kind}'.")
        };
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = {op} <3 x float> {arguments[0].Expression}, {arguments[1].Expression}");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32x3));
    }

    private static void EmitFloat3ScalarBinary(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var op = kind == IntrinsicKind.Float3MultiplyScalar ? "fmul" : "fdiv";
        var broadcast = EmitFloat3BroadcastValue(emitter, arguments[1].Expression);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = {op} <3 x float> {arguments[0].Expression}, {broadcast}");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32x3));
    }

    private static void EmitFloat3Negate(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fneg <3 x float> {arguments[0].Expression}");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32x3));
    }

    private static void EmitFloat3Dot(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var result = EmitFloat3DotValue(emitter, arguments[0].Expression, arguments[1].Expression);
        stack.Push(new LlvmValue(result, LlvmValueType.Float32));
    }

    // Emits the scalar dot product of two already-live <3 x float> SSA
    // values; shared by the Dot/Normalize/Reflect handlers.
    private static string EmitFloat3DotValue(LlvmIrEmitter emitter, string left, string right)
    {
        var product = emitter.NextValue();
        emitter.EmitLine($"{product} = fmul <3 x float> {left}, {right}");
        var x = emitter.NextValue();
        emitter.EmitLine($"{x} = extractelement <3 x float> {product}, i32 0");
        var y = emitter.NextValue();
        emitter.EmitLine($"{y} = extractelement <3 x float> {product}, i32 1");
        var z = emitter.NextValue();
        emitter.EmitLine($"{z} = extractelement <3 x float> {product}, i32 2");
        var xy = emitter.NextValue();
        emitter.EmitLine($"{xy} = fadd float {x}, {y}");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fadd float {xy}, {z}");
        return result;
    }

    private static void EmitFloat3Cross(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var left = arguments[0].Expression;
        var right = arguments[1].Expression;
        var ax = ExtractLane(emitter, left, 0);
        var ay = ExtractLane(emitter, left, 1);
        var az = ExtractLane(emitter, left, 2);
        var bx = ExtractLane(emitter, right, 0);
        var by = ExtractLane(emitter, right, 1);
        var bz = ExtractLane(emitter, right, 2);
        var cx = FSub(emitter, FMul(emitter, ay, bz), FMul(emitter, az, by));
        var cy = FSub(emitter, FMul(emitter, az, bx), FMul(emitter, ax, bz));
        var cz = FSub(emitter, FMul(emitter, ax, by), FMul(emitter, ay, bx));

        var first = emitter.NextValue();
        emitter.EmitLine($"{first} = insertelement <3 x float> poison, float {cx}, i32 0");
        var second = emitter.NextValue();
        emitter.EmitLine($"{second} = insertelement <3 x float> {first}, float {cy}, i32 1");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = insertelement <3 x float> {second}, float {cz}, i32 2");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32x3));
    }

    private static string ExtractLane(LlvmIrEmitter emitter, string vector, int lane)
    {
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = extractelement <3 x float> {vector}, i32 {lane}");
        return result;
    }

    private static string FMul(LlvmIrEmitter emitter, string left, string right)
    {
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fmul float {left}, {right}");
        return result;
    }

    private static string FSub(LlvmIrEmitter emitter, string left, string right)
    {
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fsub float {left}, {right}");
        return result;
    }

    private static void EmitFloat3Normalize(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var vector = arguments[0].Expression;
        var lengthSquared = EmitFloat3DotValue(emitter, vector, vector);
        var inverseLength = emitter.NextValue();
        emitter.EmitLine($"{inverseLength} = call float @llvm.spv.rsqrt.f32(float {lengthSquared})");
        emitter._usesInverseSqrt = true;
        var broadcast = EmitFloat3BroadcastValue(emitter, inverseLength);
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fmul <3 x float> {vector}, {broadcast}");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32x3));
    }

    private static void EmitFloat3MinMax(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var intrinsic = kind == IntrinsicKind.Float3Min ? "minnum" : "maxnum";
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = call <3 x float> @llvm.{intrinsic}.v3f32(<3 x float> {arguments[0].Expression}, <3 x float> {arguments[1].Expression})");
        emitter._usesFloat3Min |= kind == IntrinsicKind.Float3Min;
        emitter._usesFloat3Max |= kind == IntrinsicKind.Float3Max;
        stack.Push(new LlvmValue(result, LlvmValueType.Float32x3));
    }

    private static void EmitFloat3Reflect(
        LlvmIrEmitter emitter,
        IntrinsicKind kind,
        LlvmValue instance,
        IReadOnlyList<LlvmValue> arguments,
        int offset,
        Stack<LlvmValue> stack)
    {
        var incident = arguments[0].Expression;
        var normal = arguments[1].Expression;
        var dot = EmitFloat3DotValue(emitter, incident, normal);
        var doubledDot = emitter.NextValue();
        emitter.EmitLine($"{doubledDot} = fmul float {dot}, 2.000000e+00");
        var broadcast = EmitFloat3BroadcastValue(emitter, doubledDot);
        var scaledNormal = emitter.NextValue();
        emitter.EmitLine($"{scaledNormal} = fmul <3 x float> {broadcast}, {normal}");
        var result = emitter.NextValue();
        emitter.EmitLine($"{result} = fsub <3 x float> {incident}, {scaledNormal}");
        stack.Push(new LlvmValue(result, LlvmValueType.Float32x3));
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
                $"Raster {name} must be a compile-time uint constant from 0 through {maximum}.");
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
        EmitLine($"{value} = load {GetLlvmType(type)}, ptr addrspace(11) {pointer.Expression}, align {GetAlignment(type)}");
        stack.Push(new LlvmValue(value, type));
    }

    private void EmitStoreIndirect(int offset, Stack<LlvmValue> stack)
    {
        var value = Pop(stack, offset);
        var pointer = Pop(stack, offset);
        if (!pointer.IsReference) {
            throw CreateUnsupported(offset, "Indirect store requires a managed reference produced by StorageBuffer<T>.");
        }
        EnsureCompatible(pointer.Type, value.Type, offset);
        EmitLine($"store {GetLlvmType(pointer.Type)} {value.Expression}, ptr addrspace(11) {pointer.Expression}, align {GetAlignment(pointer.Type)}");
    }

    private void EmitBinary(OpCode opCode, int offset, Stack<LlvmValue> stack)
    {
        var right = Pop(stack, offset);
        var left = Pop(stack, offset);
        var type = MergeNumericTypes(left.Type, right.Type, offset);
        var instruction = GetBinaryInstruction(opCode, type, offset);
        var result = NextValue();
        EmitLine($"{result} = {instruction} {GetLlvmType(type)} {left.Expression}, {right.Expression}");
        stack.Push(new LlvmValue(result, type));
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
        }
        else {
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
        "Sia.Math.float3" => LlvmValueType.Float32x3,
        "Sia.Spirv.Texture2D" => LlvmValueType.Texture2DFloat,
        "Sia.Spirv.Texture2DArray" => LlvmValueType.Texture2DArrayFloat,
        "Sia.Spirv.Sampler" => LlvmValueType.Sampler,
        _ => throw new InvalidDataException($"CIL type '{type.Name}' is not supported by the LLVM backend.")
    };

    private static LlvmValueType GetScalarType(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 => LlvmValueType.Int32,
        SpirvScalarType.UInt32 => LlvmValueType.UInt32,
        SpirvScalarType.Float32 => LlvmValueType.Float32,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static LlvmValueType GetBufferType(SpirvScalarType type, bool readOnly) =>
        (type, readOnly) switch {
            (SpirvScalarType.Int32, true) => LlvmValueType.ReadOnlyBufferInt32,
            (SpirvScalarType.UInt32, true) => LlvmValueType.ReadOnlyBufferUInt32,
            (SpirvScalarType.Float32, true) => LlvmValueType.ReadOnlyBufferFloat32,
            (SpirvScalarType.Int32, false) => LlvmValueType.BufferInt32,
            (SpirvScalarType.UInt32, false) => LlvmValueType.BufferUInt32,
            (SpirvScalarType.Float32, false) => LlvmValueType.BufferFloat32,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

    private static LlvmValueType GetBufferElementType(LlvmValueType type) => type switch {
        LlvmValueType.ReadOnlyBufferInt32 => LlvmValueType.Int32,
        LlvmValueType.ReadOnlyBufferUInt32 => LlvmValueType.UInt32,
        LlvmValueType.ReadOnlyBufferFloat32 => LlvmValueType.Float32,
        LlvmValueType.BufferInt32 => LlvmValueType.Int32,
        LlvmValueType.BufferUInt32 => LlvmValueType.UInt32,
        LlvmValueType.BufferFloat32 => LlvmValueType.Float32,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string GetBufferTargetType(LlvmValueType type) =>
        $"target(\"spirv.VulkanBuffer\", [0 x {GetLlvmType(GetBufferElementType(type))}], 12, {(IsReadOnlyBuffer(type) ? 0 : 1)})";

    private static string GetBufferMangling(LlvmValueType type) =>
        $"tspirv.VulkanBuffer_a0{(GetBufferElementType(type) == LlvmValueType.Float32 ? "f32" : "i32")}_12_{(IsReadOnlyBuffer(type) ? 0 : 1)}t";

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
        LlvmValueType.BufferFloat32;

    private static bool IsReadOnlyBuffer(LlvmValueType type) => type is
        LlvmValueType.ReadOnlyBufferInt32 or
        LlvmValueType.ReadOnlyBufferUInt32 or
        LlvmValueType.ReadOnlyBufferFloat32;

    private static string GetLlvmType(LlvmValueType type) => type switch {
        LlvmValueType.Void => "void",
        LlvmValueType.Boolean => "i1",
        LlvmValueType.Int32 or LlvmValueType.UInt32 => "i32",
        LlvmValueType.Float32 => "float",
        LlvmValueType.UInt3 => "<3 x i32>",
        LlvmValueType.Float32x3 => "<3 x float>",
        LlvmValueType.Texture2DFloat => GetTexture2DTargetType(),
        LlvmValueType.Texture2DArrayFloat => GetTexture2DArrayTargetType(),
        LlvmValueType.Sampler => GetSamplerTargetType(),
        _ when IsBuffer(type) => GetBufferTargetType(type),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static int GetAlignment(LlvmValueType type) =>
        type is LlvmValueType.UInt3 or LlvmValueType.Float32x3 ? 16 : 4;

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
        value = opCode == OpCodes.Ldc_I4_M1 ? -1 :
            opCode == OpCodes.Ldc_I4_0 ? 0 : opCode == OpCodes.Ldc_I4_1 ? 1 :
            opCode == OpCodes.Ldc_I4_2 ? 2 : opCode == OpCodes.Ldc_I4_3 ? 3 :
            opCode == OpCodes.Ldc_I4_4 ? 4 : opCode == OpCodes.Ldc_I4_5 ? 5 :
            opCode == OpCodes.Ldc_I4_6 ? 6 : opCode == OpCodes.Ldc_I4_7 ? 7 :
            opCode == OpCodes.Ldc_I4_8 ? 8 :
            opCode == OpCodes.Ldc_I4 || opCode == OpCodes.Ldc_I4_S ? Convert.ToInt32(operand) : int.MinValue;
        return value != int.MinValue;
    }

    private static bool IsBinaryArithmetic(OpCode opCode) => opCode == OpCodes.Add ||
        opCode == OpCodes.Sub || opCode == OpCodes.Mul || opCode == OpCodes.Div ||
        opCode == OpCodes.Div_Un || opCode == OpCodes.Rem || opCode == OpCodes.Rem_Un ||
        opCode == OpCodes.And || opCode == OpCodes.Or || opCode == OpCodes.Xor ||
        opCode == OpCodes.Shl || opCode == OpCodes.Shr || opCode == OpCodes.Shr_Un;

    private static string GetBinaryInstruction(OpCode opCode, LlvmValueType type, int offset)
    {
        var floating = type == LlvmValueType.Float32;
        if (opCode == OpCodes.Add) return floating ? "fadd" : "add";
        if (opCode == OpCodes.Sub) return floating ? "fsub" : "sub";
        if (opCode == OpCodes.Mul) return floating ? "fmul" : "mul";
        if (opCode == OpCodes.Div) return floating ? "fdiv" : "sdiv";
        if (opCode == OpCodes.Div_Un) return "udiv";
        if (opCode == OpCodes.Rem) return floating ? "frem" : "srem";
        if (opCode == OpCodes.Rem_Un) return "urem";
        if (opCode == OpCodes.And) return "and";
        if (opCode == OpCodes.Or) return "or";
        if (opCode == OpCodes.Xor) return "xor";
        if (opCode == OpCodes.Shl) return "shl";
        if (opCode == OpCodes.Shr) return "ashr";
        if (opCode == OpCodes.Shr_Un) return "lshr";
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
        if (opCode.Name!.StartsWith("blt", StringComparison.Ordinal)) return floating ? "olt" : unsigned ? "ult" : "slt";
        if (opCode.Name.StartsWith("ble", StringComparison.Ordinal)) return floating ? "ole" : unsigned ? "ule" : "sle";
        if (opCode.Name.StartsWith("bgt", StringComparison.Ordinal)) return floating ? "ogt" : unsigned ? "ugt" : "sgt";
        return floating ? "oge" : unsigned ? "uge" : "sge";
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

    private static InvalidDataException CreateUnsupported(int offset, string message) =>
        new($"{message} (IL_{offset:x4})");
}
