namespace Sia.RenderGraph;

public static partial class RenderGraphCompiler
{
    private static HashSet<int>[] BuildDependencies(
        RenderGraphDefinition definition)
    {
        var dependencies = new HashSet<int>[definition.PassCount];
        for (var passIndex = 0; passIndex < definition.PassCount; passIndex++) {
            dependencies[passIndex] = [.. definition.Passes[passIndex].Dependencies];
        }

        var bufferHistory = CreateLists<BufferHistory>(definition.BufferCount);
        var textureHistory = CreateLists<TextureHistory>(definition.TextureCount);
        var initializedBuffers = CreateLists<RenderGraphBufferRange>(
            definition.BufferCount);
        var initializedTextures = CreateLists<RenderGraphTextureSubresourceRange>(
            definition.TextureCount);

        var declaredOrder = SortPasses(
            definition, dependencies, Enumerable.Repeat(true, definition.PassCount).ToArray());
        foreach (var passIndex in declaredOrder) {
            var pass = definition.Passes[passIndex];
            ValidateReads(
                definition,
                pass,
                initializedBuffers,
                initializedTextures);

            foreach (var use in pass.Buffers) {
                var history = bufferHistory[use.BufferIndex];
                var writes = Writes(use.Access);
                foreach (var previous in history) {
                    if ((writes || Writes(previous.Access)) &&
                        RenderGraphValidation.Overlaps(use.Range, previous.Range)) {
                        AddDependency(dependencies, passIndex, previous.PassIndex);
                    }
                }

                if (writes) {
                    RemoveMatchingHistory(history, use.Range);
                }
                history.Add(new BufferHistory(
                    passIndex,
                    use.Access,
                    use.Range));
            }

            foreach (var use in pass.Textures) {
                var format = definition.Textures[use.TextureIndex].Descriptor.Format;
                var history = textureHistory[use.TextureIndex];
                var writes = Writes(use.Access);
                foreach (var previous in history) {
                    if ((writes || Writes(previous.Access)) &&
                        RenderGraphValidation.Overlaps(
                            format,
                            use.Subresources,
                            previous.Subresources)) {
                        AddDependency(dependencies, passIndex, previous.PassIndex);
                    }
                }

                if (writes) {
                    RemoveMatchingHistory(history, use.Subresources);
                }
                history.Add(new TextureHistory(
                    passIndex,
                    use.Access,
                    use.Subresources));
            }

            foreach (var use in pass.Buffers.Where(static use => Writes(use.Access))) {
                AddInitializedRange(initializedBuffers[use.BufferIndex], use.Range);
            }
            foreach (var use in pass.Textures.Where(static use => Writes(use.Access))) {
                initializedTextures[use.TextureIndex].Add(use.Subresources);
            }
        }

        return dependencies;
    }

    private static void ValidateReads(
        RenderGraphDefinition definition,
        RenderGraphPassDefinition pass,
        List<RenderGraphBufferRange>[] initializedBuffers,
        List<RenderGraphTextureSubresourceRange>[] initializedTextures)
    {
        foreach (var use in pass.Buffers.Where(static use => Reads(use.Access))) {
            var resource = definition.Buffers[use.BufferIndex];
            if (!resource.IsImported &&
                !IsCovered(initializedBuffers[use.BufferIndex], use.Range)) {
                throw new RenderGraphCompilationException(
                    $"Pass '{pass.Name}' reads uninitialized range " +
                    $"[{use.Range.Offset}, {use.Range.Offset + use.Range.Size}) " +
                    $"of transient buffer '{resource.Descriptor.Name}'.");
            }
        }

        foreach (var use in pass.Textures.Where(static use => Reads(use.Access))) {
            var resource = definition.Textures[use.TextureIndex];
            if (!resource.IsImported &&
                !IsCovered(
                    resource.Descriptor.Format,
                    initializedTextures[use.TextureIndex],
                    use.Subresources)) {
                throw new RenderGraphCompilationException(
                    $"Pass '{pass.Name}' reads uninitialized subresources of " +
                    $"transient texture '{resource.Descriptor.Name}'.");
            }
        }
    }

    private static bool IsCovered(
        List<RenderGraphBufferRange> initialized,
        RenderGraphBufferRange requested)
    {
        var end = requested.Offset + requested.Size;
        var cursor = requested.Offset;
        foreach (var range in initialized) {
            var rangeEnd = range.Offset + range.Size;
            if (rangeEnd <= cursor) {
                continue;
            }
            if (range.Offset > cursor) {
                return false;
            }

            cursor = Math.Max(cursor, rangeEnd);
            if (cursor >= end) {
                return true;
            }
        }

        return false;
    }

    private static void AddInitializedRange(
        List<RenderGraphBufferRange> initialized,
        RenderGraphBufferRange range)
    {
        var start = range.Offset;
        var end = start + range.Size;
        var index = 0;
        while (index < initialized.Count &&
            initialized[index].Offset + initialized[index].Size < start) {
            index++;
        }
        var first = index;
        while (index < initialized.Count && initialized[index].Offset <= end) {
            var previous = initialized[index];
            var previousEnd = previous.Offset + previous.Size;
            if (previous.Offset <= start && previousEnd >= end) {
                return;
            }
            start = Math.Min(start, previous.Offset);
            end = Math.Max(end, previousEnd);
            index++;
        }
        if (index > first) {
            initialized.RemoveRange(first, index - first);
        }
        initialized.Insert(first, new RenderGraphBufferRange(start, end - start));
    }

    private static bool IsCovered(
        RenderGraphTextureFormat format,
        List<RenderGraphTextureSubresourceRange> initialized,
        RenderGraphTextureSubresourceRange requested)
    {
        var availableAspects = RenderGraphValidation.GetFormatAspects(format);
        var requestedAspects = RenderGraphValidation.GetRequestedAspects(
            availableAspects,
            requested.Aspect);
        var mipEnd = requested.BaseMipLevel + requested.MipLevelCount;
        var layerEnd = requested.BaseArrayLayer + requested.ArrayLayerCount;

        foreach (var range in initialized) {
            if (range.BaseMipLevel <= requested.BaseMipLevel &&
                range.BaseMipLevel + range.MipLevelCount >= mipEnd &&
                range.BaseArrayLayer <= requested.BaseArrayLayer &&
                range.BaseArrayLayer + range.ArrayLayerCount >= layerEnd &&
                (RenderGraphValidation.GetRequestedAspects(availableAspects, range.Aspect) &
                    requestedAspects) == requestedAspects) {
                return true;
            }
        }

        for (var mip = requested.BaseMipLevel; mip < mipEnd; mip++) {
            for (var layer = requested.BaseArrayLayer; layer < layerEnd; layer++) {
                var coveredAspects = RenderGraphFormatAspects.None;
                foreach (var range in initialized) {
                    if (mip >= range.BaseMipLevel &&
                        mip < range.BaseMipLevel + range.MipLevelCount &&
                        layer >= range.BaseArrayLayer &&
                        layer < range.BaseArrayLayer + range.ArrayLayerCount) {
                        coveredAspects |= RenderGraphValidation.GetRequestedAspects(
                            availableAspects, range.Aspect);
                        if ((coveredAspects & requestedAspects) == requestedAspects) {
                            break;
                        }
                    }
                }
                if ((coveredAspects & requestedAspects) != requestedAspects) {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool Reads(RenderGraphAccess access) =>
        access is RenderGraphAccess.Read or RenderGraphAccess.ReadWrite;

    private static bool Writes(RenderGraphAccess access) =>
        access is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite;

    private static void AddDependency(
        HashSet<int>[] dependencies,
        int passIndex,
        int dependency)
    {
        if (dependency != passIndex) {
            dependencies[passIndex].Add(dependency);
        }
    }

    private static void RemoveMatchingHistory(
        List<BufferHistory> history,
        RenderGraphBufferRange range)
    {
        for (var index = history.Count - 1; index >= 0; index--) {
            if (history[index].Range == range) {
                history.RemoveAt(index);
            }
        }
    }

    private static void RemoveMatchingHistory(
        List<TextureHistory> history,
        RenderGraphTextureSubresourceRange subresources)
    {
        for (var index = history.Count - 1; index >= 0; index--) {
            if (history[index].Subresources == subresources) {
                history.RemoveAt(index);
            }
        }
    }

    private readonly record struct BufferHistory(
        int PassIndex,
        RenderGraphAccess Access,
        RenderGraphBufferRange Range);

    private readonly record struct TextureHistory(
        int PassIndex,
        RenderGraphAccess Access,
        RenderGraphTextureSubresourceRange Subresources);
}
