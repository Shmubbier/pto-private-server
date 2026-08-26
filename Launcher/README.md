# ptolaunch (Steam P2P build)

Launcher that tunnels the game's raw-TCP link over a peer relay, so the game
never learns Steam exists. It points the game at `127.0.0.1` (settings.ini
`[NETWORK] IP=127.0.0.1`) and pumps the bytes to the peer. Framing and client
patches 05/07/08 are untouched.

    HOST:  game -> 127.0.0.1:51338 -> PtoServer (local)
                                      ^
           peer == relay ==> ptolaunch --+ (bridges each peer to a local server conn)

    JOIN:  game -> 127.0.0.1:51338 -> ptolaunch == relay ==> host's ptolaunch -> host's PtoServer

## Build order status

1. **Transport tunnel** - DONE. `ptolaunch demo` proves the 4-hop path with a
   plain-TCP peer link (`ptolaunch host` / `ptolaunch join <ip>` for two boxes).
   A bridge is "accept here, connect there, pump bytes", so host and join are the
   same `ServeAsync`.
2. **Steam relay (step 1b)** - DONE (built; runtime needs a Steam client + two
   boxes to exercise). `SteamPeer.cs` wraps a SteamNetworkingSockets relay
   connection as a `Stream`, so it drops into the same `PumpAsync`. `ptolaunch
   steamhost` prints your SteamID64; `ptolaunch steamjoin <id>` tunnels the local
   game to it. AppID 480 via `steam_appid.txt`; built x86 to match the repo's
   32-bit `steam_api.dll` (x64 would need `steam_api64.dll`).
3. **Firebase meta client** - presence DONE, ranked NEXT. Data model is local-first
   + a thin cloud metaservice (see NETWORKING.md): accounts/decks/campaign stay on
   each machine; only genuinely-shared state lives in Firebase RTDB, reached by REST
   (no SDK). `ptolaunch steamhost` publishes presence on a 10s heartbeat; `hosts`
   lists live hosts; `play` picks one from the directory and joins (replaces the
   manual SteamID paste). Set `PTO_FIREBASE_URL` (+ optional `PTO_FIREBASE_AUTH`) or
   drop the RTDB URL in `firebase.txt`; unset = directory disabled, join by SteamID.
   `ptolaunch metademo` self-checks the presence logic against a local fake RTDB.
   - Ranked ladder (next sub-step): Firebase, keyed by SteamID. Needs a server ->
     launcher result signal (the launcher watches for battle-end, then writes the
     result); spoofable until a Steam-auth-ticket Cloud Function validates matches.
   - Friends: use the Steam friends list (real identity, free on AppID 480).
4. Session-scoped roster: the joiner's local account/decks reach the host's server
   for the session only, not persisted, unless the two friend each other.
5. Offline / campaign launch mode (server already does bot battles; op55 landed).

## Run the self-checks

    dotnet run -c Release demo       # transport tunnel (4-hop byte round-trip)
    dotnet run -c Release metademo   # presence directory against a local fake RTDB
