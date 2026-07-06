using System.Net.Sockets;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Viewer;

/// <summary>Short-lived online/offline query against the relay for address-book
/// entries. (v1 gates presence by knowledge of the full session id; the proof
/// field is reserved for v1.1 authenticated presence.)</summary>
public static class RelayPresence
{
    public static async Task<Dictionary<string, bool>> QueryAsync(
        string relayHost, int relayPort, IReadOnlyList<string> sessionIds, CancellationToken ct)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return result;

        using var client = new TcpClient();
        await client.ConnectAsync(relayHost, relayPort, ct).ConfigureAwait(false);
        client.NoDelay = true;
        var conn = new FramedConnection(client.GetStream());

        var entries = sessionIds.Select(id => new QueryStatusEntry(id, Array.Empty<byte>())).ToList();
        await conn.SendAsync(MessageType.QueryStatus, new QueryStatusPayload(entries).Serialize(), ct)
            .ConfigureAwait(false);

        Frame? resp = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        conn.Dispose();
        if (resp is null || resp.Value.Type != MessageType.StatusResponse) return result;

        foreach (StatusResult r in StatusResponsePayload.Parse(resp.Value.Payload).Results)
            result[r.SessionId] = r.Online;
        return result;
    }
}
