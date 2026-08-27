using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PtoLauncher;

// Thin cloud metaservice client: the launcher's half of the local-first + shared-DB
// model (see NETWORKING.md). Accounts/decks/campaign stay local on each machine;
// only genuinely-shared state (host directory now, ranked later) lives in Firebase.
// The game never learns Firebase exists, same discipline as the Steam relay.
//
// Uses the Firebase Realtime Database REST API over plain HTTPS, so there's no SDK
// dependency and nothing to port. No PTO_FIREBASE_URL configured => Enabled=false and
// the launcher runs fine without a directory (host still joinable by pasted SteamID).
//
// ponytail: heartbeat presence (host rewrites its entry every ~10s, readers drop
// entries older than staleSeconds). REST can't register onDisconnect (that needs the
// websocket protocol); upgrade to a streaming client if instant drop-off matters.
sealed class MetaClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly string? _base;   // e.g. https://proj-default-rtdb.firebaseio.com  (no trailing /)
    readonly string _auth;    // "" or "?auth=<idToken|dbSecret>"

    public bool Enabled => _base != null;

    // Production: read config from env (or firebase.txt beside the exe).
    public MetaClient()
    {
        var url = Environment.GetEnvironmentVariable("PTO_FIREBASE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            try { if (File.Exists("firebase.txt")) url = File.ReadAllText("firebase.txt").Trim(); }
            catch { /* no config file, stay disabled */ }
        }
        _base = string.IsNullOrWhiteSpace(url) ? null : url!.TrimEnd('/');
        var tok = Environment.GetEnvironmentVariable("PTO_FIREBASE_AUTH");
        _auth = string.IsNullOrEmpty(tok) ? "" : "?auth=" + tok;
    }

    // Test seam: point at a local fake-RTDB endpoint.
    public MetaClient(string baseUrl) { _base = baseUrl.TrimEnd('/'); _auth = ""; }

    string Url(string path) => $"{_base}/{path}.json{_auth}";

    // --- Symmetric match queue -------------------------------------------------
    // Both peers write themselves here ("looking for match") on a heartbeat; there
    // is no host or joiner, just a queue. Pairing is computed identically by both
    // peers from the same snapshot (see Program.ElectPartner), so no negotiation.
    public async Task EnqueueAsync(ulong steamId, string name)
    {
        var body = JsonSerializer.Serialize(new QueueEntry { steamId = steamId.ToString(), name = name, ts = Now() });
        using var c = new StringContent(body, Encoding.UTF8, "application/json");
        (await _http.PutAsync(Url($"queue/{steamId}"), c)).EnsureSuccessStatusCode();
    }

    public async Task DequeueAsync(ulong steamId)
    {
        try { await _http.DeleteAsync(Url($"queue/{steamId}")); } catch { /* best effort */ }
    }

    // Fresh queue entries; a peer that died without dequeuing ages out. Entries far past
    // stale (a crashed peer) are opportunistically deleted so the DB doesn't accumulate
    // junk over a long session. DELETE is idempotent, so concurrent readers racing to
    // prune the same key is harmless.
    public async Task<List<QueueEntry>> ListQueueAsync(int staleSeconds = 20)
    {
        var json = await _http.GetStringAsync(Url("queue"));
        if (string.IsNullOrWhiteSpace(json) || json == "null") return new();
        var map = JsonSerializer.Deserialize<Dictionary<string, QueueEntry>>(json) ?? new();
        long now = Now(), fresh = now - staleSeconds, dead = now - staleSeconds * 4;
        var live = new List<QueueEntry>();
        foreach (var kv in map)
        {
            var q = kv.Value;
            if (q == null) continue;
            if (q.ts >= fresh) live.Add(q);
            else if (q.ts < dead) _ = PruneAsync($"queue/{kv.Key}"); // long-dead: best-effort delete
        }
        return live;
    }

    async Task PruneAsync(string path) { try { await _http.DeleteAsync(Url(path)); } catch { } }

    // Match confirmation: the elected authority (lower SteamID) writes its id under
    // the pair key so the other peer can confirm reciprocity before it commits and
    // connects. This closes the snapshot-skew race in the deterministic pairing.
    public async Task SetMatchAsync(string pairKey, ulong authority)
    {
        var body = JsonSerializer.Serialize(new MatchRecord { authority = authority.ToString(), ts = Now() });
        using var c = new StringContent(body, Encoding.UTF8, "application/json");
        (await _http.PutAsync(Url($"matches/{pairKey}"), c)).EnsureSuccessStatusCode();
    }

    public async Task<ulong> GetMatchAuthorityAsync(string pairKey)
    {
        var json = await _http.GetStringAsync(Url($"matches/{pairKey}"));
        if (string.IsNullOrWhiteSpace(json) || json == "null") return 0;
        var rec = JsonSerializer.Deserialize<MatchRecord>(json);
        return rec != null && ulong.TryParse(rec.authority, out var a) ? a : 0;
    }

    public async Task ClearMatchAsync(string pairKey)
    {
        try { await _http.DeleteAsync(Url($"matches/{pairKey}")); } catch { /* best effort */ }
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // --- Ranked ladder ---------------------------------------------------------
    // The server's rank (RankStore) is a PERSONAL climb ladder: rank is a pure
    // function of a player's own cumulative wins/losses, so it is host-independent
    // and Firebase only needs to accumulate the counts. Each match is recorded on
    // exactly one host, whose launcher bumps the counts once, so no double-count.
    // Keep these constants in sync with RankStore in PtoServer.cs.
    const int StartRank = 25, MinRank = 1, MaxRank = 99, WinStep = 1, LossStep = 1;

    public static int RankFromCounts(int wins, int losses)
        => Math.Max(MinRank, Math.Min(MaxRank, StartRank - wins * WinStep + losses * LossStep));

    public async Task AddResultAsync(ulong winner, ulong loser)
    {
        await BumpAsync(winner, won: true);
        await BumpAsync(loser, won: false);
    }

    // ponytail: read-modify-write, no transaction. A player is only ever in one
    // battle at a time, so concurrent writes to the same row can't happen; use an
    // ETag if-match or a Cloud Function only if that ever stops holding.
    async Task BumpAsync(ulong id, bool won)
    {
        var row = await GetRowAsync(id);
        if (won) row.wins++; else row.losses++;
        using var c = new StringContent(JsonSerializer.Serialize(row), Encoding.UTF8, "application/json");
        (await _http.PutAsync(Url($"ranked/{id}"), c)).EnsureSuccessStatusCode();
    }

    async Task<RankRow> GetRowAsync(ulong id)
    {
        var json = await _http.GetStringAsync(Url($"ranked/{id}"));
        if (string.IsNullOrWhiteSpace(json) || json == "null") return new RankRow();
        return JsonSerializer.Deserialize<RankRow>(json) ?? new RankRow();
    }

    // Whole ladder, best rank (lowest number) first.
    public async Task<List<(string id, RankRow row)>> LadderAsync()
    {
        var json = await _http.GetStringAsync(Url("ranked"));
        if (string.IsNullOrWhiteSpace(json) || json == "null") return new();
        var map = JsonSerializer.Deserialize<Dictionary<string, RankRow>>(json) ?? new();
        var outp = new List<(string, RankRow)>();
        foreach (var kv in map) if (kv.Value != null) outp.Add((kv.Key, kv.Value));
        outp.Sort((a, b) => RankFromCounts(a.Item2.wins, a.Item2.losses)
                            .CompareTo(RankFromCounts(b.Item2.wins, b.Item2.losses)));
        return outp;
    }
}

sealed class RankRow
{
    public int wins { get; set; }
    public int losses { get; set; }
}

sealed class QueueEntry
{
    public string steamId { get; set; } = "";
    public string name { get; set; } = "";
    public long ts { get; set; }
}

sealed class MatchRecord
{
    public string authority { get; set; } = "";
    public long ts { get; set; }
}
