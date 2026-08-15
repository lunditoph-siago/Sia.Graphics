namespace Sia.RenderGraph;

public static partial class RenderGraphCompiler
{
    /// <summary>
    /// Groups adjacent (in execution order) passes that write the exact same render-attachment
    /// texture set and share no other resource hazard into a single <see cref="RenderGraphPassGroup"/>.
    /// A backend may run every pass in a group inside one physical render pass instead of one
    /// per pass. This is purely additive metadata over an already-fixed execution order — it
    /// never reorders passes, and every pass belongs to exactly one group (a pass that can't
    /// merge with its neighbor simply gets a group of its own, identical to today's one-pass-
    /// per-physical-pass behavior).
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="passes"/>[<paramref name="nextIndex"/>] can join the open group
    /// that started at <paramref name="groupStart"/>, tested against its immediate predecessor
    /// (index <paramref name="nextIndex"/> - 1) — the only pass a physical render pass boundary
    /// would actually need to separate them from.
    /// </summary>
    private static bool CanMerge(
        CompiledRenderGraphPass[] passes,
        int groupStart,
        int nextIndex,
        RenderAttachmentSet groupAttachments,
        RenderAttachmentSet nextAttachments,
        CompiledRenderGraphTexture[] textures)
    {
        _ = groupStart;
        if (groupAttachments.Count == 0 || !groupAttachments.SetEquals(nextAttachments)) {
            return false;
        }

        var previous = passes[nextIndex - 1];
        var next = passes[nextIndex];

        foreach (var a in previous.Buffers) {
            foreach (var b in next.Buffers) {
                if (a.Buffer == b.Buffer &&
                    RenderGraphValidation.Overlaps(a.Range, b.Range) &&
                    (Writes(a.Access) || Writes(b.Access))) {
                    return false;
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
                    a.Subresources == b.Subresources) {
                    // The recognized shared render-attachment write itself — the point of the
                    // merge, not a hazard.
                    continue;
                }

                var format = textures[a.Texture.Index].Descriptor.Format;
                if (RenderGraphValidation.Overlaps(format, a.Subresources, b.Subresources) &&
                    (Writes(a.Access) || Writes(b.Access))) {
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
