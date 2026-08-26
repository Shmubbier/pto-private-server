using System.Net;
using System.Net.Sockets;
using System.Text;

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
            case "steamhost": // accept relay peers, bridge each to the local server
                await SteamHostAsync();
                return 0;
            case "steamjoin": // tunnel the local game to a host's SteamID over the relay
                if (args.Length < 2 || !ulong.TryParse(args[1], out var hostId))
                { Console.WriteLine("usage: ptolaunch steamjoin <hostSteamId64>"); return 1; }
                await SteamJoinAsync(hostId);
                return 0;
            case "hosts": // list live hosts from the directory (no Steam needed)
                return await HostsAsync();
            case "ladder": // print the shared ranked ladder (no Steam needed)
                return await PrintLadderAsync();
            case "play": // pick a host from the directory, then join over the relay
                return await PlayAsync();
            case "demo":
                return await DemoAsync();
            case "metademo":
                return await MetaDemoAsync();
            case "rankeddemo":
                return await RankedDemoAsync();
            default:
                Console.WriteLine("usage: ptolaunch steamhost | play | hosts | ladder | steamjoin <id> | host | join <ip> | demo");
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

    // HOST over Steam: same shape as `host`, but the inbound hop is the relay.
    static async Task SteamHostAsync()
    {
        using var relay = new SteamRelay();
        relay.Init();
        Console.WriteLine($"steam host ready. Share your SteamID64: {relay.MySteamId}");

        // Publish presence to the directory on a heartbeat; clear it on exit.
        var meta = new MetaClient();
        if (meta.Enabled)
        {
            Console.CancelKeyPress += (_, _) => { meta.UnpublishHostAsync(relay.MySteamId).Wait(2000); };
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try { await meta.PublishHostAsync(relay.MySteamId, relay.MyName); }
                    catch (Exception ex) { Console.WriteLine($"presence publish failed: {ex.Message}"); }
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            });
            Console.WriteLine("presence: published to host directory (heartbeat 10s)");
            _ = WatchMatchesAsync(meta); // push finished matches to the shared ladder
            Console.WriteLine("ladder: watching server matches.txt");
        }
        else Console.WriteLine("presence: PTO_FIREBASE_URL not set, directory disabled (join by SteamID64)");

        relay.Listen(async peer =>
        {
            try
            {
                using (peer)
                using (var server = new TcpClient())
                {
                    await server.ConnectAsync(IPAddress.Loopback, GamePort);
                    server.NoDelay = true;
                    await PumpAsync(peer, server.GetStream());
                }
            }
            catch (Exception ex) { Console.WriteLine($"peer bridge closed: {ex.Message}"); }
        });
        await Task.Delay(Timeout.Infinite); // run until killed
    }

    // JOIN over Steam: same shape as `join`, but the outbound hop is the relay. One
    // fresh relay connection per game TCP connection, mirroring the host side.
    static async Task SteamJoinAsync(ulong hostSteamId)
    {
        using var relay = new SteamRelay();
        relay.Init();
        var l = new TcpListener(IPAddress.Loopback, GamePort);
        l.Start();
        Console.WriteLine($"joined host {hostSteamId}. Launch the game (settings.ini IP=127.0.0.1).");
        while (true)
        {
            var game = await l.AcceptTcpClientAsync();
            game.NoDelay = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    using (game)
                    using (var peer = await relay.ConnectAsync(hostSteamId))
                        await PumpAsync(game.GetStream(), peer);
                }
                catch (Exception ex) { Console.WriteLine($"game bridge closed: {ex.Message}"); }
            });
        }
    }

    // List live hosts from the directory. Pure Firebase, no Steam needed.
    static async Task<int> HostsAsync()
    {
        var meta = new MetaClient();
        if (!meta.Enabled) { Console.WriteLine("directory disabled: set PTO_FIREBASE_URL"); return 1; }
        var hosts = await meta.ListHostsAsync();
        if (hosts.Count == 0) { Console.WriteLine("no live hosts"); return 0; }
        for (int i = 0; i < hosts.Count; i++)
            Console.WriteLine($"  [{i + 1}] {hosts[i].name}  ({hosts[i].steamId})");
        return 0;
    }

    // Pick a host from the directory (replacing the manual SteamID paste), then join.
    static async Task<int> PlayAsync()
    {
        var meta = new MetaClient();
        ulong id;
        if (meta.Enabled)
        {
            var hosts = await meta.ListHostsAsync();
            if (hosts.Count == 0) { Console.WriteLine("no live hosts in the directory"); return 1; }
            for (int i = 0; i < hosts.Count; i++)
                Console.WriteLine($"  [{i + 1}] {hosts[i].name}  ({hosts[i].steamId})");
            Console.Write("pick a host #: ");
            if (!int.TryParse(Console.ReadLine(), out int pick) || pick < 1 || pick > hosts.Count)
            { Console.WriteLine("invalid pick"); return 1; }
            id = ulong.Parse(hosts[pick - 1].steamId);
        }
        else
        {
            Console.Write("directory disabled. enter host SteamID64: ");
            if (!ulong.TryParse(Console.ReadLine(), out id)) { Console.WriteLine("invalid id"); return 1; }
        }
        await SteamJoinAsync(id);
        return 0;
    }

    // HOST only: tail the server's matches.txt ("ts|winner|loser", one per finished
    // human match; winner/loser are SteamID64s in the Steam build) and push each new
    // result to the shared ladder. A local line cursor makes restarts skip processed
    // lines, so each match counts exactly once. No server change needed.
    static async Task WatchMatchesAsync(MetaClient meta)
    {
        string dataDir = Environment.GetEnvironmentVariable("PTO_SERVER_DATA") ?? "data";
        string matchesFile = Path.Combine(dataDir, "matches.txt");
        string cursorFile = Path.Combine(dataDir, "ranked_cursor.txt");
        int done = 0;
        try { if (File.Exists(cursorFile) && int.TryParse(File.ReadAllText(cursorFile).Trim(), out int c)) done = c; }
        catch { /* start from 0 */ }

        while (true)
        {
            try
            {
                if (File.Exists(matchesFile))
                {
                    var lines = File.ReadAllLines(matchesFile);
                    if (done > lines.Length) done = 0; // file was reset
                    for (; done < lines.Length; done++)
                    {
                        var f = lines[done].Split('|');
                        if (f.Length >= 3 && ulong.TryParse(f[1], out var w) && ulong.TryParse(f[2], out var l))
                        {
                            await meta.AddResultAsync(w, l);
                            Console.WriteLine($"ladder: {w} beat {l}");
                        }
                    }
                    try { File.WriteAllText(cursorFile, done.ToString()); } catch { /* retry next tick */ }
                }
            }
            catch (Exception ex) { Console.WriteLine($"ladder watch error: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    static async Task<int> PrintLadderAsync()
    {
        var meta = new MetaClient();
        if (!meta.Enabled) { Console.WriteLine("ladder disabled: set PTO_FIREBASE_URL"); return 1; }
        var ladder = await meta.LadderAsync();
        if (ladder.Count == 0) { Console.WriteLine("ladder empty"); return 0; }
        foreach (var (id, row) in ladder)
            Console.WriteLine($"  rank {MetaClient.RankFromCounts(row.wins, row.losses),2}  {id}  ({row.wins}W {row.losses}L)");
        return 0;
    }

    // A local HttpListener standing in for Firebase RTDB (as the TCP demo stands in
    // for the Steam relay). Handles /coll.json (children object) and /coll/id.json
    // (one child) for GET/PUT/DELETE, keyed "coll/id".
    static (HttpListener http, CancellationTokenSource cts, string baseUrl) StartFakeRtdb(int port)
    {
        var store = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await http.GetContextAsync(); } catch { break; }
                string path = ctx.Request.Url!.AbsolutePath.TrimStart('/'); // coll.json or coll/id.json
                if (path.EndsWith(".json")) path = path[..^5];
                string body; using (var r = new StreamReader(ctx.Request.InputStream)) body = await r.ReadToEndAsync();
                string outp = "null";
                if (!path.Contains('/')) // collection
                {
                    if (ctx.Request.HttpMethod == "GET")
                    {
                        var kids = store.Where(kv => kv.Key.StartsWith(path + "/"))
                                        .Select(kv => $"\"{kv.Key[(path.Length + 1)..]}\":{kv.Value}").ToList();
                        if (kids.Count > 0) outp = "{" + string.Join(",", kids) + "}";
                    }
                }
                else // single child
                {
                    if (ctx.Request.HttpMethod == "PUT") { store[path] = body; outp = body; }
                    else if (ctx.Request.HttpMethod == "DELETE") store.TryRemove(path, out _);
                    else if (ctx.Request.HttpMethod == "GET") store.TryGetValue(path, out outp!);
                    outp ??= "null";
                }
                var buf = Encoding.UTF8.GetBytes(outp);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(buf);
                ctx.Response.Close();
            }
        });
        return (http, cts, $"http://127.0.0.1:{port}");
    }

    // Self-check: presence round-trip, stale filtering, and unpublish, via fake RTDB.
    static async Task<int> MetaDemoAsync()
    {
        var (http, cts, baseUrl) = StartFakeRtdb(52050);
        var meta = new MetaClient(baseUrl);
        await meta.PublishHostAsync(111, "Alice"); // fresh (ts = now)
        long staleTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100;
        using (var c = new StringContent($"{{\"steamId\":\"222\",\"name\":\"Bob\",\"ts\":{staleTs}}}",
                                         Encoding.UTF8, "application/json"))
            await new HttpClient().PutAsync($"{baseUrl}/hosts/222.json", c);

        var live = await meta.ListHostsAsync(20);
        bool ok1 = live.Count == 1 && live[0].steamId == "111";
        await meta.UnpublishHostAsync(111);
        bool ok2 = (await meta.ListHostsAsync(20)).Count == 0;
        cts.Cancel(); http.Stop();

        bool ok = ok1 && ok2;
        Console.WriteLine(ok
            ? "metademo OK: stale host filtered, fresh host listed, unpublish clears it"
            : $"metademo FAIL: afterPublish={live.Count} (want 1x111)");
        return ok ? 0 : 1;
    }

    // Self-check: ranked counts accumulate host-independently and rank derives right.
    static async Task<int> RankedDemoAsync()
    {
        var (http, cts, baseUrl) = StartFakeRtdb(52051);
        var meta = new MetaClient(baseUrl);
        await meta.AddResultAsync(111, 222); // 111 wins
        await meta.AddResultAsync(111, 333); // 111 wins again (a different host would do this too)

        var ladder = await meta.LadderAsync();
        cts.Cancel(); http.Stop();

        var top = ladder.Count > 0 ? ladder[0] : ("", new RankRow());
        // 111: 2W 0L -> rank 23 and top of the ladder; 222/333: 1L -> rank 26.
        bool ok = top.Item1 == "111" && top.Item2.wins == 2 && top.Item2.losses == 0
                  && MetaClient.RankFromCounts(2, 0) == 23 && ladder.Count == 3;
        Console.WriteLine(ok
            ? "rankeddemo OK: winner 2W0L at rank 23 tops a 3-player ladder"
            : $"rankeddemo FAIL: top={top.Item1} {top.Item2.wins}W{top.Item2.losses}L, count={ladder.Count}");
        return ok ? 0 : 1;
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
