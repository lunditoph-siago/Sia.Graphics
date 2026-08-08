using Sia;

namespace Sia.Graphics.UI;

public readonly record struct PointerEnter : IEvent;

public readonly record struct PointerLeave : IEvent;

public readonly record struct PointerPress(int Button) : IEvent;

public readonly record struct PointerRelease(int Button) : IEvent;

public readonly record struct PointerClick(int Button) : IEvent;

public readonly record struct PointerDragStart(Point Position) : IEvent;

public readonly record struct PointerDrag(Point Position, Point Delta) : IEvent;

public readonly record struct PointerDragEnd(Point Position) : IEvent;

public readonly record struct PointerCancel : IEvent;
