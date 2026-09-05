namespace Sia.RenderGraph;

public static partial class RenderGraphCompiler
{
    public static CompiledRenderGraph Compile(RenderGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var dependencies = BuildDependencies(definition);
        var livePasses = FindLivePasses(definition, dependencies);
        var executionOrder = SortPasses(definition, dependencies, livePasses);
        var executionIndices = new int[definition.PassCount];
        Array.Fill(executionIndices, -1);
        for (var index = 0; index < executionOrder.Length; index++) {
            executionIndices[executionOrder[index]] = index;
        }

        var bufferUsage = BuildBufferUsage(definition, livePasses);
        var textureUsage = BuildTextureUsage(definition, livePasses);
        ValidateImportedUsage(definition, bufferUsage, textureUsage);
        var bufferLifetimes = BuildBufferLifetimes(definition, executionOrder);
        var textureLifetimes = BuildTextureLifetimes(definition, executionOrder);
        var passes = BuildCompiledPasses(
            definition,
            dependencies,
            executionOrder,
            executionIndices);
        var buffers = BuildCompiledBuffers(
            definition,
            bufferUsage,
            bufferLifetimes);
        var textures = BuildCompiledTextures(
            definition,
            textureUsage,
            textureLifetimes);
        var passGroups = BuildPassGroups(passes, textures);
        var passStatuses = BuildPassStatuses(definition, livePasses);

        return new CompiledRenderGraph(
            definition.GraphId,
            buffers,
            textures,
            passes,
            passGroups,
            passStatuses,
            RenderGraphStructureHasher.Compute(definition),
            definition.PassCount);
    }

    private static RenderGraphPassStatus[] BuildPassStatuses(
        RenderGraphDefinition definition,
        bool[] livePasses) =>
        definition.Passes.Select((pass, index) => new RenderGraphPassStatus(
            new RenderGraphPassHandle(definition.GraphId, index),
            pass.Name,
            pass.Kind,
            livePasses[index],
            pass.HasSideEffects)).ToArray();

    private static RenderGraphBufferUsage[] BuildBufferUsage(
        RenderGraphDefinition definition,
        bool[] livePasses)
    {
        var usage = definition.Buffers
            .Select(static resource =>
                resource.Descriptor.Usage | resource.ExportUsage)
            .ToArray();
        for (var passIndex = 0; passIndex < definition.PassCount; passIndex++) {
            if (!livePasses[passIndex]) {
                continue;
            }
            var pass = definition.Passes[passIndex];
            foreach (var use in pass.Buffers) {
                usage[use.BufferIndex] |= use.Usage;
            }
        }

        return usage;
    }

    private static RenderGraphTextureUsage[] BuildTextureUsage(
        RenderGraphDefinition definition,
        bool[] livePasses)
    {
        var usage = definition.Textures
            .Select(static resource =>
                resource.Descriptor.Usage | resource.ExportUsage)
            .ToArray();
        for (var passIndex = 0; passIndex < definition.PassCount; passIndex++) {
            if (!livePasses[passIndex]) {
                continue;
            }
            var pass = definition.Passes[passIndex];
            foreach (var use in pass.Textures) {
                usage[use.TextureIndex] |= use.Usage;
            }
        }

        return usage;
    }

    private static void ValidateImportedUsage(
        RenderGraphDefinition definition,
        RenderGraphBufferUsage[] bufferUsage,
        RenderGraphTextureUsage[] textureUsage)
    {
        for (var index = 0; index < definition.BufferCount; index++) {
            var resource = definition.Buffers[index];
            if (resource.IsImported &&
                (resource.Descriptor.Usage & bufferUsage[index]) != bufferUsage[index]) {
                throw new RenderGraphCompilationException(
                    $"Imported buffer '{resource.Descriptor.Name}' does not declare all required usage flags.");
            }
        }

        for (var index = 0; index < definition.TextureCount; index++) {
            var resource = definition.Textures[index];
            if (resource.IsImported &&
                (resource.Descriptor.Usage & textureUsage[index]) != textureUsage[index]) {
                throw new RenderGraphCompilationException(
                    $"Imported texture '{resource.Descriptor.Name}' does not declare all required usage flags.");
            }
        }
    }

    private static CompiledRenderGraphBuffer[] BuildCompiledBuffers(
        RenderGraphDefinition definition,
        RenderGraphBufferUsage[] usage,
        RenderGraphResourceLifetime[] lifetimes) =>
        definition.Buffers.Select((resource, index) =>
            new CompiledRenderGraphBuffer(
                new RenderGraphBufferHandle(definition.GraphId, index),
                resource.Descriptor,
                usage[index],
                lifetimes[index],
                resource.IsImported,
                resource.IsExported)).ToArray();

    private static CompiledRenderGraphTexture[] BuildCompiledTextures(
        RenderGraphDefinition definition,
        RenderGraphTextureUsage[] usage,
        RenderGraphResourceLifetime[] lifetimes) =>
        definition.Textures.Select((resource, index) =>
            new CompiledRenderGraphTexture(
                new RenderGraphTextureHandle(definition.GraphId, index),
                resource.Descriptor,
                usage[index],
                lifetimes[index],
                resource.IsImported,
                resource.IsExported)).ToArray();

    private static CompiledRenderGraphPass[] BuildCompiledPasses(
        RenderGraphDefinition definition,
        HashSet<int>[] dependencies,
        int[] executionOrder,
        int[] executionIndices)
    {
        var result = new CompiledRenderGraphPass[executionOrder.Length];
        for (var executionIndex = 0;
            executionIndex < executionOrder.Length;
            executionIndex++) {
            var passIndex = executionOrder[executionIndex];
            var pass = definition.Passes[passIndex];
            var passDependencies = dependencies[passIndex]
                .Where(index => executionIndices[index] >= 0)
                .OrderBy(index => executionIndices[index])
                .Select(index => new RenderGraphPassHandle(definition.GraphId, index))
                .ToArray();
            var buffers = pass.Buffers.Select(use =>
                new RenderGraphBufferAccess(
                    new RenderGraphBufferHandle(definition.GraphId, use.BufferIndex),
                    use.Access,
                    use.Usage,
                    use.Range)).ToArray();
            var textures = pass.Textures.Select(use =>
                new RenderGraphTextureAccess(
                    new RenderGraphTextureHandle(definition.GraphId, use.TextureIndex),
                    use.Access,
                    use.Usage,
                    use.Subresources)).ToArray();

            result[executionIndex] = new CompiledRenderGraphPass(
                new RenderGraphPassHandle(definition.GraphId, passIndex),
                pass.Name,
                pass.Kind,
                passIndex,
                executionIndex,
                passDependencies,
                buffers,
                textures);
        }

        return result;
    }
}
