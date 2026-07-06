using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteDesktop.Shared.Imaging;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Viewer;

/// <summary>
/// Renders the remote screen and captures local input. The composited backing
/// bitmap is written by a background decode thread and read by OnPaint, so every
/// access is guarded by <see cref="_bufferLock"/> (GDI+ is not thread-safe).
/// Invalidation is always marshalled to the UI thread via BeginInvoke.
/// </summary>
public sealed class RemoteScreenControl : Control
{
    private readonly object _bufferLock = new();
    private Bitmap? _buffer;
    private PixelSize _remoteSize;
    private int _cursorX, _cursorY;
    private bool _cursorVisible;

    public RemoteScreenControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.Black;
    }

    /// <summary>Raised for every local mouse action, already mapped to remote pixels.</summary>
    public event Action<InputMousePayload>? MouseInput;

    public PixelSize RemoteSize => _remoteSize;

    public void ResetRemote(PixelSize size)
    {
        lock (_bufferLock)
        {
            _buffer?.Dispose();
            _buffer = size.Width > 0 && size.Height > 0
                ? new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb)
                : null;
            _remoteSize = size;
        }
        SafeInvalidate(null);
    }

    /// <summary>Decodes and composites a frame's changed tiles. Runs on a decode
    /// thread, not the UI thread. Returns the union control-rect to invalidate.</summary>
    public void ApplyFrame(ScreenFramePayload frame)
    {
        Rectangle dirtyControl = Rectangle.Empty;
        lock (_bufferLock)
        {
            if (_buffer is null) return;
            using var g = Graphics.FromImage(_buffer);
            foreach (ScreenTile tile in frame.Tiles)
            {
                using var ms = new MemoryStream(tile.Jpeg);
                using var bmp = new Bitmap(ms);
                g.DrawImageUnscaled(bmp, tile.Rect.X, tile.Rect.Y);

                PixelRect cr = AspectFit.SourceRectToControl(tile.Rect, _remoteSize, CurrentSize());
                var rect = new Rectangle(cr.X, cr.Y, cr.Width, cr.Height);
                dirtyControl = dirtyControl.IsEmpty ? rect : Rectangle.Union(dirtyControl, rect);
            }
        }
        SafeInvalidate(dirtyControl);
    }

    public void ApplyCursor(CursorUpdatePayload cursor)
    {
        _cursorX = cursor.X;
        _cursorY = cursor.Y;
        _cursorVisible = cursor.Visible;
        SafeInvalidate(null);
    }

    private PixelSize CurrentSize() => new(Math.Max(1, Width), Math.Max(1, Height));

    private void SafeInvalidate(Rectangle? rect)
    {
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (rect is { } r && !r.IsEmpty) Invalidate(r);
                else Invalidate();
            });
        }
        catch (InvalidOperationException) { /* handle went away during teardown */ }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* whole surface is painted in OnPaint */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        PixelRect content = AspectFit.GetDestContentRect(_remoteSize, CurrentSize());
        e.Graphics.Clear(Color.Black);

        lock (_bufferLock)
        {
            if (_buffer is null || content.IsEmpty) return;
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(_buffer, new Rectangle(content.X, content.Y, content.Width, content.Height));
        }

        if (_cursorVisible)
        {
            var (nx, ny) = SourceToControl(_cursorX, _cursorY, content);
            DrawPointer(e.Graphics, nx, ny);
        }
    }

    private (int X, int Y) SourceToControl(int sx, int sy, PixelRect content)
    {
        if (_remoteSize.Width <= 0 || _remoteSize.Height <= 0) return (content.X, content.Y);
        int x = content.X + (int)((double)sx / _remoteSize.Width * content.Width);
        int y = content.Y + (int)((double)sy / _remoteSize.Height * content.Height);
        return (x, y);
    }

    private static void DrawPointer(Graphics g, int x, int y)
    {
        Point[] arrow =
        {
            new(x, y), new(x, y + 16), new(x + 4, y + 12),
            new(x + 7, y + 18), new(x + 9, y + 17), new(x + 6, y + 11), new(x + 11, y + 11),
        };
        g.FillPolygon(Brushes.White, arrow);
        g.DrawPolygon(Pens.Black, arrow);
    }

    // ---- Input capture ----

    protected override void OnMouseMove(MouseEventArgs e) => Emit(e, MouseButtonFlags.None, 0);
    protected override void OnMouseDown(MouseEventArgs e) => Emit(e, ButtonFlag(e.Button, down: true), 0);
    protected override void OnMouseUp(MouseEventArgs e) => Emit(e, ButtonFlag(e.Button, down: false), 0);
    protected override void OnMouseWheel(MouseEventArgs e) => Emit(e, MouseButtonFlags.Wheel, (short)e.Delta);

    private void Emit(MouseEventArgs e, MouseButtonFlags buttons, short wheel)
    {
        var mapped = AspectFit.ControlToSource(e.X, e.Y, _remoteSize, CurrentSize());
        if (mapped is null) return;
        var (nx, ny) = AspectFit.SourceToNormalized(mapped.Value.X, mapped.Value.Y, _remoteSize);
        MouseInput?.Invoke(new InputMousePayload(nx, ny, buttons, wheel));
    }

    private static MouseButtonFlags ButtonFlag(MouseButtons button, bool down) => button switch
    {
        MouseButtons.Left => down ? MouseButtonFlags.LeftDown : MouseButtonFlags.LeftUp,
        MouseButtons.Right => down ? MouseButtonFlags.RightDown : MouseButtonFlags.RightUp,
        MouseButtons.Middle => down ? MouseButtonFlags.MiddleDown : MouseButtonFlags.MiddleUp,
        _ => MouseButtonFlags.None,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_bufferLock) { _buffer?.Dispose(); _buffer = null; }
        }
        base.Dispose(disposing);
    }
}
