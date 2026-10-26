using Sia;
using Sia.Reactive;
using Sia.UI;
using SiaReactive = Sia.Reactive.Reactive;
using UiText = Sia.UI.Text;

namespace Sia.WebGPU.Example;

internal sealed partial class CornellBoxApp
{
    private static readonly string s_GalleryParagraph = string.Concat(Enumerable.Repeat(
        "AVATAR @ Graphics: shape, wrap and rasterize. 中文 · 0123456789. "
            + "Resize the window to reflow this paragraph; refresh to change its contents. ", 4))[..512];

    private bool _textGallery;
    private int _galleryCardCount = 128;
    private int _galleryRevision;

    private static ReactiveNode RenderTextGallery(in UiViewProps props, UiAssets assets)
    {
        var columns = props.Viewport.Width >= 1100 ? 3 : props.Viewport.Width >= 700 ? 2 : 1;
        var nodes = new List<ReactiveNode> {
            ReactiveUiNode.Create(
                "gallery-root", null, 0,
                new Node {
                    Width = Val.Percent(100f), Height = Val.Percent(100f),
                    Padding = UiRect.All(Val.Px(18f)),
                    FlexDirection = FlexDirection.Column, RowGap = Val.Px(12f)
                },
                HList.From(new UiRoot(props.Viewport), new BackgroundColor(s_PanelColor))),
            ReactiveUiNode.Create(
                "gallery-title", "gallery-root", 0,
                new Node { Width = Val.Percent(100f), FlexShrink = 0f },
                HList.From(
                    new UiText($"Text gallery · {props.GalleryCardCount:N0} cards · {props.GalleryCardCount * 512:N0} body characters"),
                    new TextStyle(assets.Latin, 24f, Color.White))),
            ReactiveUiNode.Create(
                "gallery-help", "gallery-root", 1,
                new Node { Width = Val.Percent(100f), FlexShrink = 0f },
                HList.From(
                    new UiText("Scroll to explore · Resize to reflow · Refresh to reshape · F2 scene / F3 load / R refresh"),
                    new TextStyle(assets.Latin, 14f, s_MutedTextColor))),
            ReactiveUiNode.Create(
                "gallery-toolbar", "gallery-root", 2,
                new Node {
                    Width = Val.Percent(100f), FlexShrink = 0f,
                    FlexWrap = FlexWrap.Wrap, ColumnGap = Val.Px(8f), RowGap = Val.Px(8f)
                }, EmptyHList.Default),
            ReactiveUiNode.Create(
                "gallery-scroll", "gallery-root", 3,
                new Node {
                    Width = Val.Percent(100f), FlexGrow = 1f,
                    MinHeight = Val.Zero, Overflow = Overflow.Scroll,
                    FlexDirection = FlexDirection.Column, RowGap = Val.Px(12f),
                    Padding = UiRect.All(Val.Px(2f))
                }, HList.From(new ScrollView(false)))
        };

        var app = props.App;
        foreach (var count in new[] { 32, 128, 512 }) {
            nodes.Add(Button(
                $"gallery-load-{count}", $"{count} cards", count,
                new(app, count), static (in Activate _, in GalleryCapture capture) =>
                    capture.App._galleryCardCount = capture.Value,
                count == props.GalleryCardCount ? s_AccentColor : s_ControlColor, assets));
        }
        nodes.Add(Button(
            "gallery-refresh", $"Refresh text · {props.GalleryRevision}", 1000,
            new(app, 0), static (in Activate _, in GalleryCapture capture) => capture.App._galleryRevision++,
            s_ControlColor, assets));
        nodes.Add(Button(
            "gallery-back", "Cornell Box / F2", 1001,
            new(app, 0), static (in Activate _, in GalleryCapture capture) =>
                capture.App.ActivateUiControl(ControlKind.TextGallery),
            s_ControlColor, assets));

        var cards = new (int Key, GalleryCardProps Value)[props.GalleryCardCount];
        for (var index = 0; index < cards.Length; index++) {
            var row = index / columns;
            if (index % columns == 0) {
                nodes.Add(ReactiveUiNode.Create(
                    $"gallery-row-{row}", "gallery-scroll", row,
                    new Node {
                        Width = Val.Percent(100f), FlexShrink = 0f,
                        ColumnGap = Val.Px(12f), AlignItems = AlignItems.Stretch
                    }, EmptyHList.Default));
            }
            cards[index] = (index, new(index, row, props.GalleryRevision, assets));
        }
        nodes.Add(SiaReactive.ForEach(
            static (in GalleryCardProps card) => SiaReactive.Component(RenderGalleryCard, card), cards));
        return SiaReactive.ForEach(
            static (in ReactiveNode node) => SiaReactive.Component(RenderNode, node),
            nodes.Select(static (node, index) => (index, node)).ToArray());

        static ReactiveNode RenderNode(in ReactiveNode node, ref Hooks hooks)
        {
            _ = hooks;
            return node;
        }

        static ReactiveNode Button(
            string key, string label, int order, GalleryCapture capture,
            ReactiveEventAction<Activate, GalleryCapture> action, Color color, UiAssets assets) =>
            ReactiveUiNode.Create(
                key, "gallery-toolbar", order,
                new Node {
                    Height = Val.Px(36f), Padding = UiRect.Axes(Val.Px(12f), Val.Px(8f)),
                    BorderRadius = BorderRadius.All(Val.Px(5f)), FlexShrink = 0f
                },
                HList.From(
                    new UiText(label), new TextStyle(assets.Latin, 14f, Color.White),
                    new BackgroundColor(color), new Sia.UI.Button()),
                SiaReactive.On<Activate, GalleryCapture>(capture, action));
    }

    private static ReactiveNode RenderGalleryCard(in GalleryCardProps props, ref Hooks hooks)
    {
        _ = hooks;
        var key = $"gallery-card-{props.Index}";
        var size = 14f + props.Index % 4 * 2f;
        return SiaReactive.Group(
            ReactiveUiNode.Create(
                key, $"gallery-row-{props.Row}", props.Index,
                new Node {
                    FlexBasis = Val.Zero, FlexGrow = 1f, MinWidth = Val.Zero,
                    Padding = UiRect.All(Val.Px(14f)), RowGap = Val.Px(10f),
                    FlexDirection = FlexDirection.Column,
                    Border = UiRect.All(Val.Px(1f)), BorderRadius = BorderRadius.All(Val.Px(8f))
                },
                HList.From(new BackgroundColor(s_ControlColor), new BorderColor(s_AccentColor))),
            ReactiveUiNode.Create(
                $"{key}-title", key, 0,
                new Node { Width = Val.Percent(100f), FlexShrink = 0f },
                HList.From(
                    new UiText($"{props.Index + 1:D3} / {size:0}px / Latin + 中文"),
                    new TextStyle(props.Assets.Latin, 14f, s_MutedTextColor) { FallbackFonts = props.Assets.Fallbacks })),
            ReactiveUiNode.Create(
                $"{key}-body", key, 1,
                new Node { Width = Val.Percent(100f), FlexShrink = 0f },
                HList.From(
                    new UiText($"Revision {props.Revision:D4} · card {props.Index + 1:D3}\n{s_GalleryParagraph}"),
                    new TextStyle(props.Assets.Latin, size, Color.White) { FallbackFonts = props.Assets.Fallbacks })));
    }

    private readonly record struct GalleryCardProps(int Index, int Row, int Revision, UiAssets Assets);
    private readonly record struct GalleryCapture(CornellBoxApp App, int Value);
}
