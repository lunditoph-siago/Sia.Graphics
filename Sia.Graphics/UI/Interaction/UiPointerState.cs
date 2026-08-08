using Sia;

namespace Sia.Graphics.UI;

public sealed class UiPointerState : IAddon
{
    public Point Position { get; set; }
    public bool ButtonDown { get; set; }
}
