using System.IO.Hashing;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Imaging;

/// <summary>
/// Detects changed screen regions between frames by hashing a fixed tile grid.
/// Operates purely on a raw pixel buffer (BGRA/BGR stride bytes) so it is
/// GDI-free and unit-testable on Linux. Edge tiles that do not divide evenly are
/// clamped to the image bounds.
/// </summary>
public sealed class TileHasher
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _bytesPerPixel;
    private readonly int _tileSize;
    private readonly int _cols;
    private readonly int _rows;
    private readonly ulong[] _prev;
    private bool _hasPrev;

    public TileHasher(int width, int height, int stride, int bytesPerPixel = 4, int tileSize = 64)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Invalid dimensions.");
        if (stride < width * bytesPerPixel) throw new ArgumentException("Stride too small.");
        _width = width;
        _height = height;
        _stride = stride;
        _bytesPerPixel = bytesPerPixel;
        _tileSize = tileSize;
        _cols = (width + tileSize - 1) / tileSize;
        _rows = (height + tileSize - 1) / tileSize;
        _prev = new ulong[_cols * _rows];
    }

    public int Columns => _cols;
    public int Rows => _rows;

    /// <summary>
    /// Returns the merged changed rectangles for <paramref name="frame"/> and
    /// updates the internal hash state. The first call reports the whole frame.
    /// </summary>
    public IReadOnlyList<PixelRect> Diff(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < (_height - 1) * _stride + _width * _bytesPerPixel)
            throw new ArgumentException("Frame buffer too small for declared dimensions.");

        var changed = new bool[_cols * _rows];
        bool first = !_hasPrev;

        for (int ty = 0; ty < _rows; ty++)
        {
            int y0 = ty * _tileSize;
            int th = Math.Min(_tileSize, _height - y0);
            for (int tx = 0; tx < _cols; tx++)
            {
                int x0 = tx * _tileSize;
                int tw = Math.Min(_tileSize, _width - x0);
                ulong hash = HashTile(frame, x0, y0, tw, th);
                int idx = ty * _cols + tx;
                if (first || hash != _prev[idx])
                {
                    changed[idx] = true;
                    _prev[idx] = hash;
                }
            }
        }

        _hasPrev = true;
        return RectMerge.Merge(changed, _cols, _rows, _tileSize, _width, _height);
    }

    private ulong HashTile(ReadOnlySpan<byte> frame, int x0, int y0, int tw, int th)
    {
        var hasher = new XxHash64();
        int rowBytes = tw * _bytesPerPixel;
        for (int y = 0; y < th; y++)
        {
            int offset = (y0 + y) * _stride + x0 * _bytesPerPixel;
            hasher.Append(frame.Slice(offset, rowBytes));
        }
        return hasher.GetCurrentHashAsUInt64();
    }
}
