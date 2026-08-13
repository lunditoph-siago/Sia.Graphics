namespace Sia.Graphics.UI;

public readonly record struct UiNodeIdentity(
    string Key,
    string? ParentKey,
    int SiblingOrder);
