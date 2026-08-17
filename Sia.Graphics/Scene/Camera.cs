namespace Sia.Graphics.Scene;

public record struct Camera(float VerticalFovRadians, float Near, float Far)
{
    public static Camera Default => new(VerticalFovRadians: MathF.PI / 3.0f, Near: 0.1f, Far: 1000.0f);
}
