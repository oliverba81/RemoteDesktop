using RemoteDesktop.Shared.Imaging;
using RemoteDesktop.Shared.Protocol;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class ImagingTests
{
    private static byte[] MakeFrame(int w, int h, int stride, byte fill)
    {
        var buf = new byte[stride * h];
        Array.Fill(buf, fill);
        return buf;
    }

    [Fact]
    public void FirstFrame_ReportsWholeImage()
    {
        int w = 130, h = 130, stride = w * 4;
        var hasher = new TileHasher(w, h, stride);
        var rects = hasher.Diff(MakeFrame(w, h, stride, 0));
        int covered = rects.Sum(r => r.Area);
        Assert.Equal(w * h, covered);
    }

    [Fact]
    public void UnchangedFrame_ReportsNothing()
    {
        int w = 128, h = 128, stride = w * 4;
        var hasher = new TileHasher(w, h, stride);
        var frame = MakeFrame(w, h, stride, 7);
        hasher.Diff(frame);
        var second = hasher.Diff(frame);
        Assert.Empty(second);
    }

    [Fact]
    public void SinglePixelChange_ReportsOnlyThatTile()
    {
        int w = 256, h = 256, stride = w * 4;
        var hasher = new TileHasher(w, h, stride, tileSize: 64);
        var frame = MakeFrame(w, h, stride, 0);
        hasher.Diff(frame);

        // Change one pixel at (100,100) -> tile (1,1) covering x[64..128), y[64..128).
        int idx = 100 * stride + 100 * 4;
        frame[idx] = 255;
        var rects = hasher.Diff(frame);

        Assert.Single(rects);
        var r = rects[0];
        Assert.True(r.X <= 100 && r.Right > 100 && r.Y <= 100 && r.Bottom > 100);
        Assert.True(r.Area <= 64 * 64);
    }

    [Fact]
    public void RectMerge_StacksVerticallyAdjacentRuns()
    {
        // 2x2 grid, all changed -> should merge into a single rectangle.
        var changed = new[] { true, true, true, true };
        var rects = RectMerge.Merge(changed, cols: 2, rows: 2, tileSize: 64, imageWidth: 128, imageHeight: 128);
        Assert.Single(rects);
        Assert.Equal(new PixelRect(0, 0, 128, 128), rects[0]);
    }

    [Fact]
    public void AspectFit_Letterbox_MapsCenterAndClampsBorders()
    {
        var source = new PixelSize(1000, 1000);
        var dest = new PixelSize(400, 200); // wider than tall -> pillarbox to 200x200
        var content = AspectFit.GetDestContentRect(source, dest);
        Assert.Equal(200, content.Width);
        Assert.Equal(200, content.Height);
        Assert.Equal(100, content.X); // centered horizontally

        // A click in the black border returns null.
        Assert.Null(AspectFit.ControlToSource(10, 100, source, dest));
        // A click at the content center maps near source center.
        var mapped = AspectFit.ControlToSource(200, 100, source, dest);
        Assert.NotNull(mapped);
        Assert.InRange(mapped!.Value.X, 490, 510);
    }

    [Fact]
    public void Normalized_FarEdge_ReachesMax()
    {
        var source = new PixelSize(1920, 1080);
        var (nx, _) = AspectFit.SourceToNormalized(1919, 0, source);
        Assert.Equal(65535, nx);
    }
}
