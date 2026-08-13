using Sia;

namespace Sia.Graphics.UI;

public static class UiSystemChainExtensions
{
    public static SystemChain AddReactiveUi(this SystemChain chain) =>
        chain
            .Add<UiHierarchySystem>()
            .Add<UiLayoutSystem>()
            .Add<UiStackSystem>();
}
