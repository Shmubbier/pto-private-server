using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PtoLauncher;

// PTO Steam P2P launcher (see NETWORKING.md). The game speaks raw TCP framed
// [id u8][key u16][size u32][payload] and never learns Steam exists: the launcher
// points it at 127.0.0.1 and tunnels the bytes over Steam's relay.
//
// `play` is symmetric: there are no host/joiner roles. Both peers register in the
// match queue; when two are paired, the LOWER SteamID is silently elected the
// authority and runs its local PtoServer, and the other connects to it over the
// relay. Both peers compute the same pairing and election from the same queue
// snapshot, so no negotiation. Bridging itself is still "accept here, connect
// there, pump bytes" (ServeAsync / PumpAsync), unchanged.
static class Program
{
    const int GamePort = 51338; // loopback port the game dials (settings.ini IP=127.0.0.1)

    static async Task<int> Main(string[] args)
    {
        switch (args.Length > 0 ? args[0] : "")
        {
            case "play": // symmetric matchmaking: queue, pair, auto-elect authority
                return await PlayAsync();
            case "check": // preflight: Steam + Firebase + server, one line each
                return await CheckAsync();
            case "ladder": // print the shared ranked ladder (no Steam needed)
                return await PrintLadderAsync();
            case "demo": // transport self-check (in-process, no Steam)
                return await DemoAsync();
            case "queuedemo": // queue enqueue/list/dequeue self-check (fake RTDB)
                return await QueueDemoAsync();
            case "matchdemo": // deterministic pairing/election self-check (pure)
                return MatchDemo();
            case "rankeddemo": // ranked accumulation self-check (fake RTDB)
                return await RankedDemoAsync();
            default:
                Console.WriteLine("usage: ptolaunch play | check | ladder | demo | queuedemo | matchdemo | rankeddemo");
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

    // Deterministic symmetric pairing: sort fresh queue ids ascending and pair
    // adjacent (0-1, 2-3, ...). Returns my partner, or 0 if I'm alone or the odd one
    // out. Both peers of a pair compute the same partner from the same snapshot.
    internal static ulong ElectPartner(IEnumerable<ulong> queueIds, ulong me)
    {
        var ids = queueIds.Where(x => x != 0).Distinct().ToList();
        if (!ids.Contains(me)) return 0;
        ids.Sort();
        int i = ids.IndexOf(me);
        int j = (i % 2 == 0) ? i + 1 : i - 1;
        return (j >= 0 && j < ids.Count) ? ids[j] : 0;
    }

    static string PairKey(ulong a, ulong b) => $"{Math.Min(a, b)}_{Math.Max(a, b)}";

    // Symmetric matchmaking. No host/joiner: register in the queue, pair, and the
    // lower SteamID is silently elected the authority (runs its local PtoServer);
    // the other connects to it over the relay. Same UX on both machines.
    static async Task<int> PlayAsync()
    {
        var meta = new MetaClient();
        if (!meta.Enabled) { Console.WriteLine("matchmaking needs PTO_FIREBASE_URL (or firebase.txt)"); return 1; }
        using var relay = new SteamRelay();
        relay.Init();
        ulong me = relay.MySteamId;
        Console.WriteLine($"looking for a match as {relay.MyName} ({me})...");
        Console.CancelKeyPress += (_, _) => meta.DequeueAsync(me).Wait(2000);

        while (true)
        {
            await meta.EnqueueAsync(me, relay.MyName); // heartbeat "looking for match"
            var queue = await meta.ListQueueAsync();
            ulong partner = ElectPartner(queue.Select(q => ulong.TryParse(q.steamId, out var v) ? v : 0), me);
            if (partner == 0) { await Task.Delay(2000); continue; }

            string key = PairKey(me, partner);
            if (me < partner)
            {
                // Elected authority. Announce it so the partner can confirm reciprocity.
                // Stay in the queue (RunAuthorityAsync keeps heartbeating) until the guest
                // connects, otherwise the guest could no longer see us to derive the pair.
                await meta.SetMatchAsync(key, me);
                Console.WriteLine($"matched with {partner}; elected authority, running local server");
                if (!await EnsureServerAsync()) { await meta.ClearMatchAsync(key); await meta.DequeueAsync(me); return 1; }
                await RunAuthorityAsync(relay, meta, me, key);
                return 0;
            }
            else
            {
                // Wait for the authority to confirm THIS pair before committing (closes
                // the snapshot-skew race: if it chose someone else, re-poll).
                ulong confirmed = await meta.GetMatchAuthorityAsync(key);
                if (confirmed != partner) { await Task.Delay(1500); continue; }
                await meta.DequeueAsync(me);
                Console.WriteLine($"matched with {partner}; connecting to authority over relay");
                await RunGuestAsync(relay, partner);
                return 0;
            }
        }
    }

    // Authority: accept relay peers, bridge each to the local server, feed the ladder.
    // The authority's own game connects to 127.0.0.1:51338 directly (not proxied).
    static async Task RunAuthorityAsync(SteamRelay relay, MetaClient meta, ulong me, string key)
    {
        _ = WatchMatchesAsync(meta); // push finished matches to the shared ladder

        // Keep advertising in the queue so the guest can still derive the pair; stop and
        // leave the queue once the guest actually connects over the relay.
        var hbCts = new CancellationTokenSource();
        int guestArrived = 0;
        _ = Task.Run(async () =>
        {
            while (!hbCts.IsCancellationRequested)
            {
                try { await meta.EnqueueAsync(me, relay.MyName); } catch { }
                try { await Task.Delay(3000, hbCts.Token); } catch { }
            }
        });

        relay.Listen(async peer =>
        {
            if (Interlocked.Exchange(ref guestArrived, 1) == 0)
            {
                hbCts.Cancel();
                await meta.DequeueAsync(me);
                await meta.ClearMatchAsync(key);
            }
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
        Console.WriteLine("ready. point the game at 127.0.0.1 and play.");
        await Task.Delay(Timeout.Infinite); // ponytail: no idle timeout; Ctrl+C to stop
    }

    // Guest: local game -> relay -> authority. One relay connection per game TCP conn.
    static async Task RunGuestAsync(SteamRelay relay, ulong authority)
    {
        var l = new TcpListener(IPAddress.Loopback, GamePort);
        l.Start();
        Console.WriteLine("ready. point the game at 127.0.0.1 and play.");
        while (true)
        {
            var game = await l.AcceptTcpClientAsync();
            game.NoDelay = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    using (game)
                    using (var peer = await relay.ConnectAsync(authority))
                        await PumpAsync(game.GetStream(), peer);
                }
                catch (Exception ex) { Console.WriteLine($"game bridge closed: {ex.Message}"); }
            });
        }
    }

    // Ensure a local PtoServer is accepting on 51338: connect if it's up, else spawn
    // PtoServer.exe (PTO_SERVER_EXE, default "PtoServer.exe") and wait for the port.
    static async Task<bool> EnsureServerAsync()
    {
        if (await TryConnectServerAsync()) return true;
        string exe = Environment.GetEnvironmentVariable("PTO_SERVER_EXE") ?? "PtoServer.exe";
        if (TrySpawnServer(exe) == null) return false;
        for (int i = 0; i < 20; i++) { if (await TryConnectServerAsync()) return true; await Task.Delay(500); }
        Console.WriteLine("server did not come up on 127.0.0.1:51338");
        return false;
    }

    static System.Diagnostics.Process? TrySpawnServer(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
            if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            return System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Console.WriteLine($"could not start server '{exe}': {ex.Message}"); return null; }
    }

    static async Task<bool> TryConnectServerAsync()
    {
        try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, GamePort); return true; }
        catch { return false; }
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

    // Preflight so a tester sees "Steam ok / Firebase ok / server ok" up front
    // instead of a silent failure mid-match. Each line is independent.
    static async Task<int> CheckAsync()
    {
        bool ok = true;

        var meta = new MetaClient();
        if (!meta.Enabled) { Console.WriteLine("[FAIL] Firebase: no URL (put it in firebase.txt or set PTO_FIREBASE_URL)"); ok = false; }
        else
        {
            try { var q = await meta.ListQueueAsync(); Console.WriteLine($"[ ok ] Firebase: reachable ({q.Count} in queue)"); }
            catch (Exception ex) { Console.WriteLine($"[FAIL] Firebase: {FirstLine(ex)}"); ok = false; }
        }

        try
        {
            using var relay = new SteamRelay();
            relay.Init();
            Console.WriteLine($"[ ok ] Steam: signed in as {relay.MyName} ({relay.MySteamId})");
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] Steam: {FirstLine(ex)}"); ok = false; }

        string exe = Environment.GetEnvironmentVariable("PTO_SERVER_EXE") ?? "PtoServer.exe";
        if (await TryConnectServerAsync()) Console.WriteLine("[ ok ] Server: already listening on 127.0.0.1:51338");
        else if (!File.Exists(exe)) { Console.WriteLine($"[FAIL] Server: '{exe}' not found (set PTO_SERVER_EXE)"); ok = false; }
        else
        {
            // Actually launch it: a present-but-unrunnable server (e.g. missing .NET
            // runtime) must fail here, not silently mid-match.
            var proc = TrySpawnServer(exe);
            if (proc == null) { Console.WriteLine("[FAIL] Server: could not start (see error above)"); ok = false; }
            else
            {
                bool up = false;
                for (int i = 0; i < 16 && !up; i++) { if (await TryConnectServerAsync()) up = true; else await Task.Delay(250); }
                Console.WriteLine(up
                    ? "[ ok ] Server: starts and binds 127.0.0.1:51338"
                    : "[FAIL] Server: present but did not bind (is the .NET 10 runtime installed?)");
                ok &= up;
                try { proc.Kill(true); } catch { }
            }
        }

        Console.WriteLine(ok ? "\nAll good. Run play.bat to matchmake." : "\nSome checks failed (see above).");
        return ok ? 0 : 1;
    }

    static string FirstLine(Exception ex) => ex.Message.Split('\n')[0].Trim();

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

    // Self-check: queue round-trip, stale filtering, and dequeue, via fake RTDB.
    static async Task<int> QueueDemoAsync()
    {
        var (http, cts, baseUrl) = StartFakeRtdb(52050);
        var meta = new MetaClient(baseUrl);
        await meta.EnqueueAsync(111, "Alice"); // fresh (ts = now)
        long staleTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100;
        using (var c = new StringContent($"{{\"steamId\":\"222\",\"name\":\"Bob\",\"ts\":{staleTs}}}",
                                         Encoding.UTF8, "application/json"))
            await new HttpClient().PutAsync($"{baseUrl}/queue/222.json", c);

        var live = await meta.ListQueueAsync(20);
        bool ok1 = live.Count == 1 && live[0].steamId == "111";
        await meta.DequeueAsync(111);
        bool ok2 = (await meta.ListQueueAsync(20)).Count == 0;
        cts.Cancel(); http.Stop();

        bool ok = ok1 && ok2;
        Console.WriteLine(ok
            ? "queuedemo OK: stale entry filtered, fresh entry listed, dequeue clears it"
            : $"queuedemo FAIL: afterEnqueue={live.Count} (want 1x111)");
        return ok ? 0 : 1;
    }

    // Self-check (pure): from one consistent queue snapshot, paired peers agree on
    // each other and on the authority (lower id); the odd one out is unpaired.
    static int MatchDemo()
    {
        var q4 = new ulong[] { 400, 100, 300, 200 }; // unsorted on purpose
        bool pairA = ElectPartner(q4, 100) == 200 && ElectPartner(q4, 200) == 100; // reciprocal
        bool pairB = ElectPartner(q4, 300) == 400 && ElectPartner(q4, 400) == 300;
        bool authA = Math.Min(100ul, 200ul) == 100 && Math.Min(300ul, 400ul) == 300;

        var q3 = new ulong[] { 100, 200, 300 };
        bool odd = ElectPartner(q3, 300) == 0 && ElectPartner(q3, 100) == 200;
        bool alone = ElectPartner(new ulong[] { 100 }, 100) == 0;

        bool ok = pairA && pairB && authA && odd && alone;
        Console.WriteLine(ok
            ? "matchdemo OK: pairs reciprocal, authority = lower id, odd one out waits"
            : $"matchdemo FAIL: pairA={pairA} pairB={pairB} authA={authA} odd={odd} alone={alone}");
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
