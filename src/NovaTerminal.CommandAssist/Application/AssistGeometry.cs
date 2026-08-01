namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// UI-toolkit-agnostic 2D point, mirroring <c>Avalonia.Point</c>.
/// </summary>
public readonly record struct AssistPoint(double X, double Y);

/// <summary>
/// UI-toolkit-agnostic size, mirroring <c>Avalonia.Size</c>.
/// </summary>
public readonly record struct AssistSize(double Width, double Height);

/// <summary>
/// UI-toolkit-agnostic rectangle, mirroring <c>Avalonia.Rect</c>'s member semantics
/// (<see cref="Left"/> == <see cref="X"/>, <see cref="Right"/> == <c>X + Width</c>, and so on)
/// so anchor math ported off Avalonia stays arithmetically identical.
/// </summary>
/// <remarks>
/// Command Assist computes overlay placement in a plain assembly that must not reference
/// Avalonia; the App reads the resulting scalars back into <c>Thickness</c>/size properties.
/// </remarks>
public readonly record struct AssistRect(double X, double Y, double Width, double Height)
{
    public AssistRect(AssistPoint position, AssistSize size)
        : this(position.X, position.Y, size.Width, size.Height)
    {
    }

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public AssistPoint Position => new(X, Y);

    public AssistSize Size => new(Width, Height);
}
