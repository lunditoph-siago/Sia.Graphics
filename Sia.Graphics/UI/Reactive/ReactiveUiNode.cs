using Sia.Reactive;
using SiaReactive = Sia.Reactive.Reactive;

namespace Sia.Graphics.UI;

public static class ReactiveUiNode
{
    public static ReactiveNode<EntityTerm<
        HList<Node, HList<UiNodeIdentity, TComponents>>,
        UnitTerm>> Create<TComponents>(
        string key,
        string? parentKey,
        int siblingOrder,
        in Node node,
        in TComponents components)
        where TComponents : struct, IHList =>
        SiaReactive.Entity(CreateComponents(
            key, parentKey, siblingOrder, node, components));

    public static ReactiveNode<EntityTerm<
        HList<Node, HList<UiNodeIdentity, TComponents>>,
        TChildren>> Create<TComponents, TChildren>(
        string key,
        string? parentKey,
        int siblingOrder,
        in Node node,
        in TComponents components,
        in ReactiveNode<TChildren> reactions)
        where TComponents : struct, IHList
        where TChildren : struct, ITerm<TChildren> =>
        SiaReactive.Entity(
            CreateComponents(key, parentKey, siblingOrder, node, components),
            reactions);

    private static HList<Node, HList<UiNodeIdentity, TComponents>> CreateComponents<TComponents>(
        string key,
        string? parentKey,
        int siblingOrder,
        in Node node,
        in TComponents components)
        where TComponents : struct, IHList =>
        HList.Cons(
            node,
            HList.Cons(
                new UiNodeIdentity(key, parentKey, siblingOrder),
                components));
}
