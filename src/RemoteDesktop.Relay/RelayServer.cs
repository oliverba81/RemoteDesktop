using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Relay;

/// <summary>
/// The rendezvous relay. It parses only the plaintext control frames
/// (Register/Join/Query) and, once a host and viewer are matched, switches to a
/// blind byte pipe — it never holds any password-derived secret and cannot read
/// the end-to-end-encrypted session.
/// </summary>
public sealed class RelayServer
{
    private readonly RelayOptions _opts;
    private readonly IpThrottle _throttle;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ProfileStore? _profiles;
    private TcpListener? _listener;

    public RelayServer(RelayOptions opts)
    {
        _opts = opts;
        _throttle = new IpThrottle(opts);
        if (opts.ProfileDir is not null)
            _profiles = new ProfileStore(opts.ProfileDir, opts.MaxProfileBytes);
    }

    /// <summary>The port actually bound (useful when Port was 0 in tests).</summary>
    public int BoundPort { get; private set; }

    public int SessionCount => _sessions.Count;

    public async Task RunAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Any, _opts.Port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log($"Relay listening on port {BoundPort}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally { _listener.Stop(); }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        Stream stream = client.GetStream();
        var conn = new FramedConnection(stream);
        try
        {
            client.NoDelay = true;
            Frame? first = await conn.ReceiveAsync(ct).ConfigureAwait(false);
            if (first is null) { conn.Dispose(); client.Close(); return; }

            switch (first.Value.Type)
            {
                case MessageType.RegisterSession:
                    await HandleRegisterAsync(RegisterSessionPayload.Parse(first.Value.Payload), client, stream, conn, ct);
                    break;
                case MessageType.JoinSession:
                    await HandleJoinAsync(JoinSessionPayload.Parse(first.Value.Payload), client, stream, conn, ip, ct);
                    break;
                case MessageType.QueryStatus:
                    await HandleQueryAsync(QueryStatusPayload.Parse(first.Value.Payload), conn, ip, ct);
                    conn.Dispose(); client.Close();
                    break;
                case MessageType.ProfilePut:
                    await HandleProfilePutAsync(ProfilePutPayload.Parse(first.Value.Payload), conn, ip, ct);
                    conn.Dispose(); client.Close();
                    break;
                case MessageType.ProfileGet:
                    await HandleProfileGetAsync(ProfileGetPayload.Parse(first.Value.Payload), conn, ip, ct);
                    conn.Dispose(); client.Close();
                    break;
                default:
                    conn.Dispose(); client.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"[{ip}] connection error: {ex.Message}");
            try { conn.Dispose(); client.Close(); } catch { /* ignore */ }
        }
    }

    private async Task HandleRegisterAsync(
        RegisterSessionPayload p, TcpClient client, Stream stream, FramedConnection conn, CancellationToken ct)
    {
        if (Major(p.Version) != Major(ProtocolConstants.Version))
        {
            await SendErrorAsync(conn, SessionErrorCode.VersionMismatch, "Update the relay or client.", ct);
            conn.Dispose(); client.Close();
            return;
        }
        if (_sessions.Count >= _opts.MaxSessions)
        {
            await SendErrorAsync(conn, SessionErrorCode.Internal, "Relay at capacity.", ct);
            conn.Dispose(); client.Close();
            return;
        }

        var fresh = new Session
        {
            Id = p.SessionId,
            OwnerTag = p.OwnerTag,
            HostClient = client,
            HostStream = stream,
            HostConn = conn,
        };

        while (true)
        {
            if (_sessions.TryAdd(p.SessionId, fresh)) break;

            if (_sessions.TryGetValue(p.SessionId, out var existing))
            {
                // Re-register is only allowed by the owner (prevents id squatting and
                // lets an unattended host reclaim its fixed id after a reconnect).
                if (!CryptographicOperations.FixedTimeEquals(existing.OwnerTag, p.OwnerTag))
                {
                    await SendErrorAsync(conn, SessionErrorCode.NotOwner, "Session id already in use.", ct);
                    conn.Dispose(); client.Close();
                    return;
                }
                lock (existing.Lock)
                {
                    existing.TeardownViewer();
                    existing.TeardownHost();
                }
                _sessions.TryRemove(new KeyValuePair<string, Session>(p.SessionId, existing));
            }
        }

        Log($"Registered session {Mask(p.SessionId)} (total {_sessions.Count})");

        // Park: wait until a viewer pairs or the host drops. Pre-pairing frames from
        // the host (heartbeats) are ignored. When a viewer joins it cancels ParkCts,
        // and we release the stream so the blind pipe becomes its sole reader.
        bool hostGone = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, fresh.ParkCts.Token);
        try
        {
            while (true)
            {
                Frame? f = await conn.ReceiveAsync(linked.Token).ConfigureAwait(false);
                if (f is null) { hostGone = true; break; }        // host disconnected
                if (f.Value.Type == MessageType.Bye) { hostGone = true; break; }
            }
        }
        catch (OperationCanceledException) when (fresh.ParkCts.IsCancellationRequested)
        {
            // A viewer paired — normal handoff to the blind pipe.
        }
        catch (Exception)
        {
            hostGone = true;
        }
        finally
        {
            fresh.ParkExited.TrySetResult();
            if (hostGone)
            {
                _sessions.TryRemove(new KeyValuePair<string, Session>(p.SessionId, fresh));
                fresh.TeardownHost();
                Log($"Session {Mask(p.SessionId)} closed (host gone)");
            }
        }
    }

    private async Task HandleJoinAsync(
        JoinSessionPayload p, TcpClient client, Stream stream, FramedConnection conn, string ip, CancellationToken ct)
    {
        if (_throttle.IsJoinBlocked(ip))
        {
            await SendErrorAsync(conn, SessionErrorCode.RateLimited, "Too many attempts. Try again later.", ct);
            conn.Dispose(); client.Close();
            return;
        }
        if (Major(p.Version) != Major(ProtocolConstants.Version))
        {
            await SendErrorAsync(conn, SessionErrorCode.VersionMismatch, "Update the relay or client.", ct);
            conn.Dispose(); client.Close();
            return;
        }

        if (!_sessions.TryGetValue(p.SessionId, out var session))
        {
            _throttle.RecordJoinFailure(ip);
            await SendErrorAsync(conn, SessionErrorCode.SessionNotFound, "No such session.", ct);
            conn.Dispose(); client.Close();
            return;
        }

        bool claimed = false;
        lock (session.Lock)
        {
            if (!session.ViewerAttached && !session.Piping)
            {
                session.ViewerClient = client;
                session.ViewerStream = stream;
                session.ViewerConn = conn;
                session.ViewerAttached = true;
                session.Piping = true;
                claimed = true;
            }
        }

        if (!claimed)
        {
            await SendErrorAsync(conn, SessionErrorCode.SessionBusy, "A viewer is already connected.", ct);
            conn.Dispose(); client.Close();
            return;
        }

        Log($"Paired session {Mask(p.SessionId)}");

        // Stop the host park loop and wait until it has released the host stream, so
        // the blind pipe is the only reader.
        session.ParkCts.Cancel();
        await session.ParkExited.Task.ConfigureAwait(false);

        // Signal both peers, then hand the raw sockets to the blind pipe. From here
        // the relay forwards the PAKE handshake and all encrypted frames without
        // parsing them.
        await conn.SendAsync(MessageType.SessionReady, Array.Empty<byte>(), ct).ConfigureAwait(false);
        await session.HostConn.SendAsync(MessageType.SessionReady, Array.Empty<byte>(), ct).ConfigureAwait(false);

        try
        {
            await BlindPipe.RunAsync(
                session.HostClient, session.HostStream, client, stream, _opts.IdleTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<string, Session>(p.SessionId, session));
            lock (session.Lock) { session.TeardownViewer(); session.TeardownHost(); }
            Log($"Session {Mask(p.SessionId)} ended");
        }
    }

    private async Task HandleProfilePutAsync(
        ProfilePutPayload p, FramedConnection conn, string ip, CancellationToken ct)
    {
        if (_profiles is null)
        {
            await SendErrorAsync(conn, SessionErrorCode.ProfileDisabled, "Profile sync is disabled.", ct);
            return;
        }
        // Reuse the join-failure budget: a wrong passphrase costs an attempt so the
        // profile id space cannot be probed at will.
        if (_throttle.IsJoinBlocked(ip))
        {
            await SendErrorAsync(conn, SessionErrorCode.RateLimited, "Too many attempts. Try again later.", ct);
            return;
        }

        ProfileStore.PutResult result = _profiles.Put(p.ProfileId, p.AuthKey, p.Blob);
        switch (result)
        {
            case ProfileStore.PutResult.Stored:
                await conn.SendAsync(MessageType.ProfileAck, Array.Empty<byte>(), ct).ConfigureAwait(false);
                Log($"Profile {Mask(p.ProfileId)} stored ({p.Blob.Length} bytes)");
                break;
            case ProfileStore.PutResult.TooLarge:
                await SendErrorAsync(conn, SessionErrorCode.ProfileTooLarge, "Profile too large.", ct);
                break;
            default:
                _throttle.RecordJoinFailure(ip);
                await SendErrorAsync(conn, SessionErrorCode.ProfileAuthFailed, "Wrong sync passphrase.", ct);
                break;
        }
    }

    private async Task HandleProfileGetAsync(
        ProfileGetPayload p, FramedConnection conn, string ip, CancellationToken ct)
    {
        if (_profiles is null)
        {
            await SendErrorAsync(conn, SessionErrorCode.ProfileDisabled, "Profile sync is disabled.", ct);
            return;
        }
        if (_throttle.IsJoinBlocked(ip))
        {
            await SendErrorAsync(conn, SessionErrorCode.RateLimited, "Too many attempts. Try again later.", ct);
            return;
        }

        (ProfileStore.GetStatus status, byte[] blob) = _profiles.Get(p.ProfileId, p.AuthKey);
        switch (status)
        {
            case ProfileStore.GetStatus.Ok:
                await conn.SendAsync(MessageType.ProfileData, new ProfileDataPayload(blob).Serialize(), ct)
                    .ConfigureAwait(false);
                break;
            case ProfileStore.GetStatus.NotFound:
                await SendErrorAsync(conn, SessionErrorCode.ProfileNotFound, "No profile stored yet.", ct);
                break;
            default:
                _throttle.RecordJoinFailure(ip);
                await SendErrorAsync(conn, SessionErrorCode.ProfileAuthFailed, "Wrong sync passphrase.", ct);
                break;
        }
    }

    private async Task HandleQueryAsync(QueryStatusPayload p, FramedConnection conn, string ip, CancellationToken ct)
    {
        // Batch cap + per-IP distinct-id budget defeat mass enumeration. Attended
        // ids are short-lived and unattended ids are high-entropy, so knowing a
        // full id is itself the access check for presence in v1.
        var entries = p.Entries.Take(_opts.MaxQueryBatch).ToList();
        var results = new List<StatusResult>(entries.Count);

        if (!_throttle.TryChargeQuery(ip, entries.Count))
        {
            // Answer generically rather than leak which ids exist.
            foreach (var e in entries) results.Add(new StatusResult(e.SessionId, false));
        }
        else
        {
            foreach (var e in entries)
                results.Add(new StatusResult(e.SessionId, _sessions.ContainsKey(e.SessionId)));
        }

        await conn.SendAsync(MessageType.StatusResponse, new StatusResponsePayload(results).Serialize(), ct)
            .ConfigureAwait(false);
    }

    private static Task SendErrorAsync(FramedConnection conn, SessionErrorCode code, string message, CancellationToken ct)
        => conn.SendAsync(MessageType.SessionError, new SessionErrorPayload(code, message).Serialize(), ct);

    private static int Major(ushort version) => version; // single-byte major for v1
    private static string Mask(string id) => id.Length <= 4 ? "****" : id[..2] + new string('*', id.Length - 2);

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");
}
