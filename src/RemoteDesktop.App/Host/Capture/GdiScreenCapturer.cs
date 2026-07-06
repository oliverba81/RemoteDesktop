using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteDesktop.Host.Native;
using RemoteDesktop.Shared.Imaging;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Host.Capture;

/// <summary>
/// Captures the primary monitor with GDI BitBlt, diffs it against the previous
/// frame with <see cref="TileHasher"/>, and JPEG-encodes only the changed
/// rectangles. Buffers, the capture bitmap and the JPEG encoder are reused across
/// frames to keep GC pressure down.
///
/// v1 limitation: GDI CopyFromScreen does not capture the hardware cursor (sent
/// separately) and returns black frames for DRM/overlay surfaces. A DXGI Desktop
/// Duplication implementation of the same shape is the planned v2 upgrade.
/// </summary>
public sealed class GdiScreenCapturer : IDisposable
{
    private readonly Rectangle _bounds;
    private readonly int _width;
    private readonly int _height;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly ImageCodecInfo _jpegCodec;
    private byte[] _buffer = Array.Empty<byte>();
    private TileHasher? _hasher;
    private uint _frameId;

    public GdiScreenCapturer() : this(PrimaryBounds())
    {
    }

    /// <summary>Captures the given desktop rectangle (one monitor's bounds).</summary>
    public GdiScreenCapturer(Rectangle bounds)
    {
        _bounds = bounds;
        _width = Math.Max(1, bounds.Width);
        _height = Math.Max(1, bounds.Height);
        _bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    }

    public static Rectangle PrimaryBounds() => new(
        0, 0,
        Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN)),
        Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)));

    public PixelSize Size => new(_width, _height);

    /// <summary>
    /// Captures the screen and returns the changed rectangles as JPEGs, or an empty
    /// list if nothing changed. The first call returns the whole screen.
    /// </summary>
    public ScreenFramePayload? CaptureChanged(int jpegQuality)
    {
        _graphics.CopyFromScreen(_bounds.X, _bounds.Y, 0, 0, new Size(_width, _height), CopyPixelOperation.SourceCopy);

        var rect = new Rectangle(0, 0, _width, _height);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bufLen = Math.Abs(data.Stride) * _height;
            if (_buffer.Length < bufLen) _buffer = new byte[bufLen];
            Marshal.Copy(data.Scan0, _buffer, 0, bufLen);
            _hasher ??= new TileHasher(_width, _height, Math.Abs(data.Stride), bytesPerPixel: 4);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        IReadOnlyList<PixelRect> changed = _hasher!.Diff(_buffer);
        if (changed.Count == 0) return null;

        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)jpegQuality);

        var tiles = new List<ScreenTile>(changed.Count);
        foreach (PixelRect r in changed)
        {
            var cropRect = new Rectangle(r.X, r.Y, r.Width, r.Height);
            using var tile = _bitmap.Clone(cropRect, PixelFormat.Format24bppRgb);
            using var ms = new MemoryStream();
            tile.Save(ms, _jpegCodec, encoderParams);
            tiles.Add(new ScreenTile(r, ms.ToArray()));
        }

        return new ScreenFramePayload(unchecked(_frameId++), tiles);
    }

    /// <summary>Current cursor position and visibility (shape sent as empty for v1;
    /// the viewer renders a default pointer).</summary>
    public CursorUpdatePayload GetCursor()
    {
        var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref ci))
            return new CursorUpdatePayload(0, 0, false, Array.Empty<byte>());
        bool visible = (ci.flags & NativeMethods.CURSOR_SHOWING) != 0
            && _bounds.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y);
        return new CursorUpdatePayload(
            ci.ptScreenPos.X - _bounds.X, ci.ptScreenPos.Y - _bounds.Y, visible, Array.Empty<byte>());
    }

    public static int QualityToJpeg(QualityLevel level) => level switch
    {
        QualityLevel.Low => 35,
        QualityLevel.Medium => 55,
        _ => 78,
    };

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}
