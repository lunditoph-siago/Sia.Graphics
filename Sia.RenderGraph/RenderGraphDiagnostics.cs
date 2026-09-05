using System.Text;

namespace Sia.RenderGraph;

public static class RenderGraphDiagnostics
{
    public static string Describe(CompiledRenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var result = new StringBuilder();
        result.Append("passes: ")
            .Append(graph.Passes.Count)
            .Append(" live, ")
            .Append(graph.CulledPassCount)
            .AppendLine(" culled");

        foreach (var status in graph.PassStatuses) {
            result.Append(status.IsLive ? "[live] " : "[culled] ")
                .Append(status.Name)
                .Append(" (")
                .Append(status.Kind)
                .AppendLine(")");
        }

        result.AppendLine("buffers:");
        foreach (var buffer in graph.Buffers) {
            AppendResource(
                result,
                buffer.Descriptor.Name,
                buffer.Lifetime,
                buffer.IsImported,
                buffer.IsExported);
        }

        result.AppendLine("textures:");
        foreach (var texture in graph.Textures) {
            AppendResource(
                result,
                texture.Descriptor.Name,
                texture.Lifetime,
                texture.IsImported,
                texture.IsExported);
        }
        return result.ToString();
    }

    public static string ToDot(CompiledRenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var result = new StringBuilder();
        result.AppendLine("digraph RenderGraph {");
        result.AppendLine("  rankdir=LR;");
        foreach (var status in graph.PassStatuses) {
            result.Append("  p")
                .Append(status.Handle.Index)
                .Append(" [shape=box,label=\"")
                .Append(Escape(status.Name))
                .Append("\",style=")
                .Append(status.IsLive ? "solid" : "dashed")
                .AppendLine("]; ");
        }
        foreach (var pass in graph.Passes) {
            foreach (var dependency in pass.Dependencies) {
                result.Append("  p")
                    .Append(dependency.Index)
                    .Append(" -> p")
                    .Append(pass.Handle.Index)
                    .AppendLine(";");
            }
        }
        result.AppendLine("}");
        return result.ToString();
    }

    private static void AppendResource(
        StringBuilder result,
        string name,
        RenderGraphResourceLifetime lifetime,
        bool imported,
        bool exported)
    {
        result.Append("  ")
            .Append(name)
            .Append(": ")
            .Append(lifetime.IsUsed
                ? $"[{lifetime.FirstUse}, {lifetime.LastUse}]"
                : "unused");
        if (imported) {
            result.Append(" imported");
        }
        if (exported) {
            result.Append(" exported");
        }
        result.AppendLine();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
