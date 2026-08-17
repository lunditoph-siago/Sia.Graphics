using Sia;
using Sia.Graphics.UI;

namespace Sia.Graphics.Scene;

public sealed class Viewport : IAddon
{
    public Size Value { get; set; } = new(1, 1);
}
