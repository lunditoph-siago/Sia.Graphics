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

        for (var index = 1; index < passes.Length; index++) {
            var nextAttachments = GetRenderAttachmentWrites(passes[index]);
            if (CanMerge(passes, groupStart, index, groupAttachments, nextAttachments, textures)) {
                continue;
            }

            groups.Add(new RenderGraphPassGroup(groupStart, index - groupStart));
            groupStart = index;
            groupAttachments = nextAttachments;
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
        CompiledRenderGraphTexture[] textures)
    {
        if (passes[nextIndex].Kind != RenderGraphPassKind.Render ||
            groupAttachments.Count == 0 || !groupAttachments.SetEquals(nextAttachments)) {
            return false;
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
        foreach (var a in previous.Buffers) {
            foreach (var b in next.Buffers) {
                if (a.Buffer == b.Buffer &&
                    (Writes(a.Access) || Writes(b.Access))) {
                    return true;
                }
            }
        }

        foreach (var a in previous.Textures) {
            var aIsFusionPoint = groupAttachments.Contains((a.Texture, a.Subresources));
            foreach (var b in next.Textures) {
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
