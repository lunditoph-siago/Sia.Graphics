namespace Sia.Graphics.UI;

public readonly record struct ProgressBar(float Value, float Min = 0f, float Max = 1f)
{
    public float Fraction {
        get {
            if (!float.IsFinite(Value) || !float.IsFinite(Min) || !float.IsFinite(Max))
                return 0f;

            var extent = Max - Min;
            if (extent == 0f)
                return Value >= Max ? 1f : 0f;

            return System.Math.Clamp((Value - Min) / extent, 0f, 1f);
        }
    }
}
