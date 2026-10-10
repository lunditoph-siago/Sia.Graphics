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
            Require((call.Binding == CallBinding.Builtin || !functions.ContainsKey(call.Function)) && (load || store) && call.AtomicMemory is null,
                "Per-access memory operands require an ordinary or cooperative load/store.", call.Span);
            int index = call.Function is "coopStore" or "coopStoreT" ? 1 : 0;
            Require(call.Arguments.Count > index && call.Arguments[index].Type is ShaderType.Pointer, "Memory access requires a pointer operand.", call.Span);
            Require(call.Function != "workgroupUniformLoad" || call.Arguments[index].Type is ShaderType.Pointer { Base: not ShaderType.Atomic },
                "Atomic workgroup uniform loads require atomic memory operands.", call.Span);
            MemoryAccess(call.MemoryAccess, ((ShaderType.Pointer)call.Arguments[index].Type).Space, load, call.Span);
        }
    }
}
