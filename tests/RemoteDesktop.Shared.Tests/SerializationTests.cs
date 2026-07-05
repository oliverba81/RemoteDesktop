using RemoteDesktop.Shared.Protocol;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class SerializationTests
{
    [Fact]
    public void PayloadWriter_UsesLittleEndian_GoldenBytes()
    {
        var w = new PayloadWriter();
        w.WriteUInt32(0x01020304);
        byte[] bytes = w.ToArray();
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, bytes);
    }

    [Fact]
    public void Reader_Underrun_Throws()
    {
        static void ReadTooMuch()
        {
            var r = new PayloadReader(new byte[] { 0x01, 0x02 });
            r.ReadInt32();
        }
        Assert.Throws<ProtocolException>(ReadTooMuch);
    }

    [Fact]
    public void ScreenFrame_RoundTrips()
    {
        var original = new ScreenFramePayload(42, new[]
        {
            new ScreenTile(new PixelRect(10, 20, 64, 48), new byte[] { 1, 2, 3 }),
            new ScreenTile(new PixelRect(0, 0, 1920, 1080), new byte[] { 9, 8, 7, 6 }),
        });
        var parsed = ScreenFramePayload.Parse(original.Serialize());
        Assert.Equal(42u, parsed.FrameId);
        Assert.Equal(2, parsed.Tiles.Count);
        Assert.Equal(new PixelRect(10, 20, 64, 48), parsed.Tiles[0].Rect);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, parsed.Tiles[1].Jpeg);
    }

    [Fact]
    public void InputKeyboard_RoundTrips()
    {
        var original = new InputKeyboardPayload(IsUnicode: true, Value: 0x00E4, IsDown: true, IsExtended: false);
        var parsed = InputKeyboardPayload.Parse(original.Serialize());
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Hello_RoundTrips()
    {
        var original = new HelloPayload(1, true, new byte[] { 1, 2 }, new byte[] { 3, 4 },
            new byte[] { 5, 6, 7, 8 }, 2560, 1440);
        var parsed = HelloPayload.Parse(original.Serialize());
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.IsHost, parsed.IsHost);
        Assert.Equal(original.EphemeralPublicKey, parsed.EphemeralPublicKey);
        Assert.Equal(original.Salt, parsed.Salt);
        Assert.Equal(original.NoncePrefix, parsed.NoncePrefix);
        Assert.Equal(original.ScreenWidth, parsed.ScreenWidth);
        Assert.Equal(original.ScreenHeight, parsed.ScreenHeight);
    }

    [Fact]
    public void UnicodeString_RoundTrips_WithNonBmp()
    {
        var original = new ClipboardTextPayload("Hällo 🌍\r\nZeile2");
        var parsed = ClipboardTextPayload.Parse(original.Serialize());
        Assert.Equal(original.Text, parsed.Text);
    }
}
