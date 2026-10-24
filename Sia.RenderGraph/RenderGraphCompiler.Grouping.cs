namespace Sia.RenderGraph;

public static partial class RenderGraphCompiler
{
    private static RenderGraphPassGroup[] BuildPassGroups(
        CompiledRenderGraphPass[] passes,
        CompiledRenderGraphTexture[] textures)
    {
        if (passes.Length == 0) {
            return [];
        }

        var groups = new List<RenderGraphPassGroup>();
        var groupStart = 0;
        var groupAttachments = GetRenderAttachmentWrites(passes[0]);
        var groupOnlyWritesAttachments = OnlyWritesAttachments(passes[0]);

        for (var index = 1; index < passes.Length; index++) {
            var nextAttachments = GetRenderAttachmentWrites(passes[index]);
            var nextOnlyWritesAttachments = OnlyWritesAttachments(passes[index]);
            if (CanMerge(passes, groupStart, index, groupAttachments, nextAttachments, textures,
                groupOnlyWritesAttachments && nextOnlyWritesAttachments)) {
                groupOnlyWritesAttachments &= nextOnlyWritesAttachments;
                continue;
            }

            groups.Add(new RenderGraphPassGroup(groupStart, index - groupStart));
            groupStart = index;
            groupAttachments = nextAttachments;
            groupOnlyWritesAttachments = nextOnlyWritesAttachments;
        }

        groups.Add(new RenderGraphPassGroup(groupStart, passes.Length - groupStart));
        return [.. groups];
    }

    private static bool CanMerge(
        CompiledRenderGraphPass[] passes,
        int groupStart,
        int nextIndex,
        RenderAttachmentSet groupAttachments,
        RenderAttachmentSet nextAttachments,
        CompiledRenderGraphTexture[] textures,
        bool onlyWritesAttachments)
    {
        if (passes[nextIndex].Kind != RenderGraphPassKind.Render ||
            groupAttachments.Count == 0 || !groupAttachments.SetEquals(nextAttachments)) {
            return false;
        }
        // All writes target the equal attachment sets. Other accesses are reads
        // of unrelated resources, so no pairwise scan can find a hazard.
        if (onlyWritesAttachments) {
            return true;
        }

        var next = passes[nextIndex];
        for (var index = groupStart; index < nextIndex; index++) {
            if (passes[index].Kind != RenderGraphPassKind.Render ||
                HasFusionHazard(passes[index], next, groupAttachments, nextAttachments, textures)) {
                return false;
            }
        }
        return true;
    }

    private static bool HasFusionHazard(
        CompiledRenderGraphPass previous,
        CompiledRenderGraphPass next,
        RenderAttachmentSet groupAttachments,
        RenderAttachmentSet nextAttachments,
        CompiledRenderGraphTexture[] textures)
    {
        for (var i = 0; i < previous.Buffers.Count; i++) {
            var a = previous.Buffers[i];
            for (var j = 0; j < next.Buffers.Count; j++) {
                var b = next.Buffers[j];
                if (a.Buffer == b.Buffer &&
                    (Writes(a.Access) || Writes(b.Access))) {
                    return true;
                }
            }
        }

        for (var i = 0; i < previous.Textures.Count; i++) {
            var a = previous.Textures[i];
            var aIsFusionPoint = groupAttachments.Contains((a.Texture, a.Subresources));
            for (var j = 0; j < next.Textures.Count; j++) {
                var b = next.Textures[j];
                if (a.Texture != b.Texture) {
                    continue;
                }
                if (aIsFusionPoint && nextAttachments.Contains((b.Texture, b.Subresources)) &&
                    a.Usage == RenderGraphTextureUsage.RenderAttachment &&
                    b.Usage == RenderGraphTextureUsage.RenderAttachment &&
                    a.Subresources == b.Subresources) {
                    continue;
                }

                var format = textures[a.Texture.Index].Descriptor.Format;
                if (RenderGraphValidation.Overlaps(format, a.Subresources, b.Subresources) &&
                    (Writes(a.Access) || Writes(b.Access))) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool OnlyWritesAttachments(CompiledRenderGraphPass pass)
    {
        if (pass.Kind != RenderGraphPassKind.Render || pass.Textures.Count == 0) {
            return false;
        }
        for (var i = 0; i < pass.Buffers.Count; i++) {
            if (Writes(pass.Buffers[i].Access)) {
                return false;
            }
        }
        for (var i = 0; i < pass.Textures.Count; i++) {
            var access = pass.Textures[i];
            if (Writes(access.Access)) {
                if (access.Usage != RenderGraphTextureUsage.RenderAttachment) {
                    return false;
                }
                continue;
            }
            // A sampled/read attachment needs the general overlap checks, even
            // when its subresource range differs from the written attachment.
            for (var j = 0; j < pass.Textures.Count; j++) {
                var other = pass.Textures[j];
                if (other.Texture == access.Texture && Writes(other.Access)) {
                    return false;
                }
            }
        }
        return true;
    }

    private static RenderAttachmentSet GetRenderAttachmentWrites(CompiledRenderGraphPass pass)
    {
        var result = new RenderAttachmentSet();
        foreach (var access in pass.Textures) {
            if (Writes(access.Access) &&
                (access.Usage & RenderGraphTextureUsage.RenderAttachment) != 0) {
                result.Add((access.Texture, access.Subresources));
            }
        }
        return result;
    }

    private sealed class RenderAttachmentSet
        : HashSet<(RenderGraphTextureHandle Texture, RenderGraphTextureSubresourceRange Subresources)>;
}
