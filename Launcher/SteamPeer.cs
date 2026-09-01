using System.Collections.Concurrent;
using System.Threading.Channels;
using Steamworks;

namespace PtoLauncher;

// The peer hop over Steam, using the CLASSIC ISteamNetworking P2P API
// (SendP2PPacket / ReadP2PPacket) instead of the newer SteamNetworkingSockets/SDR.
// SDR-relayed P2P does not work on AppID 480 for CGNAT'd peers (5008 rendezvous
// timeout); the classic API does its own NAT-punch + Valve relay and historically
// works on 480. A peer session is exposed as a duplex Stream so nothing else in the
// launcher changes; reliable+ordered packets behave like TCP, so the game's framing
// and patch-07 reassembly are untouched.
//
// AppID 480 (Spacewar) via steam_appid.txt. Requires the Steam client running; the
// pump thread owns SteamAPI callbacks and P2P packet receive. Sessions are keyed by
// the remote SteamID64 (the classic API has no connection handles or listen sockets).
sealed class SteamRelay : IDisposable
{
    readonly ConcurrentDictionary<ulong, SteamConnectionStream> _conns = new();
    readonly ConcurrentQueue<string> _log = new(); // status logs, drained OFF the callback thread
    Callback<P2PSessionRequest_t>? _reqCb;
    Callback<P2PSessionConnectFail_t>? _failCb;
    Func<SteamConnectionStream, Task>? _onAccept;
    volatile bool _listening;
    Thread? _pump;
    volatile bool _run = true;

    public ulong MySteamId { get; private set; }
    public string MyName { get; private set; } = "host";

    // The classic path has no SDR route to wait on; kept because the guest still polls
    // it before connecting. Always ready.
    public bool RelayReady() => true;

    public void Init()
    {
        if (!SteamAPI.Init())
            throw new InvalidOperationException(
                "SteamAPI.Init failed. Is Steam running, and steam_appid.txt (480) beside the exe?");
        MySteamId = SteamUser.GetSteamID().m_SteamID;
        MyName = SteamFriends.GetPersonaName();
        SteamNetworkingUtils.InitRelayNetworkAccess(); // harmless; may warm the relay classic routes through
        _reqCb = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
        _failCb = Callback<P2PSessionConnectFail_t>.Create(OnSessionConnectFail);
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "steam-pump" };
        _pump.Start();
    }

    // HOST: accept inbound peers; onAccept gets each new peer session as a Stream.
    public void Listen(Func<SteamConnectionStream, Task> onAccept)
    {
        _onAccept = onAccept;
        _listening = true;
        Console.WriteLine("steam: listening for peers (classic P2P)");
    }

    // JOIN: open a session to a host SteamID. The classic API has no pre-data "connected"
    // event, so the stream is returned immediately; a peer that can't be reached surfaces
    // later as a P2PSessionConnectFail. timeoutSeconds kept for signature compatibility.
    public Task<SteamConnectionStream> ConnectAsync(ulong hostSteamId, int timeoutSeconds = 15)
    {
        // Fresh session every time. A stale stream from a previous match has a completed read
        // channel (reads return 0 -> "host closed"), so drop any old one and reset the P2P
        // session so the host gets a clean P2PSessionRequest.
        if (_conns.TryRemove(hostSteamId, out var old)) old.Complete();
        SteamNetworking.CloseP2PSessionWithUser(new CSteamID(hostSteamId));
        var s = new SteamConnectionStream(this, hostSteamId);
        _conns[hostSteamId] = s;
        s.Connected.TrySetResult(s);
        return s.Connected.Task;
    }

    void PumpLoop()
    {
        while (_run)
        {
            SteamAPI.RunCallbacks(); // dispatches OnSessionRequest / OnSessionConnectFail on this thread
            while (_log.TryDequeue(out var line)) Console.WriteLine(line); // print AFTER the lock is released
            while (SteamNetworking.IsP2PPacketAvailable(out uint size) && size > 0)
            {
                var buf = new byte[size];
                if (SteamNetworking.ReadP2PPacket(buf, size, out uint read, out CSteamID sender) && read > 0)
                {
                    if (read != size) Array.Resize(ref buf, (int)read);
                    if (_conns.TryGetValue(sender.m_SteamID, out var s)) s.Deliver(buf);
                    // else: packet from an unknown/closed peer -> drop
                }
            }
            Thread.Sleep(4);
        }
    }

    // A peer wants to send to us. Always accept; if it's a new inbound peer and we're the
    // host, create its stream and hand it to the bridge.
    void OnSessionRequest(P2PSessionRequest_t ev)
    {
        ulong id = ev.m_steamIDRemote.m_SteamID;
        SteamNetworking.AcceptP2PSessionWithUser(ev.m_steamIDRemote);
        if (!_listening) return; // only the host bridges inbound peers (the guest initiated its own)
        // A request starts a (re)connection. If we're already bridging this peer, ignore the dup;
        // if a stale stream lingers (previous match), replace it and re-bridge.
        if (_conns.TryGetValue(id, out var live) && !live.Completed) return;
        if (_conns.TryRemove(id, out var stale)) stale.Complete();
        var s = new SteamConnectionStream(this, id);
        _conns[id] = s;
        _log.Enqueue("relay: incoming peer, accepting");
        if (_onAccept != null) _ = _onAccept(s);
    }

    // A session could not be established (peer unreachable / not on the app). The error
    // code is the key diagnostic: 4 = Timeout (relay couldn't carry it), 2 = NoRightsToApp,
    // 3 = DestinationNotLoggedIn, 1 = NotRunningApp.
    void OnSessionConnectFail(P2PSessionConnectFail_t ev)
    {
        ulong id = ev.m_steamIDRemote.m_SteamID;
        _log.Enqueue($"relay: P2P session failed (error {ev.m_eP2PSessionError})");
        if (_conns.TryRemove(id, out var down))
        {
            down.Connected.TrySetException(new IOException($"P2P session failed (error {ev.m_eP2PSessionError})"));
            down.Complete(); // read side returns 0, tearing down the bridge
        }
        SteamNetworking.CloseP2PSessionWithUser(ev.m_steamIDRemote);
    }

    internal void Forget(ulong peer) => _conns.TryRemove(peer, out _);

    public void Dispose()
    {
        _run = false;
        _pump?.Join(500);
        SteamAPI.Shutdown();
    }
}

// One peer session as a duplex Stream. Writes are reliable P2P packets; reads drain a
// channel the SteamRelay pump fills. Reliable+ordered, so bytes arrive intact and in
// order like TCP. Keyed by the remote SteamID64.
sealed class SteamConnectionStream : Stream
{
    readonly SteamRelay _relay;
    readonly ulong _peer;
    readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    byte[]? _left;
    int _leftOff;

    public bool Completed { get; private set; } // read channel closed: a dead/reusable-only-fresh stream

    public readonly TaskCompletionSource<SteamConnectionStream> Connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SteamConnectionStream(SteamRelay relay, ulong peerSteamId) { _relay = relay; _peer = peerSteamId; }

    internal void Deliver(byte[] data) => _rx.Writer.TryWrite(data);
    internal void Complete() { Completed = true; _rx.Writer.TryComplete(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_left == null || _leftOff >= _left.Length)
        {
            try { _left = await _rx.Reader.ReadAsync(ct); _leftOff = 0; }
            catch (ChannelClosedException) { return 0; } // peer closed
        }
        int n = Math.Min(buffer.Length, _left.Length - _leftOff);
        _left.AsSpan(_leftOff, n).CopyTo(buffer.Span);
        _leftOff += n;
        return n;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var data = buffer.ToArray();
        if (!SteamNetworking.SendP2PPacket(new CSteamID(_peer), data, (uint)data.Length, EP2PSend.k_EP2PSendReliable))
            throw new IOException("steam SendP2PPacket failed");
        return ValueTask.CompletedTask;
    }

    // Sync shims over the async paths (PumpAsync only uses the async ones).
    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        Complete();
        _relay.Forget(_peer); // remove from the relay's map so the next match gets a fresh stream
        SteamNetworking.CloseP2PSessionWithUser(new CSteamID(_peer));
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override void Flush() { }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}
