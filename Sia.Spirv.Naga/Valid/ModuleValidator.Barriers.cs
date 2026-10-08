using Sia.Spirv.Naga.IR;

namespace Sia.Spirv.Naga.Valid;

public static partial class ModuleValidator
{
    private sealed partial class Validator
    {
        private void BarrierMemory(SpirvBarrierMemory memory, bool control, SourceSpan span)
        {
            Require(control ? memory.ExecutionScope is 2 or 3 : memory.ExecutionScope is null, "Invalid barrier execution scope.", span);
            if (control && memory.ExecutionScope == 2 || memory.Scope == 2) Restrict(WorkgroupStages);
            Require(memory.Scope <= 5, "Unsupported barrier memory scope.", span);
            Require(memory.Scope != 5 || module.VulkanMemoryModel, "Queue-family barrier scope requires the Vulkan memory model.", span);
            const uint orders = 2 | 4 | 8 | 16, classes = 64 | 128 | 256 | 512 | 1024 | 2048 | 4096;
            uint order = memory.Semantics & orders, flags = memory.Semantics;
            Require((flags & ~(orders | classes | 8192u | 16384u)) == 0, "Unknown or non-barrier memory semantics bits.", span);
            Require(order == 0 || (order & (order - 1)) == 0, "Barrier specifies multiple memory orders.", span);
            Require(!module.VulkanMemoryModel || order != 16, "Sequentially consistent barrier order is unavailable with the Vulkan memory model.", span);
            Require((flags & (4096 | 8192 | 16384)) == 0 || module.VulkanMemoryModel, "Barrier output, availability and visibility semantics require the Vulkan memory model.", span);
            Require((flags & 8192) == 0 || order is 4 or 8, "Barrier MakeAvailable requires release semantics.", span);
            Require((flags & 16384) == 0 || order is 2 or 8, "Barrier MakeVisible requires acquire semantics.", span);
            Require(flags == 0 && control || order != 0 && (flags & (64 | 256 | 2048 | 4096)) != 0,
                "Nonempty barriers require a memory order and a shader memory class.", span);
            Require(!control || memory.Scope != 4 || order == 0, "Invocation-scope control barrier requires relaxed memory semantics.", span);
        }
    }
}
