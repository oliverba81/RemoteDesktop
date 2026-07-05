namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// Plain, GDI-free rectangle in pixel coordinates. Deliberately not
/// System.Drawing.Rectangle so the Shared library stays usable by the
/// cross-platform relay and unit-testable on Linux.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int Area => Width * Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct PixelSize(int Width, int Height);
