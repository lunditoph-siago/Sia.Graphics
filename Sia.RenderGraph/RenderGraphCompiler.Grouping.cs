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
        var group = Describe(passes[0]);
        for (var index = 1; index < passes.Length; index++) {
            var next = Describe(passes[index]);
            if (CanMerge(passes.AsSpan(groupStart, index - groupStart), group,
                passes[index], next, textures)) {
                group = group with { CanFuseByAttachments = group.CanFuseByAttachments && next.CanFuseByAttachments };
                continue;
            }

            groups.Add(new RenderGraphPassGroup(groupStart, index - groupStart));
            groupStart = index;
            group = next;
        }

        groups.Add(new RenderGraphPassGroup(groupStart, passes.Length - groupStart));
        return [.. groups];

        static FusionInfo Describe(CompiledRenderGraphPass pass)
        {
            var attachments = new RenderAttachmentSet();
            foreach (var access in pass.Textures) {
                if ((access.Access is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite) &&
                    (access.Usage & RenderGraphTextureUsage.RenderAttachment) != 0) {
                    attachments.Add((access.Texture, access.Subresources));
                }
            }

            var canFuseByAttachments = pass.Kind == RenderGraphPassKind.Render;
            for (var i = 0; canFuseByAttachments && i < pass.Buffers.Count; i++) {
                canFuseByAttachments = pass.Buffers[i].Access is not
                    (RenderGraphAccess.Write or RenderGraphAccess.ReadWrite);
            }
            for (var i = 0; canFuseByAttachments && i < pass.Textures.Count; i++) {
                var access = pass.Textures[i];
                canFuseByAttachments = access.Access is not
                    (RenderGraphAccess.Write or RenderGraphAccess.ReadWrite) ||
                    access.Usage == RenderGraphTextureUsage.RenderAttachment;
            }
            for (var i = 0; canFuseByAttachments && i < pass.Textures.Count; i++) {
                var read = pass.Textures[i];
                if (read.Access is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite) { continue; }
                for (var j = 0; j < pass.Textures.Count; j++) {
                    var write = pass.Textures[j];
                    if ((write.Access is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite) &&
                        read.Texture == write.Texture) {
                        canFuseByAttachments = false;
                        break;
                    }
                }
            }
            return new(pass.Kind, attachments, canFuseByAttachments);
        }

        static bool CanMerge(
            ReadOnlySpan<CompiledRenderGraphPass> previousPasses,
            in FusionInfo group,
            CompiledRenderGraphPass nextPass,
            in FusionInfo next,
            CompiledRenderGraphTexture[] textures)
        {
            if (group.Kind != RenderGraphPassKind.Render || next.Kind != RenderGraphPassKind.Render ||
                group.Attachments.Count == 0 || !group.Attachments.SetEquals(next.Attachments)) {
                return false;
            }
            if (group.CanFuseByAttachments && next.CanFuseByAttachments) {
                return true;
            }
            foreach (var previous in previousPasses) {
                if (previous.Kind != RenderGraphPassKind.Render ||
                    HasFusionHazard(previous, nextPass, group.Attachments, textures)) {
                    return false;
                }
            }
            return true;
        }

        static bool HasFusionHazard(
            CompiledRenderGraphPass previous,
            CompiledRenderGraphPass next,
            RenderAttachmentSet attachments,
            CompiledRenderGraphTexture[] textures)
        {
            for (var i = 0; i < previous.Buffers.Count; i++) {
                var a = previous.Buffers[i];
                for (var j = 0; j < next.Buffers.Count; j++) {
                    var b = next.Buffers[j];
                    if (a.Buffer == b.Buffer &&
                        ((a.Access is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite) ||
                         (b.Access is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite))) {
                        return true;
                    }
                }
            }
            for (var i = 0; i < previous.Textures.Count; i++) {
                var a = previous.Textures[i];
                for (var j = 0; j < next.Textures.Count; j++) {
                    var b = next.Textures[j];
                    if (a.Texture != b.Texture ||
                        (a.Access is not (RenderGraphAccess.Write or RenderGraphAccess.ReadWrite) &&
                         b.Access is not (RenderGraphAccess.Write or RenderGraphAccess.ReadWrite))) {
                        continue;
                    }
                    var sameAttachment = a.Usage == RenderGraphTextureUsage.RenderAttachment &&
                        b.Usage == RenderGraphTextureUsage.RenderAttachment &&
                        a.Subresources == b.Subresources &&
                        attachments.Contains((a.Texture, a.Subresources));
                    if (!sameAttachment && RenderGraphValidation.Overlaps(
                        textures[a.Texture.Index].Descriptor.Format, a.Subresources, b.Subresources)) {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    private readonly record struct FusionInfo(
        RenderGraphPassKind Kind,
        RenderAttachmentSet Attachments,
        bool CanFuseByAttachments);

    private sealed class RenderAttachmentSet
        : HashSet<(RenderGraphTextureHandle Texture, RenderGraphTextureSubresourceRange Subresources)>;
}
