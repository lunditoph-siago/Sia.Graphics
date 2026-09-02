using Sia;

namespace Sia.Graphics.UI;

public static class UiControlSystemChainExtensions
{
    public static SystemChain AddUiControls(this SystemChain chain) =>
        chain
            .Add<ButtonSystem>()
            .Add<CheckboxSystem>()
            .Add<RadioButtonSystem>()
            .Add<SliderSystem>()
            .Add<TabSystem>()
            .Add<DropdownSystem>()
            .Add<TextInputSystem>()
            .Add<ScrollViewSystem>();
}
