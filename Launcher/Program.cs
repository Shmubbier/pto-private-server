using System.Net;
using System.Net.Sockets;

namespace PtoLauncher;

// Step 1 of the Steam P2P build order (see NETWORKING.md): the TCP-over-relay
// tunnel, proven with a plain-TCP peer link so it is testable on one box today.
//
// The game speaks raw TCP framed [id u8][key u16][size u32][payload] and never
// learns Steam exists: the launcher points it at 127.0.0.1 and tunnels the bytes.
// A bridge is just "accept here, connect there, pump bytes", so HOST and JOIN are
// the same ServeAsync with different endpoints. When Steam lands (step 1b), only
// ONE hop changes: JOIN's outbound TcpClient becomes a relay-connect and HOST's
// inbound accept becomes a relay-accept, both still yielding a Stream to Pump.
static class Program
{
    const int GamePort = 51338; // loopback port the game dials (settings.ini IP=127.0.0.1)
    const int PeerPort = 51339; // spike-only plain-TCP stand-in for the Steam relay link

    static async Task<int> Main(string[] args)
    {
        switch (args.Length > 0 ? args[0] : "")
        {
            case "host": // accept peers, bridge each to the local server
                await ServeAsync(IPAddress.Any, PeerPort, "127.0.0.1", GamePort, CancellationToken.None);
                return 0;
            case "join": // accept the local game, bridge to the host peer
                await ServeAsync(IPAddress.Loopback, GamePort,
                                 args.Length > 1 ? args[1] : "127.0.0.1", PeerPort, CancellationToken.None);
                return 0;
            case "demo":
                return await DemoAsync();
            default:
                Console.WriteLine("usage: ptolaunch host | join <hostip> | demo");
                return 1;
        }
    }

    // Accept on (bind:listenPort); for every inbound connection open one outbound
    // to (outHost:outPort) and pump bytes both ways until either side closes.
    static async Task ServeAsync(IPAddress bind, int listenPort, string outHost, int outPort, CancellationToken ct)
    {
        var l = new TcpListener(bind, listenPort);
        l.Start();
        Console.WriteLine($"listen {bind}:{listenPort} -> {outHost}:{outPort}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var inbound = await l.AcceptTcpClientAsync(ct);
                _ = BridgeToAsync(inbound, outHost, outPort);
            }
        }
        finally { l.Stop(); }
    }

    static async Task BridgeToAsync(TcpClient inbound, string outHost, int outPort)
    {
        inbound.NoDelay = true;
        try
        {
            using (inbound)
            using (var outbound = new TcpClient())
            {
                await outbound.ConnectAsync(outHost, outPort);
                outbound.NoDelay = true;
                await PumpAsync(inbound.GetStream(), outbound.GetStream());
            }
        }
        catch (Exception ex) { Console.WriteLine($"bridge closed: {ex.Message}"); }
    }

    // Byte-for-byte tunnel between two streams. Never parses the protocol, so the
    // framing and patches 05/07/08 are untouched. This is the exact data path the
    // Steam relay will replace on the peer side.
    static async Task PumpAsync(Stream a, Stream b)
    {
        using var cts = new CancellationTokenSource();
        var t1 = CopyAsync(a, b, cts);
        var t2 = CopyAsync(b, a, cts);
        await Task.WhenAny(t1, t2);
        cts.Cancel(); // one side closed: unblock the other
        try { await Task.WhenAll(t1, t2); } catch { /* expected on teardown */ }
    }

    static async Task CopyAsync(Stream from, Stream to, CancellationTokenSource cts)
    {
        try { await from.CopyToAsync(to, cts.Token); }
        finally { cts.Cancel(); }
    }

    // Self-check: game -> join bridge -> peer link -> host bridge -> server, echoed
    // back. A 3 KB blob forces TCP segmentation across all four hops (the size that
    // crashed the game before patch 07), proving the pump preserves bytes in order.
    static async Task<int> DemoAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int sPort = 52001, pPort = 52002, gPort = 52003;
        _ = EchoServerAsync(sPort, cts.Token);                                   // fake server
        _ = ServeAsync(IPAddress.Loopback, pPort, "127.0.0.1", sPort, cts.Token); // host: peer -> server
        _ = ServeAsync(IPAddress.Loopback, gPort, "127.0.0.1", pPort, cts.Token); // join: game -> peer
        await Task.Delay(300); // let the three listeners bind

        using var game = new TcpClient();
        await game.ConnectAsync("127.0.0.1", gPort);
        game.NoDelay = true;
        var ns = game.GetStream();

        var sent = new byte[3000];
        new Random(1).NextBytes(sent);
        await ns.WriteAsync(sent);

        var got = new byte[sent.Length];
        int off = 0;
        while (off < got.Length)
        {
            int n = await ns.ReadAsync(got.AsMemory(off));
            if (n == 0) break;
            off += n;
        }

        bool ok = off == sent.Length && got.AsSpan().SequenceEqual(sent);
        Console.WriteLine(ok
            ? "demo OK: 3000 bytes round-tripped game -> join -> host -> server -> back, in order"
            : $"demo FAIL: got {off}/{sent.Length} bytes, match={got.AsSpan().SequenceEqual(sent)}");
        return ok ? 0 : 1;
    }

    static async Task EchoServerAsync(int port, CancellationToken ct)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var c = await l.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    using (c) { c.NoDelay = true; var s = c.GetStream(); await s.CopyToAsync(s, ct); }
                });
            }
        }
        finally { l.Stop(); }
    }
}
