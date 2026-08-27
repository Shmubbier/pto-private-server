# ptolaunch (Steam P2P build)

Launcher that tunnels the game's raw-TCP link over the Steam relay, so the game
never learns Steam exists. It points the game at `127.0.0.1` (settings.ini
`[NETWORK] IP=127.0.0.1`) and pumps the bytes to the peer. Framing and client
patches 05/07/08 are untouched.

`ptolaunch play` is symmetric: no host/joiner roles. Both peers queue up; when
paired, the LOWER SteamID is silently elected the authority and runs its local
PtoServer, the other connects to it over the relay. Same on both machines.

    authority (lower SteamID):  game -> 127.0.0.1:51338 -> PtoServer (local)
                                                            ^
                       guest's peer == relay ==> ptolaunch -+ (bridges to local server)

    guest:  game -> 127.0.0.1:51338 -> ptolaunch == relay ==> authority's ptolaunch -> its PtoServer

## Build order status

1. **Transport tunnel** - DONE. `ptolaunch demo` proves the byte path in-process.
   A bridge is "accept here, connect there, pump bytes" (`ServeAsync` / `PumpAsync`).
2. **Steam relay** - DONE (built; `check` confirmed a live `SteamAPI.Init` +
   relay-access init against a real Steam client). `SteamPeer.cs` wraps a
   SteamNetworkingSockets relay connection as a `Stream`, so it drops into the same
   `PumpAsync`. AppID 480 via `steam_appid.txt`; built x64 (so it shares one .NET 10
   runtime with the server), so it needs the MODERN 64-bit `steam_api64.dll` matching
   Steamworks.NET 2024.8.0 (Windows-x64 dll from `Steamworks.NET-Standalone_2024.8.0.zip`
   or SDK 1.60). The game's own 2018-era `steam_api.dll` has no networking API and will
   NOT work for the launcher. Both launcher and server target **net10** and run on a
   single .NET 10 x64 runtime.
3. **Firebase meta client** - DONE. Data model is local-first + a thin cloud
   metaservice (see NETWORKING.md): accounts/decks/campaign stay on each machine;
   only genuinely-shared state lives in Firebase RTDB, reached by REST (no SDK). Set
   `PTO_FIREBASE_URL` (+ optional `PTO_FIREBASE_AUTH`) or drop the RTDB URL in
   `firebase.txt`.
   - **Offline-first + auto-detect online**: `ptolaunch play` always runs a local
     PtoServer (on `LocalPort` 51339) and proxies the game (`GamePort` 51338) to it,
     so login, deckbuilding, and the campaign work with no internet or broadcasting.
     The proxy sniffs the game->server stream (`OpcodeSniffer`) and only when it sees
     **Op.Queue (0)** does it go online; the campaign (Op.StartStage 55) is a local
     bot battle so it never trips it. `sniffdemo` self-checks the detection.
   - **Symmetric matchmaking**: on going online both peers enqueue and compute the
     same pairing (`ElectPartner`: sort ids, pair adjacent); the lower SteamID is
     elected authority and hosts on its own local server (its game is already queued
     there); the guest reconnects onto the authority over the relay (the fixed client
     can't migrate a live session, so the launcher drops its connection to force it).
     The authority writes a match record the guest confirms (closes the snapshot-skew
     race) and stays in the queue until the guest connects. `matchdemo` / `queuedemo`
     self-check the election and queue. On **Op.BattleEnd (3)** the guest's launcher
     forwards the result then drops the relay so the client reconnects back to its own
     local server (offline); the authority is already local. Ranked is pushed to
     Firebase by the authority tailing `matches.txt`.
   - **Ranked ladder**: rank is a personal climb ladder
     (`rank = clamp(25 - wins + losses, 1, 99)`), a pure function of a player's own
     counts, so it is authority-independent: Firebase just accumulates wins/losses.
     The authority tails the server's `data/matches.txt` (a line cursor makes
     restarts count each match once) and bumps `/ranked/<steamid>`; `ptolaunch
     ladder` prints it. `PTO_SERVER_DATA` points at the server data dir (default
     `data`). `rankeddemo` self-checks it. Spoofable until a Steam-auth-ticket Cloud
     Function validates matches (deferred ceiling).
   - Friends: use the Steam friends list (real identity, free on AppID 480).
4. Session-scoped roster: the guest's local account/decks reach the authority's
   server for the session only, not persisted, unless the two friend each other.
5. Offline / campaign launch mode (server already does bot battles; op55 landed).

The authority auto-spawns `PtoServer.exe` if 51338 isn't already up
(`PTO_SERVER_EXE`, default `PtoServer.exe`).

## Run the self-checks

    dotnet run -c Release demo        # transport tunnel (4-hop byte round-trip)
    dotnet run -c Release matchdemo   # deterministic pairing / authority election
    dotnet run -c Release sniffdemo   # Op.Queue "go online" detection
    dotnet run -c Release queuedemo   # match queue against a local fake RTDB
    dotnet run -c Release rankeddemo  # ranked count accumulation + rank derivation

Live preflight (needs Steam running + PTO_FIREBASE_URL): `ptolaunch check` prints
one line each for Steam, Firebase, and the server binary.
