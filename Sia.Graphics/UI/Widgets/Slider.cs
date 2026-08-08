using Sia;

namespace Sia.Graphics.UI;

public record struct SliderValue(float Value);

public record struct SliderRange(float Min, float Max);

public record struct SliderStep(float Step);

public readonly record struct SliderChanged(float Value) : IEvent;

public sealed class SliderSystem() : SystemBase(Matchers.Of<SliderValue, SliderRange>())
{
    public override void Initialize(World world)
    {
        world.Dispatcher.Listen<PointerPress>((Entity target, in PointerPress e) => {
            UpdateFromPointer(world, target);
            return false;
        });
        world.Dispatcher.Listen<PointerDrag>((Entity target, in PointerDrag e) => {
            UpdateFromPointer(world, target);
            return false;
        });
    }

    private static void UpdateFromPointer(World world, Entity target)
    {
        if (!target.IsValid || target.Contains<Disabled>())
            return;
        if (!target.Contains<SliderValue>() || !target.Contains<SliderRange>()
            || !target.Contains<ComputedNode>() || !target.Contains<UiGlobalTransform>())
            return;

        var pointer = world.AcquireAddon<UiPointerState>();
        var computed = target.Get<ComputedNode>();
        var transform = target.Get<UiGlobalTransform>();
        var local = transform.InverseTransform(pointer.Position);

        var t = computed.Size.Width > 0f ? Math.Clamp(local.X / computed.Size.Width, 0f, 1f) : 0f;
        var range = target.Get<SliderRange>();
        var value = range.Min + t * (range.Max - range.Min);

        if (target.Contains<SliderStep>()) {
            var step = target.Get<SliderStep>().Step;
            if (float.IsFinite(step) && step > 0f)
                value = range.Min + MathF.Round((value - range.Min) / step) * step;
        }
        value = Math.Clamp(value, MathF.Min(range.Min, range.Max), MathF.Max(range.Min, range.Max));

        ref var sliderValue = ref target.Get<SliderValue>();
        if (!AreClose(sliderValue.Value, value)) {
            sliderValue.Value = value;
            world.Dispatcher.Send(target, new SliderChanged(value));
        }
    }

    private static bool AreClose(float a, float b) => MathF.Abs(a - b) < 0.0001f;
}
