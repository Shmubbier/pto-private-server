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

    public async Task PublishHostAsync(ulong steamId, string name)
    {
        var body = JsonSerializer.Serialize(new HostEntry { steamId = steamId.ToString(), name = name, ts = Now() });
        using var c = new StringContent(body, Encoding.UTF8, "application/json");
        (await _http.PutAsync(Url($"hosts/{steamId}"), c)).EnsureSuccessStatusCode();
    }

    public async Task UnpublishHostAsync(ulong steamId)
    {
        try { await _http.DeleteAsync(Url($"hosts/{steamId}")); } catch { /* best effort on shutdown */ }
    }

    // Live hosts, freshest filter applied so a host that died without unpublishing ages out.
    public async Task<List<HostEntry>> ListHostsAsync(int staleSeconds = 20)
    {
        var json = await _http.GetStringAsync(Url("hosts"));
        if (string.IsNullOrWhiteSpace(json) || json == "null") return new();
        var map = JsonSerializer.Deserialize<Dictionary<string, HostEntry>>(json) ?? new();
        long cutoff = Now() - staleSeconds;
        var live = new List<HostEntry>();
        foreach (var h in map.Values) if (h != null && h.ts >= cutoff) live.Add(h);
        live.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        return live;
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

sealed class HostEntry
{
    public string steamId { get; set; } = "";
    public string name { get; set; } = "";
    public long ts { get; set; }
}
