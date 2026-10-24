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
                group = Combine(group, next);
                continue;
            }

            groups.Add(new RenderGraphPassGroup(groupStart, index - groupStart));
            groupStart = index;
            group = next;
        }

        groups.Add(new RenderGraphPassGroup(groupStart, passes.Length - groupStart));
        return [.. groups];
    }

    private static FusionInfo Describe(CompiledRenderGraphPass pass) => new(
        pass.Kind,
        GetRenderAttachmentWrites(pass),
        pass.Kind == RenderGraphPassKind.Render &&
        All(pass.Buffers, static access => !Writes(access.Access)) &&
        All(pass.Textures, static access =>
            !Writes(access.Access) || access.Usage == RenderGraphTextureUsage.RenderAttachment) &&
        !AnyPair(pass.Textures, pass.Textures, static (read, write) =>
            !Writes(read.Access) && Writes(write.Access) && read.Texture == write.Texture));

    // Matching attachment sets have already been established by CanMerge.
    private static FusionInfo Combine(in FusionInfo group, in FusionInfo next) =>
        group with { CanFuseByAttachments = group.CanFuseByAttachments && next.CanFuseByAttachments };

    private static bool CanMerge(
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

    private static bool HasFusionHazard(
        CompiledRenderGraphPass previous,
        CompiledRenderGraphPass next,
        RenderAttachmentSet attachments,
        CompiledRenderGraphTexture[] textures)
    {
        for (var i = 0; i < previous.Buffers.Count; i++) {
            var a = previous.Buffers[i];
            for (var j = 0; j < next.Buffers.Count; j++) {
                var b = next.Buffers[j];
                if (Conflicts(a, b)) {
                    return true;
                }
            }
        }
        for (var i = 0; i < previous.Textures.Count; i++) {
            var a = previous.Textures[i];
            for (var j = 0; j < next.Textures.Count; j++) {
                var b = next.Textures[j];
                if (Conflicts(a, b, attachments, textures)) {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool Conflicts(
        in RenderGraphBufferAccess a,
        in RenderGraphBufferAccess b) =>
        a.Buffer == b.Buffer && (Writes(a.Access) || Writes(b.Access));

    private static bool Conflicts(
        in RenderGraphTextureAccess a,
        in RenderGraphTextureAccess b,
        RenderAttachmentSet attachments,
        CompiledRenderGraphTexture[] textures) =>
        a.Texture == b.Texture &&
        (Writes(a.Access) || Writes(b.Access)) &&
        !(a.Usage == RenderGraphTextureUsage.RenderAttachment &&
            b.Usage == RenderGraphTextureUsage.RenderAttachment &&
            a.Subresources == b.Subresources &&
            attachments.Contains((a.Texture, a.Subresources))) &&
        RenderGraphValidation.Overlaps(
            textures[a.Texture.Index].Descriptor.Format, a.Subresources, b.Subresources);

    private static bool All<T>(IReadOnlyList<T> values, Func<T, bool> predicate)
    {
        for (var i = 0; i < values.Count; i++) {
            if (!predicate(values[i])) { return false; }
        }
        return true;
    }

    private static bool AnyPair<T>(IReadOnlyList<T> first, IReadOnlyList<T> second, Func<T, T, bool> predicate)
    {
        for (var i = 0; i < first.Count; i++) {
            for (var j = 0; j < second.Count; j++) {
                if (predicate(first[i], second[j])) { return true; }
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

    private readonly record struct FusionInfo(
        RenderGraphPassKind Kind,
        RenderAttachmentSet Attachments,
        bool CanFuseByAttachments);

    private sealed class RenderAttachmentSet
        : HashSet<(RenderGraphTextureHandle Texture, RenderGraphTextureSubresourceRange Subresources)>;
}
