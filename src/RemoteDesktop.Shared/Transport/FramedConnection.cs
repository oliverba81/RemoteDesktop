using System.Buffers;
using System.Buffers.Binary;
using RemoteDesktop.Shared.Crypto;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Transport;

/// <summary>A single decoded message: type + raw payload bytes.</summary>
public readonly record struct Frame(MessageType Type, byte[] Payload);

/// <summary>
/// Length-prefixed message transport over a duplex <see cref="Stream"/>.
///
/// Wire layout of one frame: <c>[4B len(LE)][body]</c>.
///  * Control/handshake phase (plaintext): <c>body = [1B type][payload]</c>.
///  * Secure phase (after <see cref="ActivateEncryption"/>):
///    <c>body = [8B counter][ AES-GCM( [1B type][payload] ) || 16B tag ]</c>.
///
/// The MessageType lives inside the encrypted plaintext, so it is covered by the
/// GCM tag and cannot be flipped by a tampering relay.
///
/// Sends are serialized by a single writer (see <see cref="SendScheduler"/>); this
/// class does not lock. Reads are single-threaded on one receive loop.
/// </summary>
public sealed class FramedConnection : IDisposable
{
    private readonly Stream _stream;
    private AeadCipher? _send;
    private AeadCipher? _recv;
    private ulong _sendCounter;

    public FramedConnection(Stream stream) => _stream = stream;

    public bool IsEncrypted => _send is not null;

    /// <summary>Switches both directions to AES-GCM. Called once, after the handshake.</summary>
    public void ActivateEncryption(AeadCipher send, AeadCipher recv)
    {
        _send = send;
        _recv = recv;
        _sendCounter = 0;
    }

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] body;
        if (_send is null)
        {
            // Plaintext: [type][payload]
            body = new byte[1 + payload.Length];
            body[0] = (byte)type;
            payload.CopyTo(body.AsMemory(1));
        }
        else
        {
            // Secure: [8B counter][ciphertext(=[type][payload]) || tag]
            ulong counter = _sendCounter++;
            var plaintext = new byte[1 + payload.Length];
            plaintext[0] = (byte)type;
            payload.CopyTo(plaintext.AsMemory(1));
            byte[] sealedBytes = _send.Seal(counter, plaintext);
            body = new byte[ProtocolConstants.NonceCounterSize + sealedBytes.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(body, counter);
            sealedBytes.CopyTo(body.AsSpan(ProtocolConstants.NonceCounterSize));
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await _stream.WriteAsync(header, ct).ConfigureAwait(false);
        await _stream.WriteAsync(body, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or returns null on a clean end-of-stream.</summary>
    public async Task<Frame?> ReceiveAsync(CancellationToken ct)
    {
        byte[] header = new byte[4];
        int read = await ReadUpToAsync(header, ct).ConfigureAwait(false);
        if (read == 0) return null; // clean EOF
        if (read < 4) throw new ProtocolException("Truncated frame header.");

        int bodyLen = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (bodyLen < 0 || bodyLen > ProtocolConstants.MaxFrameBody)
            throw new ProtocolException($"Frame length {bodyLen} out of range.");

        byte[] body = ArrayPool<byte>.Shared.Rent(bodyLen);
        try
        {
            await _stream.ReadExactlyAsync(body.AsMemory(0, bodyLen), ct).ConfigureAwait(false);
            return Decode(body.AsSpan(0, bodyLen));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    private Frame Decode(ReadOnlySpan<byte> body)
    {
        if (_recv is null)
        {
            if (body.Length < 1) throw new ProtocolException("Empty plaintext frame.");
            return new Frame((MessageType)body[0], body[1..].ToArray());
        }

        if (body.Length < ProtocolConstants.NonceCounterSize)
            throw new ProtocolException("Secure frame missing counter.");
        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(body);
        byte[] plaintext = _recv.Open(counter, body[ProtocolConstants.NonceCounterSize..]);
        if (plaintext.Length < 1) throw new ProtocolException("Empty secure plaintext.");
        return new Frame((MessageType)plaintext[0], plaintext[1..]);
    }

    private async Task<int> ReadUpToAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public void Dispose()
    {
        _send?.Dispose();
        _recv?.Dispose();
        _stream.Dispose();
    }
}
