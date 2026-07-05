using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Imaging;

/// <summary>
/// Maps between a remote screen (source) and a scaled, letterboxed viewer control
/// (destination) preserving aspect ratio. Pure math, unit-testable — the fiddly
/// rounding here is where off-by-one input-misplacement and stale-pixel seams come
/// from, so it lives in Shared with tests.
/// </summary>
public static class AspectFit
{
    /// <summary>The content rectangle (inside the letterbox) where the remote image is drawn.</summary>
    public static PixelRect GetDestContentRect(PixelSize source, PixelSize dest)
    {
        if (source.Width <= 0 || source.Height <= 0 || dest.Width <= 0 || dest.Height <= 0)
            return new PixelRect(0, 0, 0, 0);

        double scale = Math.Min((double)dest.Width / source.Width, (double)dest.Height / source.Height);
        int w = (int)Math.Round(source.Width * scale);
        int h = (int)Math.Round(source.Height * scale);
        int x = (dest.Width - w) / 2;
        int y = (dest.Height - h) / 2;
        return new PixelRect(x, y, w, h);
    }

    /// <summary>
    /// Maps a point in control coordinates to a remote pixel, or null if it falls
    /// in the letterbox border. Used to convert mouse positions.
    /// </summary>
    public static (int X, int Y)? ControlToSource(int cx, int cy, PixelSize source, PixelSize dest)
    {
        PixelRect content = GetDestContentRect(source, dest);
        if (content.IsEmpty) return null;
        if (cx < content.X || cx >= content.Right || cy < content.Y || cy >= content.Bottom)
            return null;

        double fx = (double)(cx - content.X) / content.Width;
        double fy = (double)(cy - content.Y) / content.Height;
        int sx = Math.Clamp((int)(fx * source.Width), 0, source.Width - 1);
        int sy = Math.Clamp((int)(fy * source.Height), 0, source.Height - 1);
        return (sx, sy);
    }

    /// <summary>
    /// Normalizes a remote pixel to the 0..65535 absolute-coordinate space
    /// SendInput expects. Uses (W-1)/(H-1) so the far pixel edge is reachable.
    /// </summary>
    public static (ushort X, ushort Y) SourceToNormalized(int sx, int sy, PixelSize source)
    {
        int w = Math.Max(1, source.Width - 1);
        int h = Math.Max(1, source.Height - 1);
        ushort nx = (ushort)Math.Clamp((int)Math.Round(sx * 65535.0 / w), 0, 65535);
        ushort ny = (ushort)Math.Clamp((int)Math.Round(sy * 65535.0 / h), 0, 65535);
        return (nx, ny);
    }

    /// <summary>
    /// Maps a changed remote rectangle to the control rectangle to invalidate.
    /// Origin floored, extent ceiled, then inflated by 1px so no stale seam remains
    /// at the moving edge of a window.
    /// </summary>
    public static PixelRect SourceRectToControl(PixelRect src, PixelSize source, PixelSize dest)
    {
        PixelRect content = GetDestContentRect(source, dest);
        if (content.IsEmpty) return new PixelRect(0, 0, 0, 0);

        double sxScale = (double)content.Width / source.Width;
        double syScale = (double)content.Height / source.Height;

        int x0 = content.X + (int)Math.Floor(src.X * sxScale);
        int y0 = content.Y + (int)Math.Floor(src.Y * syScale);
        int x1 = content.X + (int)Math.Ceiling(src.Right * sxScale);
        int y1 = content.Y + (int)Math.Ceiling(src.Bottom * syScale);

        x0 = Math.Max(content.X, x0 - 1);
        y0 = Math.Max(content.Y, y0 - 1);
        x1 = Math.Min(content.Right, x1 + 1);
        y1 = Math.Min(content.Bottom, y1 + 1);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}
