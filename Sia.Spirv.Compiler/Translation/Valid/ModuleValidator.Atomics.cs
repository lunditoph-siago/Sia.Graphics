using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Valid;

public static partial class ModuleValidator
{
    private sealed partial class Validator
    {
        private void AtomicMemory(Expression.Call call)
        {
            bool compare = call.Function == "spirvAtomicCompareExchange";
            Require(!compare || call.AtomicMemory?.UnequalSemantics is not null,
                "Native compare/exchange requires equal and unequal memory semantics.", call.Span);
            if (call.AtomicMemory is not { } memory) return;
            Require(!functions.ContainsKey(call.Function) && (call.Function.StartsWith("atomic", StringComparison.Ordinal)
                || call.Function.StartsWith("textureAtomic", StringComparison.Ordinal) || compare),
                "Native atomic memory operands require an atomic operation.", call.Span);
            Require(memory.Scope <= 5, "Unsupported atomic memory scope.", call.Span);
            Require(memory.Scope != 5 || module.VulkanMemoryModel, "Queue-family scope requires the Vulkan memory model.", call.Span);
            if (memory.Scope == 2) Restrict(WorkgroupStages);
            Require(memory.UnequalSemantics is null || compare || call.Function == "atomicCompareExchangeWeak",
                "Unequal memory semantics require compare/exchange.", call.Span);
            void Semantics(uint semantics, bool unequal = false)
            {
                const uint orders = 2 | 4 | 8 | 16, classes = 64 | 128 | 256 | 512 | 1024 | 2048 | 4096;
                uint order = semantics & orders;
                Require((semantics & ~(orders | classes | 8192u | 16384u | 32768u)) == 0,
                    "Unknown atomic memory semantics bits.", call.Span);
                Require(order == 0 || (order & (order - 1)) == 0, "Atomic memory semantics specify multiple memory orders.", call.Span);
                Require(!module.VulkanMemoryModel || order != 16, "Sequentially consistent order is unavailable with the Vulkan memory model.", call.Span);
                Require((semantics & (4096 | 8192 | 16384 | 32768)) == 0 || module.VulkanMemoryModel,
                    "Output, availability, visibility and volatile semantics require the Vulkan memory model.", call.Span);
                Require((semantics & (8192 | 16384)) == 0 || (semantics & classes) != 0,
                    "Availability and visibility semantics require a memory class.", call.Span);
                Require((semantics & 8192) == 0 || order is 4 or 8, "MakeAvailable requires release semantics.", call.Span);
                Require((semantics & 16384) == 0 || order is 2 or 8, "MakeVisible requires acquire semantics.", call.Span);
                Require(memory.Scope != 4 || order == 0, "Invocation scope requires relaxed memory order.", call.Span);
                Require(call.Function != "atomicLoad" || order is not (4 or 8), "Atomic load cannot use release semantics.", call.Span);
                Require(call.Function != "atomicStore" || order is not (2 or 8), "Atomic store cannot use acquire semantics.", call.Span);
                if (unequal)
                {
                    uint equalOrder = memory.Semantics & orders;
                    Require(order is not (4 or 8), "Unequal compare/exchange semantics cannot release memory.", call.Span);
                    Require(order == 0 || order == equalOrder || order == 2 && equalOrder is 8 or 16,
                        "Unequal compare/exchange order cannot be stronger than equal order.", call.Span);
                    Require((semantics & 32768) == (memory.Semantics & 32768),
                        "Compare/exchange volatile semantics must match.", call.Span);
                }
            }
            Semantics(memory.Semantics);
            if (memory.UnequalSemantics is uint unequal) Semantics(unequal, true);
            else if (call.Function == "atomicCompareExchangeWeak") Semantics(memory.Semantics, true);
        }
    }
}
