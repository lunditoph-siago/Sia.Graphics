namespace Sia.Graphics.UI;

public record struct Node
{
    public Display Display = Display.Flex;
    public BoxSizing BoxSizing = BoxSizing.BorderBox;
    public PositionType PositionType = PositionType.Relative;
    public Overflow Overflow = Overflow.Visible;
    public Val OverflowClipMargin = Val.Zero;

    public Val Left = Val.Auto;
    public Val Right = Val.Auto;
    public Val Top = Val.Auto;
    public Val Bottom = Val.Auto;

    public Val Width = Val.Auto;
    public Val Height = Val.Auto;
    public Val MinWidth = Val.Auto;
    public Val MinHeight = Val.Auto;
    public Val MaxWidth = Val.Auto;
    public Val MaxHeight = Val.Auto;
    public float? AspectRatio = null;

    public UiRect Margin = UiRect.Zero;
    public UiRect Padding = UiRect.Zero;
    public UiRect Border = UiRect.Zero;
    public BorderRadius BorderRadius = BorderRadius.Zero;

    public AlignItems AlignItems = AlignItems.Default;
    public AlignItems AlignSelf = AlignItems.Default;
    public AlignContent AlignContent = AlignContent.Default;
    public AlignItems JustifyItems = AlignItems.Default;
    public AlignItems JustifySelf = AlignItems.Default;
    public AlignContent JustifyContent = AlignContent.Default;

    public Val RowGap = Val.Zero;
    public Val ColumnGap = Val.Zero;

    // Flexbox container/item properties.
    public FlexDirection FlexDirection = FlexDirection.Row;
    public FlexWrap FlexWrap = FlexWrap.NoWrap;
    public float FlexGrow = 0f;
    public float FlexShrink = 1f;
    public Val FlexBasis = Val.Auto;

    // Grid container properties.
    public List<GridTrackSizingFunction> GridTemplateColumns = [];
    public List<GridTrackSizingFunction> GridTemplateRows = [];
    public List<GridTrackSizingFunction> GridAutoColumns = [];
    public List<GridTrackSizingFunction> GridAutoRows = [];
    public GridAutoFlow GridAutoFlow = GridAutoFlow.Row;

    // Grid item placement.
    public GridPlacement GridColumn = GridPlacement.Auto;
    public GridPlacement GridRow = GridPlacement.Auto;

    public Node() { }
}

public readonly record struct BorderRadius(Val TopLeft, Val TopRight, Val BottomRight, Val BottomLeft)
{
    public static readonly BorderRadius Zero = All(Val.Zero);

    public static BorderRadius All(Val value) => new(value, value, value, value);
}
