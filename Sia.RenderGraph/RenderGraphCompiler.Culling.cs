namespace Sia.RenderGraph;

public static partial class RenderGraphCompiler
{
    private static bool[] FindLivePasses(
        RenderGraphDefinition definition,
        HashSet<int>[] dependencies)
    {
        var livePasses = new bool[definition.PassCount];
        var pending = new Stack<int>();

        for (var passIndex = 0; passIndex < definition.PassCount; passIndex++) {
            var pass = definition.Passes[passIndex];
            if (pass.HasSideEffects ||
                WritesImportedResource(definition, pass) ||
                WritesExportedResource(definition, pass)) {
                pending.Push(passIndex);
            }
        }

        while (pending.TryPop(out var passIndex)) {
            if (livePasses[passIndex]) {
                continue;
            }

            livePasses[passIndex] = true;
            foreach (var dependency in dependencies[passIndex]) {
                pending.Push(dependency);
            }
        }

        return livePasses;
    }

    private static bool WritesImportedResource(
        RenderGraphDefinition definition,
        RenderGraphPassDefinition pass) =>
        pass.Buffers.Any(use =>
            Writes(use.Access) && definition.Buffers[use.BufferIndex].IsImported) ||
        pass.Textures.Any(use =>
            Writes(use.Access) && definition.Textures[use.TextureIndex].IsImported);

    private static bool WritesExportedResource(
        RenderGraphDefinition definition,
        RenderGraphPassDefinition pass) =>
        pass.Buffers.Any(use =>
            Writes(use.Access) && definition.Buffers[use.BufferIndex].IsExported) ||
        pass.Textures.Any(use =>
            Writes(use.Access) && definition.Textures[use.TextureIndex].IsExported);
}
