namespace Sia.Graphics.UI;

internal static class UiVertexSourceFactory
{
    public static IUiVertexSource Create(UiLegalizationPlan plan) =>
        plan.VertexDataMode switch {
            UiVertexDataMode.StorageBuffers => new StorageBufferVertexSource(),
            UiVertexDataMode.VertexBuffer => new VertexBufferVertexSource(),
            _ => throw new ArgumentOutOfRangeException(nameof(plan))
        };
}
