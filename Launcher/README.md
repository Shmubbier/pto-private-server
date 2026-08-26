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
2. Steam relay (step 1b): replace the ONE peer hop. JOIN's outbound `TcpClient`
   becomes a SteamNetworkingSockets relay-connect by SteamID; HOST's inbound
   accept becomes a relay-accept. Both still yield a `Stream` to `PumpAsync`, so
   nothing else changes. AppID 480 (Spacewar) via `steam_appid.txt`, Steamworks.NET.
3. Lobby create / list / join + friend invites (ISteamMatchmaking).
4. Session-scoped roster (each player owns local accounts/decks; peer info kept
   for the session only unless friended).
5. Offline / campaign launch mode (server already does bot battles; op55 landed).

## Run the self-check

    dotnet run -c Release demo
