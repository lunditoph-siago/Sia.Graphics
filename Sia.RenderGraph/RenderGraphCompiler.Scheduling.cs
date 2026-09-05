namespace Sia.RenderGraph;

public static partial class RenderGraphCompiler
{
    private static int[] SortPasses(
        RenderGraphDefinition definition,
        HashSet<int>[] dependencies,
        bool[] livePasses)
    {
        var outgoing = CreateLists<int>(definition.PassCount);
        var remainingDependencies = new int[definition.PassCount];
        var ready = new SortedSet<int>();
        for (var passIndex = 0; passIndex < definition.PassCount; passIndex++) {
            if (!livePasses[passIndex]) {
                continue;
            }
            remainingDependencies[passIndex] = dependencies[passIndex]
                .Count(dependency => livePasses[dependency]);
            if (remainingDependencies[passIndex] == 0) {
                ready.Add(passIndex);
            }
            foreach (var dependency in dependencies[passIndex]) {
                if (livePasses[dependency]) {
                    outgoing[dependency].Add(passIndex);
                }
            }
        }

        var livePassCount = livePasses.Count(static live => live);
        var executionOrder = new int[livePassCount];
        var executionIndex = 0;
        while (ready.Count != 0) {
            var passIndex = ready.Min;
            ready.Remove(passIndex);
            executionOrder[executionIndex++] = passIndex;
            foreach (var dependent in outgoing[passIndex]) {
                remainingDependencies[dependent]--;
                if (remainingDependencies[dependent] == 0) {
                    ready.Add(dependent);
                }
            }
        }

        if (executionIndex != livePassCount) {
            var cyclePasses = Enumerable.Range(0, definition.PassCount)
                .Where(index => livePasses[index] && remainingDependencies[index] != 0)
                .Select(index => $"'{definition.Passes[index].Name}'");
            throw new RenderGraphCompilationException(
                $"Render graph contains a dependency cycle involving {string.Join(", ", cyclePasses)}.");
        }

        return executionOrder;
    }

    private static RenderGraphResourceLifetime[] BuildBufferLifetimes(
        RenderGraphDefinition definition,
        int[] executionOrder)
    {
        var lifetimes = Enumerable
            .Repeat(RenderGraphResourceLifetime.Unused, definition.BufferCount)
            .ToArray();
        for (var executionIndex = 0;
            executionIndex < executionOrder.Length;
            executionIndex++) {
            foreach (var use in definition.Passes[executionOrder[executionIndex]].Buffers) {
                ExtendLifetime(lifetimes, use.BufferIndex, executionIndex);
            }
        }
        for (var index = 0; index < definition.BufferCount; index++) {
            if (definition.Buffers[index].IsExported) {
                ExtendLifetime(lifetimes, index, executionOrder.Length);
            }
        }

        return lifetimes;
    }

    private static RenderGraphResourceLifetime[] BuildTextureLifetimes(
        RenderGraphDefinition definition,
        int[] executionOrder)
    {
        var lifetimes = Enumerable
            .Repeat(RenderGraphResourceLifetime.Unused, definition.TextureCount)
            .ToArray();
        for (var executionIndex = 0;
            executionIndex < executionOrder.Length;
            executionIndex++) {
            foreach (var use in definition.Passes[executionOrder[executionIndex]].Textures) {
                ExtendLifetime(lifetimes, use.TextureIndex, executionIndex);
            }
        }
        for (var index = 0; index < definition.TextureCount; index++) {
            if (definition.Textures[index].IsExported) {
                ExtendLifetime(lifetimes, index, executionOrder.Length);
            }
        }

        return lifetimes;
    }

    private static void ExtendLifetime(
        RenderGraphResourceLifetime[] lifetimes,
        int resourceIndex,
        int executionIndex)
    {
        var lifetime = lifetimes[resourceIndex];
        lifetimes[resourceIndex] = lifetime.IsUsed
            ? lifetime with { LastUse = executionIndex }
            : new RenderGraphResourceLifetime(executionIndex, executionIndex);
    }

    private static List<T>[] CreateLists<T>(int count)
    {
        var result = new List<T>[count];
        for (var index = 0; index < count; index++) {
            result[index] = [];
        }

        return result;
    }
}
