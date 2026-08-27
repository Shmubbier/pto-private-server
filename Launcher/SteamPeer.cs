using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Steamworks;

namespace PtoLauncher;

// Step 1b: the peer hop over Steam's relay (SDR), replacing the plain-TCP link.
// A relay connection is exposed as a Stream so it plugs straight into PumpAsync,
// so nothing else in the launcher changes. Reliable+ordered messages behave like
// TCP, so the game's framing and patch-07 reassembly are untouched.
//
// AppID 480 (Spacewar) via steam_appid.txt. Requires the Steam client running;
// the SteamRelay pump thread owns SteamAPI callbacks and message receive.
sealed class SteamRelay : IDisposable
{
    const int VirtualPort = 0; // single service; ConnectP2P and the listen socket must agree

    readonly ConcurrentDictionary<HSteamNetConnection, SteamConnectionStream> _conns = new();
    readonly ConcurrentQueue<string> _log = new(); // status logs, drained OFF the callback thread
    Callback<SteamNetConnectionStatusChangedCallback_t>? _cb;
    HSteamListenSocket _listen = HSteamListenSocket.Invalid;
    Func<SteamConnectionStream, Task>? _onAccept;
    Thread? _pump;
    volatile bool _run = true;

    public ulong MySteamId { get; private set; }
    public string MyName { get; private set; } = "host";

    // Is the SDR relay route established? InitRelayNetworkAccess resolves this in the
    // background over a few seconds; connecting before it's Current causes 5008 timeouts.
    public bool RelayReady() =>
        SteamNetworkingUtils.GetRelayNetworkStatus(out _) ==
        ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current;

    public void Init()
    {
        if (!SteamAPI.Init())
            throw new InvalidOperationException(
                "SteamAPI.Init failed. Is Steam running, and steam_appid.txt (480) beside the exe?");
        MySteamId = SteamUser.GetSteamID().m_SteamID;
        MyName = SteamFriends.GetPersonaName();
        SteamNetworkingUtils.InitRelayNetworkAccess(); // start warming a relay route
        _cb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "steam-pump" };
        _pump.Start();
    }

    // HOST: listen for inbound relay connections; onAccept gets each as a Stream.
    public void Listen(Func<SteamConnectionStream, Task> onAccept)
    {
        _onAccept = onAccept;
        _listen = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
        Console.WriteLine("steam: listening for peers on the relay");
    }

    // JOIN: open a relay connection to a host SteamID, resolved when it connects, with a
    // timeout so a stuck "connecting" (relay route not ready, host unreachable) can't hang.
    public async Task<SteamConnectionStream> ConnectAsync(ulong hostSteamId, int timeoutSeconds = 15)
    {
        var id = new SteamNetworkingIdentity();
        id.SetSteamID64(hostSteamId);
        HSteamNetConnection conn = SteamNetworkingSockets.ConnectP2P(ref id, VirtualPort, 0, null);
        var s = new SteamConnectionStream(conn);
        _conns[conn] = s;
        var done = await Task.WhenAny(s.Connected.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (done != s.Connected.Task)
        {
            _conns.TryRemove(conn, out _);
            SteamNetworkingSockets.CloseConnection(conn, 0, "connect timeout", false);
            throw new TimeoutException($"relay connect to {hostSteamId} timed out after {timeoutSeconds}s");
        }
        return await s.Connected.Task; // completed, or faulted (peer closed)
    }

    void PumpLoop()
    {
        var msgs = new IntPtr[64];
        while (_run)
        {
            SteamAPI.RunCallbacks(); // dispatches OnStatusChanged on this thread
            while (_log.TryDequeue(out var line)) Console.WriteLine(line); // print AFTER the lock is released
            foreach (var kv in _conns)
            {
                int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(kv.Key, msgs, msgs.Length);
                for (int i = 0; i < n; i++)
                {
                    SteamNetworkingMessage_t m = Marshal.PtrToStructure<SteamNetworkingMessage_t>(msgs[i]);
                    var data = new byte[m.m_cbSize];
                    Marshal.Copy(m.m_pData, data, 0, m.m_cbSize);
                    kv.Value.Deliver(data);
                    SteamNetworkingMessage_t.Release(msgs[i]);
                }
            }
            Thread.Sleep(4);
        }
    }

    void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t ev)
    {
        HSteamNetConnection conn = ev.m_hConn;
        switch (ev.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                // Inbound (has a listen socket) needs accepting; outbound has none.
                if (ev.m_info.m_hListenSocket != HSteamListenSocket.Invalid)
                {
                    _log.Enqueue("relay: incoming peer, accepting");
                    if (SteamNetworkingSockets.AcceptConnection(conn) == EResult.k_EResultOK)
                        _conns[conn] = new SteamConnectionStream(conn);
                    else
                        SteamNetworkingSockets.CloseConnection(conn, 0, "accept failed", false);
                }
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                _log.Enqueue("relay: peer connected");
                if (_conns.TryGetValue(conn, out var up))
                {
                    up.Connected.TrySetResult(up); // unblocks JOIN's ConnectAsync
                    if (_onAccept != null) _ = _onAccept(up); // HOST: start bridging this peer
                }
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                _log.Enqueue($"relay: connection closed ({ev.m_info.m_eEndReason}: {ev.m_info.m_szEndDebug})");
                if (_conns.TryRemove(conn, out var down))
                {
                    down.Connected.TrySetException(new IOException("relay connection closed"));
                    down.Complete(); // read side returns 0, tearing down the bridge
                }
                SteamNetworkingSockets.CloseConnection(conn, 0, "", false);
                break;
        }
    }

    internal void Forget(HSteamNetConnection conn) => _conns.TryRemove(conn, out _);

    public void Dispose()
    {
        _run = false;
        _pump?.Join(500);
        if (_listen != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(_listen);
        SteamAPI.Shutdown();
    }
}

// One relay connection as a duplex Stream. Writes are reliable Steam messages;
// reads drain a channel the SteamRelay pump fills. Ordered+reliable, so bytes
// arrive intact and in order like TCP.
sealed class SteamConnectionStream : Stream
{
    readonly HSteamNetConnection _conn;
    readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    byte[]? _left;
    int _leftOff;

    public readonly TaskCompletionSource<SteamConnectionStream> Connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SteamConnectionStream(HSteamNetConnection conn) => _conn = conn;

    internal void Deliver(byte[] data) => _rx.Writer.TryWrite(data);
    internal void Complete() => _rx.Writer.TryComplete();

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

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        IntPtr p = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer.ToArray(), 0, p, buffer.Length);
            EResult r = SteamNetworkingSockets.SendMessageToConnection(
                _conn, p, (uint)buffer.Length, Constants.k_nSteamNetworkingSend_Reliable, out _);
            if (r != EResult.k_EResultOK) throw new IOException("steam send: " + r);
        }
        finally { Marshal.FreeHGlobal(p); }
        await Task.CompletedTask;
    }

    // Sync shims over the async paths (PumpAsync only uses the async ones).
    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        _rx.Writer.TryComplete();
        SteamNetworkingSockets.CloseConnection(_conn, 0, "", false);
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
