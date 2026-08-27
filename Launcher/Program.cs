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
    const int GamePort = 51338;  // loopback port the game dials (settings.ini IP=127.0.0.1); the launcher proxy
    const int LocalPort = 51339; // the local PtoServer sits here; the proxy forwards GamePort -> here
    const byte OpQueue = 0;      // client -> server: join online matchmaking (the "go online" trigger)
    const byte OpBattleEnd = 3;  // server -> client: battle finished (the "match over" signal)

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
            case "sniffdemo": // Op.Queue detection across chunk boundaries (pure)
                return SniffDemo();
            case "rankeddemo": // ranked accumulation self-check (fake RTDB)
                return await RankedDemoAsync();
            default:
                Console.WriteLine("usage: ptolaunch play | check | ladder | demo | queuedemo | matchdemo | sniffdemo | rankeddemo");
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

    // Offline-first. The launcher always runs a local server and proxies the game to
    // it, so login, deckbuilding, and the campaign work with no internet and no
    // broadcasting. It watches the game -> server stream and only when the game sends
    // Op.Queue (the player chose online matchmaking) does it go online. The campaign
    // (Op.StartStage) is a local bot battle, so it never trips this.
    static async Task<int> PlayAsync()
    {
        var meta = new MetaClient(); // only used once the player goes online
        if (!await EnsureServerAsync()) return 1;
        Console.WriteLine("offline: local server ready. Login, deckbuilding, and campaign work with no internet.");
        Console.WriteLine("point the game at 127.0.0.1 and play. Choose online matchmaking in-game to find a match.");

        var online = new OnlineState();
        var l = new TcpListener(IPAddress.Loopback, GamePort);
        l.Start();
        while (true)
        {
            var game = await l.AcceptTcpClientAsync();
            game.NoDelay = true;
            _ = HandleGameAsync(game, meta, online);
        }
    }

    // Route one game connection. Once we've been elected the guest, the game's
    // (reconnected) session is tunneled to the authority over the relay. Otherwise it
    // goes to the local server, with the game -> server side sniffed for Op.Queue.
    static async Task HandleGameAsync(TcpClient game, MetaClient meta, OnlineState online)
    {
        try
        {
            if (online.GuestAuthority != 0)
            {
                using (game)
                using (var peer = await online.Relay!.ConnectAsync(online.GuestAuthority))
                    await GuestBridgeAsync(game, peer, online);
                return;
            }
            using (game)
            using (var server = new TcpClient())
            {
                await server.ConnectAsync(IPAddress.Loopback, LocalPort);
                server.NoDelay = true;
                var sniff = new OpcodeSniffer(OpQueue, () => _ = GoOnlineAsync(meta, online, game));
                await PumpSniffAsync(game.GetStream(), server.GetStream(), sniff);
            }
        }
        catch (Exception ex) { Console.WriteLine($"game session ended: {ex.Message}"); }
    }

    // Fired once, when the game asks to matchmake. Brings up Steam + Firebase, pairs,
    // and elects. The authority hosts on its own local server (its game is already
    // queued there). The guest must reconnect onto the authority (the fixed client
    // cannot move a live session), so we drop its connection and route the reconnect
    // to the relay.
    static async Task GoOnlineAsync(MetaClient meta, OnlineState online, TcpClient game)
    {
        if (!online.Begin()) return; // once per match (Reset re-arms it after the match ends)
        if (!meta.Enabled) { Console.WriteLine("online needs Firebase (firebase.txt); staying offline"); return; }
        SteamRelay relay;
        if (online.Relay != null) relay = online.Relay; // reuse across matches
        else
        {
            try { relay = new SteamRelay(); relay.Init(); }
            catch (Exception ex) { Console.WriteLine($"online needs Steam running: {ex.Message}"); return; }
            online.Relay = relay;
        }
        ulong me = relay.MySteamId;
        Console.WriteLine($"online: matchmaking as {relay.MyName} ({me})...");

        while (true)
        {
            await meta.EnqueueAsync(me, relay.MyName);
            var queue = await meta.ListQueueAsync();
            ulong partner = ElectPartner(queue.Select(q => ulong.TryParse(q.steamId, out var v) ? v : 0), me);
            if (partner == 0) { await Task.Delay(2000); continue; }
            string key = PairKey(me, partner);

            if (me < partner)
            {
                // Authority: our local server is the match server, and our game's Op.Queue
                // already queued us there. Just bridge the guest in over the relay.
                await meta.SetMatchAsync(key, me);
                Console.WriteLine($"matched with {partner}; you are hosting. Waiting for opponent to join...");
                RunAuthorityRelay(relay, meta, me, key);
                return;
            }

            ulong confirmed = await meta.GetMatchAuthorityAsync(key);
            if (confirmed != partner) { await Task.Delay(1500); continue; }
            await meta.DequeueAsync(me);
            online.GuestAuthority = partner; // route the reconnect to the relay
            Console.WriteLine($"matched with {partner}; joining their game.");
            Console.WriteLine(">> Restart the game when it says 'disconnected', then choose online matchmaking again to enter the match.");
            try { game.Close(); } catch { } // drop the local session so the client reconnects via the relay
            return;
        }
    }

    // Guest online session: pump game <-> relay, watching the relay -> game side for
    // Op.BattleEnd. When the match ends we forward the result, then drop the connection
    // and re-arm; the client reconnects and (GuestAuthority now cleared) lands back on
    // its own local server, offline. Firebase is updated by the authority, not here.
    static async Task GuestBridgeAsync(TcpClient game, SteamConnectionStream peer, OnlineState online)
    {
        using var cts = new CancellationTokenSource();
        var g = game.GetStream();
        var endSniff = new OpcodeSniffer(OpBattleEnd, () =>
        {
            Console.WriteLine("match over; returning to offline. Restart the game to reconnect to your local server.");
            online.Reset();
            cts.Cancel();
        });
        var t1 = CopyAsync(g, peer, cts);                 // game -> relay (plain)
        var t2 = SniffCopyAsync(peer, g, endSniff, cts);  // relay -> game (watch for battle end)
        await Task.WhenAny(t1, t2);
        cts.Cancel();
        try { await Task.WhenAll(t1, t2); } catch { /* expected on teardown */ }
    }

    // Authority: accept relay peers and bridge each to the LOCAL server; feed the ladder.
    // Keep advertising in the queue until the guest connects, then leave.
    static void RunAuthorityRelay(SteamRelay relay, MetaClient meta, ulong me, string key)
    {
        _ = WatchMatchesAsync(meta);
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
                    await server.ConnectAsync(IPAddress.Loopback, LocalPort);
                    server.NoDelay = true;
                    await PumpAsync(peer, server.GetStream());
                }
            }
            catch (Exception ex) { Console.WriteLine($"peer bridge closed: {ex.Message}"); }
        });
    }

    // Bidirectional pump where the game -> server direction is teed through a sniffer.
    static async Task PumpSniffAsync(Stream game, Stream server, OpcodeSniffer sniff)
    {
        using var cts = new CancellationTokenSource();
        var t1 = SniffCopyAsync(game, server, sniff, cts);
        var t2 = CopyAsync(server, game, cts);
        await Task.WhenAny(t1, t2);
        cts.Cancel();
        try { await Task.WhenAll(t1, t2); } catch { /* expected on teardown */ }
    }

    static async Task SniffCopyAsync(Stream from, Stream to, OpcodeSniffer sniff, CancellationTokenSource cts)
    {
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token)) > 0)
            {
                await to.WriteAsync(buf.AsMemory(0, n), cts.Token);
                sniff.Feed(buf, n);
            }
        }
        finally { cts.Cancel(); }
    }

    // Ensure the local PtoServer is accepting on LocalPort: connect if up, else spawn
    // PtoServer.exe (PTO_SERVER_EXE, default "PtoServer.exe") with --port and wait.
    static async Task<bool> EnsureServerAsync()
    {
        if (await TryConnectServerAsync(LocalPort)) return true;
        string exe = Environment.GetEnvironmentVariable("PTO_SERVER_EXE") ?? "PtoServer.exe";
        if (TrySpawnServer(exe, LocalPort) == null) return false;
        for (int i = 0; i < 20; i++) { if (await TryConnectServerAsync(LocalPort)) return true; await Task.Delay(500); }
        Console.WriteLine($"server did not come up on 127.0.0.1:{LocalPort}");
        return false;
    }

    static System.Diagnostics.Process? TrySpawnServer(string exe, int port)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
            var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
            if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            return System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Console.WriteLine($"could not start server '{exe}': {ex.Message}"); return null; }
    }

    static async Task<bool> TryConnectServerAsync(int port)
    {
        try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); return true; }
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
        if (await TryConnectServerAsync(LocalPort)) Console.WriteLine($"[ ok ] Server: already listening on 127.0.0.1:{LocalPort}");
        else if (!File.Exists(exe)) { Console.WriteLine($"[FAIL] Server: '{exe}' not found (set PTO_SERVER_EXE)"); ok = false; }
        else
        {
            // Actually launch it: a present-but-unrunnable server (e.g. missing .NET
            // runtime) must fail here, not silently mid-match.
            var proc = TrySpawnServer(exe, LocalPort);
            if (proc == null) { Console.WriteLine("[FAIL] Server: could not start (see error above)"); ok = false; }
            else
            {
                bool up = false;
                for (int i = 0; i < 16 && !up; i++) { if (await TryConnectServerAsync(LocalPort)) up = true; else await Task.Delay(250); }
                Console.WriteLine(up
                    ? $"[ ok ] Server: starts and binds 127.0.0.1:{LocalPort}"
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

    // Self-check: the sniffer fires once on Op.Queue and ignores login + campaign, even
    // when packets are split across read boundaries (5-byte chunks here).
    static int SniffDemo()
    {
        static byte[] Frame(byte op, int payloadLen)
        {
            var p = new byte[7 + payloadLen];
            p[0] = op;
            BitConverter.GetBytes((ushort)1374).CopyTo(p, 1);
            BitConverter.GetBytes((uint)(7 + payloadLen)).CopyTo(p, 3);
            return p;
        }
        int fired = 0;
        var sniff = new OpcodeSniffer(OpQueue, () => fired++);
        var stream = new List<byte>();
        stream.AddRange(Frame(46, 20)); // Op.Login   - ignore
        stream.AddRange(Frame(55, 3));  // Op.StartStage (campaign) - ignore
        stream.AddRange(Frame(0, 1));   // Op.Queue   - the trigger
        stream.AddRange(Frame(7, 4));   // a later packet - must not double-fire
        var all = stream.ToArray();
        for (int off = 0; off < all.Length; off += 5)
        {
            int n = Math.Min(5, all.Length - off);
            var chunk = new byte[n];
            Array.Copy(all, off, chunk, 0, n);
            sniff.Feed(chunk, n);
        }
        bool ok = fired == 1;
        Console.WriteLine(ok
            ? "sniffdemo OK: fired once on Op.Queue, ignored login + campaign, no double-fire"
            : $"sniffdemo FAIL: fired {fired} times (want 1)");
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

// Shared state for a single launcher session's transition from offline to online.
sealed class OnlineState
{
    int _begun;
    public SteamRelay? Relay;    // created on first online use, reused across matches
    public ulong GuestAuthority; // set once we've been elected guest; then game reconnects route to the relay
    public bool Begin() => Interlocked.Exchange(ref _begun, 1) == 0; // go online at most once per match
    public void Reset() { GuestAuthority = 0; Interlocked.Exchange(ref _begun, 0); } // after a match: back offline, re-armed
}

// Watches a framed client->server byte stream (transparent: the caller still forwards
// every byte) and fires ONCE when a packet with the target opcode appears. Framing is
// [id u8][key u16][size u32 total incl 7-byte header][payload]; an accumulator handles
// packets split across reads. Client->server packets are small, so buffering is cheap.
sealed class OpcodeSniffer
{
    readonly byte _target;
    readonly Action _onSeen;
    byte[] _buf = Array.Empty<byte>();
    int _len;
    bool _fired;

    public OpcodeSniffer(byte targetOpcode, Action onSeen) { _target = targetOpcode; _onSeen = onSeen; }

    public void Feed(byte[] data, int count)
    {
        if (_fired || count <= 0) return;
        Append(data, count);
        int off = 0;
        while (_len - off >= 7)
        {
            uint size = BitConverter.ToUInt32(_buf, off + 3);
            if (size < 7) { off++; continue; }      // resync on garbage (shouldn't happen on a valid stream)
            if (_len - off < size) break;            // packet not complete yet
            if (_buf[off] == _target) { _fired = true; _onSeen(); break; }
            off += (int)size;
        }
        if (off > 0 && !_fired) { Array.Copy(_buf, off, _buf, 0, _len - off); _len -= off; }
    }

    void Append(byte[] data, int count)
    {
        if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_len + count, _buf.Length * 2 + 256));
        Array.Copy(data, 0, _buf, _len, count);
        _len += count;
    }
}
