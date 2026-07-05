using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Imaging;

/// <summary>
/// Coalesces a boolean grid of changed tiles into a small set of covering
/// rectangles in pixel coordinates. Horizontal runs per tile-row are merged, then
/// vertically adjacent runs with an identical x-span are stacked. Edge tiles are
/// clamped to the image bounds.
/// </summary>
public static class RectMerge
{
    public static IReadOnlyList<PixelRect> Merge(
        bool[] changed, int cols, int rows, int tileSize, int imageWidth, int imageHeight)
    {
        var open = new List<PixelRect>();   // rectangles still growing downward
        var result = new List<PixelRect>();

        for (int ty = 0; ty < rows; ty++)
        {
            var rowRuns = new List<PixelRect>();
            int tx = 0;
            while (tx < cols)
            {
                if (!changed[ty * cols + tx]) { tx++; continue; }
                int startTx = tx;
                while (tx < cols && changed[ty * cols + tx]) tx++;

                int x0 = startTx * tileSize;
                int x1 = Math.Min(tx * tileSize, imageWidth);
                int y0 = ty * tileSize;
                int y1 = Math.Min((ty + 1) * tileSize, imageHeight);
                rowRuns.Add(new PixelRect(x0, y0, x1 - x0, y1 - y0));
            }

            // Try to extend open rectangles whose x-span exactly matches a run.
            var stillOpen = new List<PixelRect>();
            foreach (var run in rowRuns)
            {
                int matchIndex = open.FindIndex(o => o.X == run.X && o.Width == run.Width && o.Bottom == run.Y);
                if (matchIndex >= 0)
                {
                    var o = open[matchIndex];
                    open.RemoveAt(matchIndex);
                    stillOpen.Add(o with { Height = o.Height + run.Height });
                }
                else
                {
                    stillOpen.Add(run);
                }
            }

            // Any open rectangle not extended this row is finished.
            result.AddRange(open);
            open = stillOpen;
        }

        result.AddRange(open);
        return result;
    }
}
