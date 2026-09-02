using Sia;

namespace Sia.Graphics.UI;

public sealed class UiFocusState : IAddon
{
    public Entity? Focused { get; internal set; }
}
