namespace Sia.Graphics.Text;

public sealed class DynamicTextureAtlasBuilder(int width, int height)
{
    private int _cursorX;
    private int _cursorY;
    private int _shelfHeight;

    public int Width { get; } = width;
    public int Height { get; } = height;

    public bool TryAllocate(int glyphWidth, int glyphHeight, out int x, out int y)
    {
        if (glyphWidth > Width || glyphHeight > Height) {
            x = 0;
            y = 0;
            return false;
        }

        if (_cursorX + glyphWidth > Width) {
            _cursorX = 0;
            _cursorY += _shelfHeight;
            _shelfHeight = 0;
        }

        if (_cursorY + glyphHeight > Height) {
            x = 0;
            y = 0;
            return false;
        }

        x = _cursorX;
        y = _cursorY;
        _cursorX += glyphWidth;
        _shelfHeight = System.Math.Max(_shelfHeight, glyphHeight);
        return true;
    }
}
