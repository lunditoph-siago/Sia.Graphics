using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Valid;

public static partial class ModuleValidator
{
    private sealed partial class Validator
    {
        private bool? usesVulkanMemoryModel;
        private bool UsesVulkanMemoryModel => usesVulkanMemoryModel ??= module.VulkanMemoryModel || ShaderMemoryRequirements.UsesCooperativeMemoryModel(module);
        private void MemoryAccess(SpirvMemoryAccess? memory, AddressSpace space, bool load, SourceSpan span)
        {
            if (memory is null) return;
            uint flags = memory.Flags;
            Require((flags & ~63u) == 0, "Unsupported per-access memory flags.", span);
            Require(((flags & 2) != 0) == memory.Alignment.HasValue, "Aligned memory access requires exactly one alignment literal.", span);
            if (memory.Alignment is uint alignment) Require(alignment != 0 && (alignment & (alignment - 1)) == 0, "Memory alignment must be a power of two.", span);
            Require(((flags & 8) != 0) == memory.AvailableScope.HasValue && ((flags & 16) != 0) == memory.VisibleScope.HasValue,
                "Memory availability/visibility operands do not match their flags.", span);
            Require((flags & (8 | 16 | 32)) == 0 || UsesVulkanMemoryModel, "Non-private and availability/visibility accesses require the Vulkan memory model.", span);
            Require((flags & (8 | 16)) == 0 || (flags & 32) != 0, "Availability/visibility access requires NonPrivatePointer.", span);
            Require((flags & (load ? 8u : 16u)) == 0, load ? "A load cannot make memory available." : "A store cannot make memory visible.", span);
            Require((flags & 32) == 0 || space is AddressSpace.Uniform or AddressSpace.Workgroup or AddressSpace.Storage,
                "NonPrivatePointer requires shared memory storage.", span);
            foreach (uint scope in new[] { memory.AvailableScope, memory.VisibleScope }.OfType<uint>())
            {
                Require(scope is >= 1 and <= 5, "Unsupported per-access memory scope.", span);
                if (scope == 2) Restrict(WorkgroupStages);
            }
        }
        private void CallMemoryAccess(Expression.Call call)
        {
            if (call.MemoryAccess is null) return;
            bool load = call.Function is "coopLoad" or "coopLoadT" or "atomicLoad" or "workgroupUniformLoad";
            bool store = call.Function is "coopStore" or "coopStoreT" or "atomicStore";
            Require(call.Binding == CallBinding.Builtin && (load || store) && call.AtomicMemory is null,
                "Per-access memory operands require an ordinary or cooperative load/store.", call.Span);
            int index = call.Function is "coopStore" or "coopStoreT" ? 1 : 0;
            Require(call.Arguments.Count > index && call.Arguments[index].Type is ShaderType.Pointer, "Memory access requires a pointer operand.", call.Span);
            Require(call.Function != "workgroupUniformLoad" || call.Arguments[index].Type is ShaderType.Pointer { Base: not ShaderType.Atomic },
                "Atomic workgroup uniform loads require atomic memory operands.", call.Span);
            MemoryAccess(call.MemoryAccess, ((ShaderType.Pointer)call.Arguments[index].Type).Space, load, call.Span);
        }

        private void ImageAccess(Expression.Call call)
        {
            if (call.ImageAccess is not { } memory) return;
            Require(call.Binding == CallBinding.Builtin && call.Function is "textureLoad" or "textureStore"
                && call.AtomicMemory is null && call.MemoryAccess is null, "Image access operands require an ordinary texture load/store.", call.Span);
            uint flags = memory.Flags;
            Require((flags & ~SpirvImageAccess.SupportedFlags) == 0, "Unsupported image access flags.", call.Span);
            Require(((flags & 256) != 0) == memory.AvailableScope.HasValue && ((flags & 512) != 0) == memory.VisibleScope.HasValue,
                "Image scope operands do not match their flags.", call.Span);
            Require((flags & 3840) == 0 || UsesVulkanMemoryModel, "Image memory operands require the Vulkan memory model.", call.Span);
            Require((flags & 768) == 0 || (flags & 1024) != 0, "Image availability/visibility requires NonPrivateTexel.", call.Span);
            Require((flags & (call.Function == "textureLoad" ? 256u : 512u)) == 0, "Image availability requires a write and visibility requires a read.", call.Span);
            Require((flags & 12288) != 12288, "SignExtend and ZeroExtend cannot be combined.", call.Span);
            Require((flags & 4096) == 0 || call.Arguments.FirstOrDefault()?.Type is ShaderType.Image { Component.Kind: ScalarKind.Sint or ScalarKind.Uint },
                "SignExtend requires integer texels.", call.Span);
            Require((flags & 8192) == 0 || call.Arguments.FirstOrDefault()?.Type is ShaderType.Image { Component.Kind: ScalarKind.Uint },
                "ZeroExtend requires unsigned integer texels.", call.Span);
            foreach (uint scope in new[] { memory.AvailableScope, memory.VisibleScope }.OfType<uint>()) {
                Require(scope is >= 1 and <= 5, "Unsupported image memory scope.", call.Span);
                if (scope == 2) Restrict(WorkgroupStages);
            }
        }
    }
}
