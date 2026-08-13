using Sia.Reactive;
using SiaReactive = Sia.Reactive.Reactive;

namespace Sia.Graphics.UI;

public static class ReactiveUiNode
{
    public static ReactiveNode<EntityTerm<
        HList<Node, HList<UiNodeKey, HList<UiParentKey,
        HList<UiSiblingOrder, TComponents>>>>,
        UnitTerm>> Create<TComponents>(
        string key,
        string? parentKey,
        int siblingOrder,
        in Node node,
        in TComponents components)
        where TComponents : struct, IHList =>
        SiaReactive.Entity(HList.Cons(
            node,
            HList.Cons(
                new UiNodeKey(key),
                HList.Cons(
                    new UiParentKey(parentKey),
                    HList.Cons(new UiSiblingOrder(siblingOrder), components)))));

    public static ReactiveNode<EntityTerm<
        HList<Node, HList<UiNodeKey, HList<UiParentKey,
        HList<UiSiblingOrder, TComponents>>>>,
        TChildren>> Create<TComponents, TChildren>(
        string key,
        string? parentKey,
        int siblingOrder,
        in Node node,
        in TComponents components,
        in ReactiveNode<TChildren> reactions)
        where TComponents : struct, IHList
        where TChildren : struct, ITerm<TChildren> =>
        SiaReactive.Entity(HList.Cons(
            node,
            HList.Cons(
                new UiNodeKey(key),
                HList.Cons(
                    new UiParentKey(parentKey),
                    HList.Cons(new UiSiblingOrder(siblingOrder), components)))),
            reactions);
}
