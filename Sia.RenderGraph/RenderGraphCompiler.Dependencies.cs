using System.Runtime.InteropServices;

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

        var bufferReaders = new List<BufferHistory>?[definition.BufferCount];
        var bufferWriters = new List<BufferHistory>?[definition.BufferCount];
        var textureReaders = new List<TextureHistory>?[definition.TextureCount];
        var textureWriters = new List<TextureHistory>?[definition.TextureCount];
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
                var writes = Writes(use.Access);
                if (bufferWriters[use.BufferIndex] is { } writers) {
                    AddBufferDependencies(dependencies, passIndex, CollectionsMarshal.AsSpan(writers), use.Range);
                }
                if (writes) {
                    if (bufferReaders[use.BufferIndex] is { } readers) {
                        AddBufferDependencies(dependencies, passIndex, CollectionsMarshal.AsSpan(readers), use.Range);
                    }
                    RemoveMatchingBufferHistory(bufferReaders[use.BufferIndex], use.Range);
                    RemoveMatchingBufferHistory(bufferWriters[use.BufferIndex], use.Range);
                }
                var history = writes ? bufferWriters : bufferReaders;
                (history[use.BufferIndex] ??= []).Add(new BufferHistory(passIndex, use.Range));
            }

            foreach (var use in pass.Textures) {
                var format = definition.Textures[use.TextureIndex].Descriptor.Format;
                var writes = Writes(use.Access);
                if (textureWriters[use.TextureIndex] is { } writers) {
                    AddTextureDependencies(dependencies, passIndex, CollectionsMarshal.AsSpan(writers), format, use.Subresources);
                }
                if (writes) {
                    if (textureReaders[use.TextureIndex] is { } readers) {
                        AddTextureDependencies(dependencies, passIndex, CollectionsMarshal.AsSpan(readers), format, use.Subresources);
                    }
                    RemoveMatchingTextureHistory(textureReaders[use.TextureIndex], use.Subresources);
                    RemoveMatchingTextureHistory(textureWriters[use.TextureIndex], use.Subresources);
                }
                var history = writes ? textureWriters : textureReaders;
                (history[use.TextureIndex] ??= []).Add(new TextureHistory(passIndex, use.Subresources));
            }

            foreach (var use in pass.Buffers.Where(static use => Writes(use.Access))) {
                AddInitializedBufferRange(initializedBuffers[use.BufferIndex], use.Range);
            }
            foreach (var use in pass.Textures.Where(static use => Writes(use.Access))) {
                AddInitializedTextureRange(initializedTextures[use.TextureIndex], use.Subresources);
            }
        }

        return dependencies;

        static void ValidateReads(
            RenderGraphDefinition definition,
            RenderGraphPassDefinition pass,
            List<RenderGraphBufferRange>[] initializedBuffers,
            List<RenderGraphTextureSubresourceRange>[] initializedTextures)
        {
            foreach (var use in pass.Buffers.Where(static use => Reads(use.Access))) {
                var resource = definition.Buffers[use.BufferIndex];
                if (!resource.IsImported &&
                    !IsBufferCovered(initializedBuffers[use.BufferIndex], use.Range)) {
                    throw new RenderGraphCompilationException(
                        $"Pass '{pass.Name}' reads uninitialized range " +
                        $"[{use.Range.Offset}, {use.Range.Offset + use.Range.Size}) " +
                        $"of transient buffer '{resource.Descriptor.Name}'.");
                }
            }

            foreach (var use in pass.Textures.Where(static use => Reads(use.Access))) {
                var resource = definition.Textures[use.TextureIndex];
                if (!resource.IsImported &&
                    !IsTextureCovered(
                        resource.Descriptor.Format,
                        initializedTextures[use.TextureIndex],
                        use.Subresources)) {
                    throw new RenderGraphCompilationException(
                        $"Pass '{pass.Name}' reads uninitialized subresources of " +
                        $"transient texture '{resource.Descriptor.Name}'.");
                }
            }

            static bool IsBufferCovered(
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

            static bool IsTextureCovered(
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

            static bool Reads(RenderGraphAccess access) =>
                access is RenderGraphAccess.Read or RenderGraphAccess.ReadWrite;
        }

        static void AddInitializedBufferRange(
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

        static void AddInitializedTextureRange(
            List<RenderGraphTextureSubresourceRange> initialized,
            RenderGraphTextureSubresourceRange range)
        {
            for (var index = initialized.Count - 1; index >= 0; index--) {
                if (CombineLayers(initialized[index], range) is { } combined) {
                    range = combined;
                    initialized.RemoveAt(index);
                }
            }
            initialized.Add(range);

            // Only the layer axis changes; a union must never bridge a mip or aspect hole.
            static RenderGraphTextureSubresourceRange? CombineLayers(
                in RenderGraphTextureSubresourceRange a,
                in RenderGraphTextureSubresourceRange b) =>
                a.Aspect == b.Aspect &&
                a.BaseMipLevel == b.BaseMipLevel && a.MipLevelCount == b.MipLevelCount &&
                a.BaseArrayLayer <= b.BaseArrayLayer + b.ArrayLayerCount &&
                b.BaseArrayLayer <= a.BaseArrayLayer + a.ArrayLayerCount
                    ? a with {
                        BaseArrayLayer = Math.Min(a.BaseArrayLayer, b.BaseArrayLayer),
                        ArrayLayerCount = Math.Max(a.BaseArrayLayer + a.ArrayLayerCount,
                            b.BaseArrayLayer + b.ArrayLayerCount) - Math.Min(a.BaseArrayLayer, b.BaseArrayLayer)
                    }
                    : null;
        }

        static void AddBufferDependencies(
            HashSet<int>[] dependencies,
            int passIndex,
            ReadOnlySpan<BufferHistory> history,
            RenderGraphBufferRange range)
        {
            foreach (var previous in history) {
                if (RenderGraphValidation.Overlaps(range, previous.Range)) {
                    AddDependency(dependencies, passIndex, previous.PassIndex);
                }
            }
        }

        static void AddTextureDependencies(
            HashSet<int>[] dependencies,
            int passIndex,
            ReadOnlySpan<TextureHistory> history,
            RenderGraphTextureFormat format,
            RenderGraphTextureSubresourceRange subresources)
        {
            foreach (var previous in history) {
                if (RenderGraphValidation.Overlaps(format, subresources, previous.Subresources)) {
                    AddDependency(dependencies, passIndex, previous.PassIndex);
                }
            }
        }

        static void AddDependency(
            HashSet<int>[] dependencies,
            int passIndex,
            int dependency)
        {
            if (dependency != passIndex) {
                dependencies[passIndex].Add(dependency);
            }
        }

        static void RemoveMatchingBufferHistory(
            List<BufferHistory>? history,
            RenderGraphBufferRange range)
        {
            if (history is null) { return; }
            for (var index = history.Count - 1; index >= 0; index--) {
                if (history[index].Range == range) {
                    history.RemoveAt(index);
                }
            }
        }

        static void RemoveMatchingTextureHistory(
            List<TextureHistory>? history,
            RenderGraphTextureSubresourceRange subresources)
        {
            if (history is null) { return; }
            for (var index = history.Count - 1; index >= 0; index--) {
                if (history[index].Subresources == subresources) {
                    history.RemoveAt(index);
                }
            }
        }
    }

    private static bool Writes(RenderGraphAccess access) =>
        access is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite;

    private readonly record struct BufferHistory(
        int PassIndex,
        RenderGraphBufferRange Range);

    private readonly record struct TextureHistory(
        int PassIndex,
        RenderGraphTextureSubresourceRange Subresources);
}
