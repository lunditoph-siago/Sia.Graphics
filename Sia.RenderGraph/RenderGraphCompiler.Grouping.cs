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
        var group = DescribeFusion(passes[0]);
        for (var index = 1; index < passes.Length; index++) {
            var next = DescribeFusion(passes[index]);
            if (CanMerge(passes.AsSpan(groupStart, index - groupStart),
                group, passes[index], next, textures)) {
                group = group with {
                    CanFuseByAttachments = group.CanFuseByAttachments && next.CanFuseByAttachments
                };
                continue;
            }

            groups.Add(new RenderGraphPassGroup(groupStart, index - groupStart));
            groupStart = index;
            group = next;
        }

        groups.Add(new RenderGraphPassGroup(groupStart, passes.Length - groupStart));
        return [.. groups];

        static FusionInfo DescribeFusion(CompiledRenderGraphPass pass)
        {
            var attachments = GetRenderAttachmentWrites(pass);
            return new FusionInfo(pass.Kind, attachments, CanFuseByAttachments(pass, attachments));
        }

        static RenderAttachmentSet GetRenderAttachmentWrites(CompiledRenderGraphPass pass)
        {
            var attachments = new RenderAttachmentSet();
            foreach (var access in pass.Textures) {
                if (Writes(access.Access) &&
                    (access.Usage & RenderGraphTextureUsage.RenderAttachment) != 0) {
                    attachments.Add((access.Texture, access.Subresources));
                }
            }
            return attachments;
        }

        static bool CanFuseByAttachments(
            CompiledRenderGraphPass pass,
            RenderAttachmentSet attachments)
        {
            if (pass.Kind != RenderGraphPassKind.Render || HasNonAttachmentWrites(pass)) {
                return false;
            }
            for (var index = 0; index < pass.Textures.Count; index++) {
                var access = pass.Textures[index];
                if (Writes(access.Access)) {
                    continue;
                }

                foreach (var attachment in attachments) {
                    if (access.Texture == attachment.Texture) {
                        return false;
                    }
                }
            }
            return true;
        }

        static bool HasNonAttachmentWrites(CompiledRenderGraphPass pass)
        {
            for (var index = 0; index < pass.Buffers.Count; index++) {
                if (Writes(pass.Buffers[index].Access)) {
                    return true;
                }
            }
            for (var index = 0; index < pass.Textures.Count; index++) {
                var access = pass.Textures[index];
                if (Writes(access.Access) &&
                    access.Usage != RenderGraphTextureUsage.RenderAttachment) {
                    return true;
                }
            }
            return false;
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
            for (var previousIndex = 0; previousIndex < previous.Buffers.Count; previousIndex++) {
                var previousAccess = previous.Buffers[previousIndex];
                for (var nextIndex = 0; nextIndex < next.Buffers.Count; nextIndex++) {
                    var nextAccess = next.Buffers[nextIndex];
                    if (previousAccess.Buffer == nextAccess.Buffer &&
                        (Writes(previousAccess.Access) || Writes(nextAccess.Access))) {
                        return true;
                    }
                }
            }

            for (var previousIndex = 0; previousIndex < previous.Textures.Count; previousIndex++) {
                var previousAccess = previous.Textures[previousIndex];
                for (var nextIndex = 0; nextIndex < next.Textures.Count; nextIndex++) {
                    var nextAccess = next.Textures[nextIndex];
                    if (previousAccess.Texture != nextAccess.Texture ||
                        (!Writes(previousAccess.Access) && !Writes(nextAccess.Access))) {
                        continue;
                    }

                    var sameAttachment =
                        previousAccess.Usage == RenderGraphTextureUsage.RenderAttachment &&
                        nextAccess.Usage == RenderGraphTextureUsage.RenderAttachment &&
                        previousAccess.Subresources == nextAccess.Subresources &&
                        attachments.Contains((previousAccess.Texture, previousAccess.Subresources));
                    if (!sameAttachment && RenderGraphValidation.Overlaps(
                        textures[previousAccess.Texture.Index].Descriptor.Format,
                        previousAccess.Subresources, nextAccess.Subresources)) {
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
