namespace RemoteDesktop.Shared.Protocol;

// Each payload knows how to serialize itself and exposes a static Parse. The
// 1-byte MessageType is written by the framing layer, not here.

public sealed record RegisterSessionPayload(ushort Version, string SessionId, byte[] OwnerTag)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteUInt16(Version);
        w.WriteString(SessionId);
        w.WriteBytes(OwnerTag);
        return w.ToArray();
    }

    public static RegisterSessionPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new RegisterSessionPayload(r.ReadUInt16(), r.ReadString(), r.ReadBytes());
    }
}

public sealed record JoinSessionPayload(ushort Version, string SessionId)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteUInt16(Version);
        w.WriteString(SessionId);
        return w.ToArray();
    }

    public static JoinSessionPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new JoinSessionPayload(r.ReadUInt16(), r.ReadString());
    }
}

public sealed record SessionErrorPayload(SessionErrorCode Code, string Message)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteByte((byte)Code);
        w.WriteString(Message);
        return w.ToArray();
    }

    public static SessionErrorPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new SessionErrorPayload((SessionErrorCode)r.ReadByte(), r.ReadString());
    }
}

/// <summary>Presence query. Each id carries an HMAC proof so the relay only reveals
/// online/offline to callers who already know the password (address book owner).</summary>
public sealed record QueryStatusPayload(IReadOnlyList<QueryStatusEntry> Entries)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteInt32(Entries.Count);
        foreach (var e in Entries)
        {
            w.WriteString(e.SessionId);
            w.WriteBytes(e.Proof);
        }
        return w.ToArray();
    }

    public static QueryStatusPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        int count = r.ReadInt32();
        var list = new List<QueryStatusEntry>(Math.Min(count, 64));
        for (int i = 0; i < count; i++)
            list.Add(new QueryStatusEntry(r.ReadString(), r.ReadBytes()));
        return new QueryStatusPayload(list);
    }
}

public readonly record struct QueryStatusEntry(string SessionId, byte[] Proof);

public sealed record StatusResponsePayload(IReadOnlyList<StatusResult> Results)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteInt32(Results.Count);
        foreach (var res in Results)
        {
            w.WriteString(res.SessionId);
            w.WriteBool(res.Online);
        }
        return w.ToArray();
    }

    public static StatusResponsePayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        int count = r.ReadInt32();
        var list = new List<StatusResult>(count);
        for (int i = 0; i < count; i++)
            list.Add(new StatusResult(r.ReadString(), r.ReadBool()));
        return new StatusResponsePayload(list);
    }
}

public readonly record struct StatusResult(string SessionId, bool Online);

/// <summary>Peer-to-peer handshake. Sent after SessionReady. Carries the sender's
/// ephemeral ECDH public key and, from the joiner, the password-auth commitment.</summary>
public sealed record HelloPayload(
    ushort Version,
    bool IsHost,
    byte[] EphemeralPublicKey,
    byte[] Salt,
    byte[] NoncePrefix,
    int ScreenWidth,
    int ScreenHeight)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteUInt16(Version);
        w.WriteBool(IsHost);
        w.WriteBytes(EphemeralPublicKey);
        w.WriteBytes(Salt);
        w.WriteBytes(NoncePrefix);
        w.WriteInt32(ScreenWidth);
        w.WriteInt32(ScreenHeight);
        return w.ToArray();
    }

    public static HelloPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new HelloPayload(
            r.ReadUInt16(), r.ReadBool(), r.ReadBytes(), r.ReadBytes(), r.ReadBytes(),
            r.ReadInt32(), r.ReadInt32());
    }
}

/// <summary>Password-auth confirmation MAC that proves both peers derived the same
/// key from the same password over the same transcript.</summary>
public sealed record HelloAckPayload(byte[] Confirmation)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteBytes(Confirmation);
        return w.ToArray();
    }

    public static HelloAckPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new HelloAckPayload(r.ReadBytes());
    }
}

/// <summary>A screen update: one or more changed rectangles, each an independent JPEG.</summary>
public sealed record ScreenFramePayload(uint FrameId, IReadOnlyList<ScreenTile> Tiles)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteUInt32(FrameId);
        w.WriteInt32(Tiles.Count);
        foreach (var t in Tiles)
        {
            w.WriteInt32(t.Rect.X);
            w.WriteInt32(t.Rect.Y);
            w.WriteInt32(t.Rect.Width);
            w.WriteInt32(t.Rect.Height);
            w.WriteBytes(t.Jpeg);
        }
        return w.ToArray();
    }

    public static ScreenFramePayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        uint frameId = r.ReadUInt32();
        int count = r.ReadInt32();
        var tiles = new List<ScreenTile>(count);
        for (int i = 0; i < count; i++)
        {
            var rect = new PixelRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            tiles.Add(new ScreenTile(rect, r.ReadBytes()));
        }
        return new ScreenFramePayload(frameId, tiles);
    }
}

public readonly record struct ScreenTile(PixelRect Rect, byte[] Jpeg);

public sealed record FrameAckPayload(uint FrameId)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteUInt32(FrameId);
        return w.ToArray();
    }

    public static FrameAckPayload Parse(ReadOnlySpan<byte> data) => new(new PayloadReader(data).ReadUInt32());
}

public sealed record CursorUpdatePayload(int X, int Y, bool Visible, byte[] PngShape)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteInt32(X);
        w.WriteInt32(Y);
        w.WriteBool(Visible);
        w.WriteBytes(PngShape);
        return w.ToArray();
    }

    public static CursorUpdatePayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new CursorUpdatePayload(r.ReadInt32(), r.ReadInt32(), r.ReadBool(), r.ReadBytes());
    }
}

public enum QualityLevel : byte { Low = 0, Medium = 1, High = 2 }

public sealed record QualityChangePayload(QualityLevel Level)
{
    public byte[] Serialize() { var w = new PayloadWriter(); w.WriteByte((byte)Level); return w.ToArray(); }
    public static QualityChangePayload Parse(ReadOnlySpan<byte> data) => new((QualityLevel)new PayloadReader(data).ReadByte());
}

[Flags]
public enum MouseButtonFlags : ushort
{
    None = 0,
    LeftDown = 1, LeftUp = 2,
    RightDown = 4, RightUp = 8,
    MiddleDown = 16, MiddleUp = 32,
    Wheel = 64,
}

/// <summary>Absolute mouse position normalized to 0..65535 over the primary monitor.</summary>
public sealed record InputMousePayload(ushort NormX, ushort NormY, MouseButtonFlags Buttons, short WheelDelta)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteUInt16(NormX);
        w.WriteUInt16(NormY);
        w.WriteUInt16((ushort)Buttons);
        w.WriteUInt16(unchecked((ushort)WheelDelta));
        return w.ToArray();
    }

    public static InputMousePayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new InputMousePayload(r.ReadUInt16(), r.ReadUInt16(),
            (MouseButtonFlags)r.ReadUInt16(), unchecked((short)r.ReadUInt16()));
    }
}

/// <summary>
/// Keyboard event. When <see cref="IsUnicode"/> is set, <see cref="Value"/> is a
/// UTF-16 code unit to inject via KEYEVENTF_UNICODE (layout independent, the fix
/// for DE-viewer / US-host mismatches). Otherwise it is a virtual-key code.
/// </summary>
public sealed record InputKeyboardPayload(bool IsUnicode, ushort Value, bool IsDown, bool IsExtended)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteBool(IsUnicode);
        w.WriteUInt16(Value);
        w.WriteBool(IsDown);
        w.WriteBool(IsExtended);
        return w.ToArray();
    }

    public static InputKeyboardPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new InputKeyboardPayload(r.ReadBool(), r.ReadUInt16(), r.ReadBool(), r.ReadBool());
    }
}

public sealed record ClipboardTextPayload(string Text)
{
    public byte[] Serialize() { var w = new PayloadWriter(); w.WriteString(Text); return w.ToArray(); }
    public static ClipboardTextPayload Parse(ReadOnlySpan<byte> data) => new(new PayloadReader(data).ReadString());
}

public sealed record FileOfferPayload(string TransferId, string FileName, long Size)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteString(TransferId);
        w.WriteString(FileName);
        w.WriteInt64(Size);
        return w.ToArray();
    }

    public static FileOfferPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new FileOfferPayload(r.ReadString(), r.ReadString(), r.ReadInt64());
    }
}

public sealed record FileChunkPayload(string TransferId, long Offset, byte[] Data)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter();
        w.WriteString(TransferId);
        w.WriteInt64(Offset);
        w.WriteBytes(Data);
        return w.ToArray();
    }

    public static FileChunkPayload Parse(ReadOnlySpan<byte> data)
    {
        var r = new PayloadReader(data);
        return new FileChunkPayload(r.ReadString(), r.ReadInt64(), r.ReadBytes());
    }
}
