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
    const byte OpLogin = 46;     // client -> server: login (captured to replay onto the host)
    const byte OpLoaded = 48;    // server -> client: login flow done, door open -> lobby

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
        EnsureClientLocal();          // point the Steam client's real config at us
        _ = KeepClientLocalAsync();   // keep it there (the game rewrites it on exit)
        Console.WriteLine("offline: local server ready. Login, deckbuilding, and campaign work with no internet.");
        Console.WriteLine("point the game at 127.0.0.1 and play. Choose online matchmaking in-game to find a match.");

        var online = new OnlineState();
        // Warm the Steam relay NOW so its SDR route is ready by the time a match happens.
        // Connecting cold causes 5008 rendezvous timeouts, badly so behind CGNAT where the
        // relay is the only viable path. Non-fatal: offline play doesn't need it.
        try { var r = new SteamRelay(); r.Init(); online.Relay = r; Console.WriteLine("steam: relay warming up (ready by match time)"); }
        catch (Exception ex) { Console.WriteLine($"steam: relay not started yet ({ex.Message}); will init on first match"); }

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
                {
                    // Wait for the SDR relay route to be ready before connecting (else 5008).
                    for (int i = 0; i < 30 && !online.Relay!.RelayReady(); i++)
                    {
                        if (i == 0) Console.WriteLine("relay: waiting for the Steam relay network to be ready...");
                        await Task.Delay(500);
                    }
                    // The relay route or the host's listen socket may not be ready the instant
                    // the guest reconnects; retry a few times before giving up.
                    SteamConnectionStream? peer = null;
                    for (int attempt = 1; attempt <= 4 && peer == null; attempt++)
                    {
                        Console.WriteLine($"relay: connecting to host {online.GuestAuthority} (attempt {attempt}/4)...");
                        try { peer = await online.Relay!.ConnectAsync(online.GuestAuthority); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  attempt {attempt} failed: {ex.Message}");
                            if (attempt < 4) await Task.Delay(2000);
                        }
                    }
                    if (peer == null)
                    {
                        Console.WriteLine("could not reach the host over the relay. Restart the game and pick matchmaking again to retry.");
                        return;
                    }
                    using (peer) await GuestBridgeAsync(game, peer, online);
                }
                return;
            }
            using (game)
            {
                var server = new TcpClient();
                await server.ConnectAsync(IPAddress.Loopback, LocalPort);
                server.NoDelay = true;
                using var localCts = new CancellationTokenSource();

                // Capture the game's login (op46) and matchmaking (op0) so we can replay them onto
                // the authority and hand the LIVE connection over with no visible disconnect.
                var loginSniff = new OpcodeSniffer(OpLogin, pkt => online.LoginPacket = pkt, repeat: true);
                var handoff = new TaskCompletionSource<SteamConnectionStream?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var queueSniff = new OpcodeSniffer(OpQueue, pkt => { online.QueuePacket = pkt; _ = GoOnlineAsync(meta, online, game, handoff); });
                online.QueueSniff = queueSniff;
                // server -> game: on Op.BattleEnd, if we were hosting, re-arm and go passive offline.
                var endSniff = new OpcodeSniffer(OpBattleEnd, _ =>
                {
                    if (online.EndHosting())
                    {
                        queueSniff.Rearm();
                        Console.WriteLine("match over; back to passive offline, ready for another match.");
                    }
                }, repeat: true);

                var pump = PumpDualSniffAsync(game.GetStream(), server.GetStream(),
                                              new[] { loginSniff, queueSniff }, new[] { endSniff }, localCts);
                var done = await Task.WhenAny(pump, handoff.Task);
                if (done == handoff.Task && handoff.Task.Result is SteamConnectionStream peer)
                {
                    // SEAMLESS: stop the local pump, drop the local server, and splice the SAME game
                    // connection to the authority over the relay - no disconnect, no scary message.
                    localCts.Cancel();
                    try { await pump; } catch { }
                    try { server.Close(); } catch { }
                    Console.WriteLine("joined - handed your live session to the host, no reconnect needed.");
                    using (peer) await GuestBridgeAsync(game, peer, online);
                    return;
                }
                try { server.Close(); } catch { }
                await pump; // normal offline / authority session end
            }
        }
        catch (Exception ex) { Console.WriteLine($"game session ended: {ex.Message}"); }
    }

    // Fired once, when the game asks to matchmake. Brings up Steam + Firebase, pairs,
    // and elects. The authority hosts on its own local server (its game is already
    // queued there). The guest must reconnect onto the authority (the fixed client
    // cannot move a live session), so we drop its connection and route the reconnect
    // to the relay.
    static async Task GoOnlineAsync(MetaClient meta, OnlineState online, TcpClient game, TaskCompletionSource<SteamConnectionStream?> handoff)
    {
        // Fire-and-forget from the sniffer: swallow nothing silently. On any error, re-arm
        // so the player can just try matchmaking again.
        try { await GoOnlineCoreAsync(meta, online, game, handoff); }
        catch (Exception ex)
        {
            Console.WriteLine($"matchmaking stopped: {ex.Message}. Choose matchmaking again to retry.");
            online.CancelHosting();
            handoff.TrySetResult(null); // never leave HandleGameAsync waiting
        }
    }

    static async Task GoOnlineCoreAsync(MetaClient meta, OnlineState online, TcpClient game, TaskCompletionSource<SteamConnectionStream?> handoff)
    {
        if (!online.Begin()) return; // once per match (Reset re-arms it after the match ends)
        if (!meta.Enabled) { Console.WriteLine("online needs Firebase (firebase.txt); staying offline"); return; }
        SteamRelay relay;
        if (online.Relay != null) relay = online.Relay; // reuse across matches
        else
        {
            try { relay = new SteamRelay(); relay.Init(); }
            catch (Exception ex) { Console.WriteLine($"online needs Steam running: {ex.Message}"); online.Reset(); return; }
            online.Relay = relay;
        }
        ulong me = relay.MySteamId;
        Console.WriteLine($"online: matchmaking as {relay.MyName} ({me})...");

        int polls = 0;
        while (true)
        {
            List<QueueEntry> queue;
            try { await meta.EnqueueAsync(me, relay.MyName); queue = await meta.ListQueueAsync(); }
            catch (Exception ex) { Console.WriteLine($"  Firebase unreachable, retrying ({ex.Message})"); await Task.Delay(3000); continue; }

            ulong partner = ElectPartner(queue.Select(q => ulong.TryParse(q.steamId, out var v) ? v : 0), me);
            if (partner == 0)
            {
                int others = queue.Count(q => ulong.TryParse(q.steamId, out var v) && v != me);
                if (polls++ % 5 == 0) // ~every 10s, not every poll
                    Console.WriteLine($"  waiting for an opponent... ({others} other player(s) queued)");
                await Task.Delay(2000);
                continue;
            }
            string key = PairKey(me, partner);

            if (me < partner)
            {
                // Authority: our local server is the match server, and our game's Op.Queue
                // already queued us there. Advertise and bridge the guest in over the relay.
                online.MyId = me; online.MatchKey = key; online.Hosting = true;
                await meta.SetMatchAsync(key, me);
                Console.WriteLine($"matched with {partner} - you're the host. Waiting for them to join...");
                EnsureAuthorityListening(relay, meta, online); // once per session
                StartAdvertise(relay, meta, online);           // per match, with a timeout, until the guest connects
                return;
            }

            ulong confirmed = await meta.GetMatchAuthorityAsync(key);
            if (confirmed != partner)
            {
                if (polls++ % 3 == 0) Console.WriteLine($"  matched with {partner}; waiting for host to confirm...");
                await Task.Delay(1500);
                continue;
            }
            await meta.DequeueAsync(me);
            Console.WriteLine($"matched with {partner} - connecting you into their game...");
            // Seamless: connect the relay and pre-run the login onto the authority on the game's
            // behalf, so the launcher can splice the LIVE game connection over (HandleGameAsync
            // does the splice). Falls back to a forced reconnect if the seamless join fails.
            try
            {
                var peer = await SeamlessGuestConnectAsync(online, partner);
                handoff.TrySetResult(peer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  seamless join failed ({ex.Message}); falling back to reconnect.");
                online.GuestAuthority = partner;              // route the reconnect to the relay
                Console.WriteLine(">> If the game shows 'disconnected', it will auto-rejoin the match.");
                handoff.TrySetResult(null);
                try { game.Close(); } catch { }               // force the client to reconnect via the relay
            }
            return;
        }
    }

    // Guest seamless join: connect the relay to the authority, replay the captured login onto it
    // and swallow the authority's login-flow responses (the game already logged in to the local
    // server), then replay the matchmaking op so the authority pairs us into the host's match.
    // The returned stream is positioned so its next bytes are the battle setup; HandleGameAsync
    // bridges the live game connection to it, so the player never sees a disconnect.
    static async Task<SteamConnectionStream> SeamlessGuestConnectAsync(OnlineState online, ulong authority)
    {
        if (online.LoginPacket == null || online.QueuePacket == null)
            throw new InvalidOperationException("no captured login/queue to replay");
        for (int i = 0; i < 30 && !online.Relay!.RelayReady(); i++) await Task.Delay(500);
        SteamConnectionStream? peer = null;
        for (int attempt = 1; attempt <= 4 && peer == null; attempt++)
        {
            try { peer = await online.Relay!.ConnectAsync(authority); }
            catch (Exception ex) { Console.WriteLine($"  relay connect attempt {attempt} failed: {ex.Message}"); if (attempt < 4) await Task.Delay(2000); }
        }
        if (peer == null) throw new IOException("could not reach the host over the relay");

        await peer.WriteAsync(online.LoginPacket);
        await SuppressUntilAsync(peer, OpLoaded);   // discard the authority's login flow up to op48
        await peer.WriteAsync(online.QueuePacket);  // queue on the authority -> it pairs us in
        return peer;
    }

    // Read and discard whole framed packets from a stream until (and including) one with `opcode`.
    static async Task SuppressUntilAsync(Stream s, byte opcode)
    {
        var acc = new byte[8192];
        int len = 0;
        var buf = new byte[16384];
        while (true)
        {
            int n = await s.ReadAsync(buf);
            if (n <= 0) throw new IOException("host closed during the login handoff");
            if (len + n > acc.Length) Array.Resize(ref acc, Math.Max(len + n, acc.Length * 2));
            Array.Copy(buf, 0, acc, len, n); len += n;
            int off = 0;
            while (len - off >= 7)
            {
                uint size = BitConverter.ToUInt32(acc, off + 3);
                if (size < 7) { off++; continue; }
                if (len - off < size) break;
                byte op = acc[off];
                off += (int)size;
                if (op == opcode) return; // login flow consumed; the rest of the burst ends here
            }
            if (off > 0) { Array.Copy(acc, off, acc, 0, len - off); len -= off; }
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
        var endSniff = new OpcodeSniffer(OpBattleEnd, _ =>
        {
            Console.WriteLine("match over; returning to offline.");
            online.Reset();
            cts.Cancel();
        });
        var t1 = CopyAsync(g, peer, cts);                     // game -> relay (plain)
        var t2 = SniffCopyAsync(peer, g, cts, endSniff);      // relay -> game (watch for battle end)
        await Task.WhenAny(t1, t2);
        cts.Cancel();
        try { await Task.WhenAll(t1, t2); } catch { /* expected on teardown */ }
    }

    // Set up the relay listener + ladder tail ONCE per session. The listener is match-
    // agnostic: any peer that connects is bridged to the local server, and its arrival
    // ends the current match's queue advertising.
    static void EnsureAuthorityListening(SteamRelay relay, MetaClient meta, OnlineState online)
    {
        if (online.Listening) return;
        online.Listening = true;
        _ = WatchMatchesAsync(meta); // push finished matches to the shared ladder
        relay.Listen(async peer =>
        {
            online.HeartbeatCts?.Cancel();                       // guest arrived: stop advertising this match
            try { await meta.DequeueAsync(online.MyId); } catch { }
            if (online.MatchKey != null) { try { await meta.ClearMatchAsync(online.MatchKey); } catch { } }
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

    // Per match: keep the authority in the queue so the guest can derive the pair, until
    // the guest connects (the listener cancels this) or a timeout gives up.
    const int AuthorityWaitSeconds = 120;
    static void StartAdvertise(SteamRelay relay, MetaClient meta, OnlineState online)
    {
        var cts = new CancellationTokenSource();
        online.HeartbeatCts = cts;
        _ = Task.Run(async () =>
        {
            int elapsed = 0;
            while (!cts.IsCancellationRequested)
            {
                try { await meta.EnqueueAsync(online.MyId, relay.MyName); } catch { }
                try { await Task.Delay(3000, cts.Token); } catch { break; } // cancelled = guest connected
                elapsed += 3;
                if (elapsed >= AuthorityWaitSeconds && !cts.IsCancellationRequested)
                {
                    Console.WriteLine($"no opponent joined in {AuthorityWaitSeconds}s; cancelled. Choose matchmaking again to retry.");
                    try { await meta.DequeueAsync(online.MyId); } catch { }
                    if (online.MatchKey != null) { try { await meta.ClearMatchAsync(online.MatchKey); } catch { } }
                    online.CancelHosting(); // re-arm so a fresh in-game matchmake works
                    break;
                }
            }
        });
    }

    // Bidirectional pump where each direction is teed through its sniffers. Caller may pass
    // a CTS to cancel the pump externally (used to hand the game off to the relay seamlessly).
    static async Task PumpDualSniffAsync(Stream game, Stream server, OpcodeSniffer[] gameToServer, OpcodeSniffer[] serverToGame, CancellationTokenSource cts)
    {
        var t1 = SniffCopyAsync(game, server, cts, gameToServer);
        var t2 = SniffCopyAsync(server, game, cts, serverToGame);
        await Task.WhenAny(t1, t2);
        cts.Cancel();
        try { await Task.WhenAll(t1, t2); } catch { /* expected on teardown */ }
    }

    static async Task SniffCopyAsync(Stream from, Stream to, CancellationTokenSource cts, params OpcodeSniffer[] sniffs)
    {
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token)) > 0)
            {
                await to.WriteAsync(buf.AsMemory(0, n), cts.Token);
                foreach (var s in sniffs) s.Feed(buf, n);
            }
        }
        finally { cts.Cancel(); }
    }

    // Ensure the local PtoServer is accepting on LocalPort: connect if up, else spawn
    // PtoServer.exe (PTO_SERVER_EXE, default "PtoServer.exe") with --port and wait.
    // The Steam client ignores DisableSandbox and reads its server IP from
    // %LOCALAPPDATA%\ptoc\settings.ini (default 100.107.105.101 = the old Tailscale
    // host). Force it to the local launcher, and re-assert since the game rewrites
    // that file when it exits. Only writes on drift, so the poll loop is quiet.
    static string ClientIniPath()
    {
        string name = Environment.GetEnvironmentVariable("PTO_CLIENT_SANDBOX") ?? "ptoc";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name, "settings.ini");
    }

    static bool IsIpLine(string line) =>
        line.Replace(" ", "").TrimStart().StartsWith("IP=", StringComparison.OrdinalIgnoreCase);

    static void EnsureClientLocal()
    {
        const string want = "IP=\"127.0.0.1\"";
        try
        {
            string path = ClientIniPath();
            if (File.Exists(path))
            {
                var lines = new List<string>(File.ReadAllLines(path));
                int ip = lines.FindIndex(IsIpLine);
                if (ip >= 0)
                {
                    string val = lines[ip].Split('=', 2)[1].Trim().Trim('"');
                    if (val == "127.0.0.1") return; // already correct: no write (no churn)
                    lines[ip] = want;
                }
                else
                {
                    int net = lines.FindIndex(l => l.Trim().StartsWith("[NETWORK]", StringComparison.OrdinalIgnoreCase));
                    if (net >= 0) lines.Insert(net + 1, want); else { lines.Add("[NETWORK]"); lines.Add(want); }
                }
                File.WriteAllLines(path, lines);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, new[] { "[NETWORK]", want });
            }
            Console.WriteLine($"client server IP -> 127.0.0.1 ({path})");
        }
        catch (Exception ex) { Console.WriteLine($"client IP enforce skipped: {ex.Message}"); }
    }

    static async Task KeepClientLocalAsync()
    {
        while (true) { EnsureClientLocal(); await Task.Delay(4000); }
    }

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
        var sniff = new OpcodeSniffer(OpQueue, _ => fired++);
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
        bool oneShot = fired == 1;

        // repeat mode: fires on every Op.BattleEnd (authority watches many battles)
        int ends = 0;
        var rep = new OpcodeSniffer(3, _ => ends++, repeat: true);
        var s2 = new List<byte>();
        s2.AddRange(Frame(3, 2)); s2.AddRange(Frame(20, 5)); s2.AddRange(Frame(3, 0));
        var b2 = s2.ToArray();
        for (int off = 0; off < b2.Length; off += 4) { int n = Math.Min(4, b2.Length - off); var c = new byte[n]; Array.Copy(b2, off, c, 0, n); rep.Feed(c, n); }

        // one-shot re-arm: fires again after Rearm()
        int q2 = 0;
        var os = new OpcodeSniffer(0, _ => q2++);
        var qf = Frame(0, 1);
        os.Feed(qf, qf.Length); os.Rearm(); os.Feed(qf, qf.Length);

        bool ok = oneShot && ends == 2 && q2 == 2;
        Console.WriteLine(ok
            ? "sniffdemo OK: one-shot fires once (ignored login+campaign); repeat fires per battle-end; rearm re-fires"
            : $"sniffdemo FAIL: oneShot={oneShot} ends={ends}(want 2) rearm={q2}(want 2)");
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

    // Authority side, updated per match; the persistent relay listener reads these.
    public bool Listening;                    // relay.Listen + ladder tail set up once
    public volatile bool Hosting;             // currently advertising/hosting a match
    public volatile CancellationTokenSource? HeartbeatCts;
    public ulong MyId;
    public string? MatchKey;
    public OpcodeSniffer? QueueSniff; // the game's Op.Queue trigger, re-armed after a match/cancel
    public byte[]? LoginPacket;       // guest: last op46 seen, replayed onto the authority
    public byte[]? QueuePacket;       // guest: the op0 that triggered matchmaking, replayed onto the authority

    public bool Begin() => Interlocked.Exchange(ref _begun, 1) == 0; // go online at most once per match
    public void Reset() { GuestAuthority = 0; Interlocked.Exchange(ref _begun, 0); } // guest match end: offline, re-armed

    // Authority match end (guarded so offline campaign battles, which also send
    // Op.BattleEnd, don't trip it). Returns true if we were actually hosting.
    public bool EndHosting()
    {
        if (!Hosting) return false;
        Hosting = false;
        Interlocked.Exchange(ref _begun, 0);
        return true;
    }

    // Authority gave up waiting for a guest: stop hosting and re-arm so a fresh
    // in-game matchmake retriggers.
    public void CancelHosting()
    {
        Hosting = false;
        Interlocked.Exchange(ref _begun, 0);
        QueueSniff?.Rearm();
    }
}

// Watches a framed client->server byte stream (transparent: the caller still forwards
// every byte) and fires ONCE when a packet with the target opcode appears. Framing is
// [id u8][key u16][size u32 total incl 7-byte header][payload]; an accumulator handles
// packets split across reads. Client->server packets are small, so buffering is cheap.
sealed class OpcodeSniffer
{
    readonly byte _target;
    readonly Action<byte[]> _onSeen; // receives the full framed packet bytes
    readonly bool _repeat; // false: fire once then go idle until Rearm(); true: fire on every occurrence
    byte[] _buf = Array.Empty<byte>();
    int _len;
    bool _fired;

    public OpcodeSniffer(byte targetOpcode, Action<byte[]> onSeen, bool repeat = false)
    { _target = targetOpcode; _onSeen = onSeen; _repeat = repeat; }

    public void Rearm() => _fired = false; // re-arm a one-shot sniffer for the next match

    public void Feed(byte[] data, int count)
    {
        if (count <= 0 || (_fired && !_repeat)) return;
        Append(data, count);
        int off = 0;
        while (_len - off >= 7)
        {
            uint size = BitConverter.ToUInt32(_buf, off + 3);
            if (size < 7) { off++; continue; }      // resync on garbage (shouldn't happen on a valid stream)
            if (_len - off < size) break;            // packet not complete yet
            if (_buf[off] == _target)
            {
                _onSeen(_buf[off..(off + (int)size)]);
                if (!_repeat) { _fired = true; off += (int)size; break; }
            }
            off += (int)size;
        }
        if (off > 0) { Array.Copy(_buf, off, _buf, 0, _len - off); _len -= off; }
    }

    void Append(byte[] data, int count)
    {
        if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_len + count, _buf.Length * 2 + 256));
        Array.Copy(data, 0, _buf, _len, count);
        _len += count;
    }
}
