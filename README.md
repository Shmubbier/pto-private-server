# Pixel Tactics Online, private server (Steam P2P)

A clean-room compatibility server for the abandoned **Pixel Tactics Online** client, plus a
launcher that connects overseas players peer-to-peer over **Steam's relay** with zero setup.
The original server (`ptotemserv.ddns.net`) is gone; this project reimplements the protocol
so the client can connect again, and `ptolaunch` handles getting two players connected.

**Status:** ✅ **Playable.** The full server-authoritative **battle engine** is complete
(every hero card's orders/spells, passives, positional and ongoing auras, traps, scry,
discard, and all 25 leaders), plus accounts, decks, ranked ladder, concede, and the
singleplayer campaign (op55). The **Steam P2P launcher** is built and self-checked; the live
relay path wants a real two-machine run to fully exercise.

> This is the **`steam-p2p`** branch. `main` is the earlier central-server (Tailscale/cloud-VM)
> build; that hosting path is archived (`archive/tailscale` + tag `tailscale-final`) and its
> guide, `DEPLOY.md`, is legacy. Everything below is the P2P model.

---

## Architecture

Two pieces, cleanly split:

- **`PtoServer.cs`** — the clean-room game server: the battle engine and account/deck/ladder
  logic. Binds raw TCP `51338`. Unchanged by the P2P work; it just runs locally now.
- **`Launcher/ptolaunch`** — the networking + deployment layer. The game only ever talks to
  `127.0.0.1`; the launcher tunnels its bytes to the peer over Steam's relay, runs the local
  server, and handles matchmaking. See [`Launcher/README.md`](Launcher/README.md).

**Data model: local-first + a thin cloud metaservice.** Accounts, decks, and campaign progress
stay on each machine (the local `PtoServer`'s `data/`). Only genuinely-shared state lives in
**Firebase** (Realtime Database, plain REST, no SDK): the match queue and the ranked ladder.
Firebase is reached over outbound HTTPS, so it sidesteps CGNAT with no port to forward and no
VM to keep alive. Friends come from Steam's own friends list.

**Symmetric matchmaking, no host/joiner.** `ptolaunch play` registers you in the queue; when
two peers pair, the **lower SteamID** is silently elected the authority and runs its local
server, and the other connects to it over the relay. Both compute the same pairing from the
same queue snapshot, so there is no negotiation. When a match ends, both return to passive
offline, ready to play again. Details and the build order: [`Launcher/README.md`](Launcher/README.md).

---

## How the networking works

The client is a **GameMaker Studio 2** game. It talks to the server over a **raw TCP socket** on
port **51338**, always at `127.0.0.1` (the launcher's proxy):

    authority (lower SteamID):  game -> 127.0.0.1:51338 -> PtoServer (local)
                                                            ^
                       guest's peer == Steam relay ==> ptolaunch (bridges to local server)

    guest:  game -> 127.0.0.1:51338 -> ptolaunch == Steam relay ==> authority's ptolaunch -> its server

Because the tunnel is a byte-for-byte pump, the wire framing and the three WAN client patches
(connect-on-login, TCP reassembly, port config) are untouched.

**Where the client reads its server address (important):** the Steam client is GMS2 and
**does not honor DisableSandbox**, so it ignores the `settings.ini` next to the exe and reads
`%LOCALAPPDATA%\ptoc\settings.ini`. `ptolaunch` writes and continuously enforces `IP=127.0.0.1`
there, and the client's built-in default is patched to `127.0.0.1` too, so it always reaches
the local launcher. See `NETWORKING.md` for the full story.

### Wire format (little-endian, identical both directions)

| offset | type | meaning                                         |
|-------:|------|-------------------------------------------------|
| 0      | u8   | opcode                                           |
| 1      | u16  | magic = `1374` (client reads but ignores it)     |
| 3      | u32  | total length in bytes, **including this header** |
| 7…     | …    | payload                                          |

`buffer_string` = raw UTF-8 bytes followed by a single `0x00` terminator.

### Key opcodes

| op | name                     | direction   | notes |
|---:|--------------------------|-------------|-------|
| 46 | login / register         | C→S, S→C     | Steam build: mode 2 = get-or-create keyed on the SteamID |
| 48 | loaded (door → lobby)    | S→C         | bool legend, u16 rank |
| 49 | add_card_to_collection   | S→C         | the ~3 KB account blob is many op49 packets |
| 60 | stages                   | S→C         | campaign unlock bitmap |
|  0 | queue (go online)        | C→S         | the launcher's "go online" trigger |
|  3 | battle_end               | S→C         | the "match over" signal |
| 55 | start_stage              | C→S         | singleplayer campaign stage vs AI |
| 57 | concede                  | C→S         | sender concedes, opponent wins |

The complete opcode table and every `container_*` handler live in [`docs/PROTOCOL.md`](docs/PROTOCOL.md).

---

## Quick start (the playtest bundle)

The shipped deliverable is `PTO_C151_P2P_Playtest/` (built separately; not in git). Each player:

1. Install the **.NET 10 x64 runtime** (one time). Have **Steam** running (a distinct account each).
2. Put your shared Firebase Realtime Database URL in `firebase.txt`.
3. `check.bat` → wants three green lines (Steam, Firebase, server).
4. `play.bat` (online matchmaking) or `local.bat` (offline / vs AI), then launch `game\ptoc.exe`.

The bundle's `README.txt` has the full walkthrough. The launcher points the client at the local
server automatically, so start a `.bat` before launching the game.

## Build from source

Server (net10, runs locally on each machine):

```bash
dotnet publish PtoServer.csproj -c Release -o out/server
```

Launcher (net10, **x64** to match the 64-bit `steam_api64.dll`; AppID 480 via `steam_appid.txt`):

```bash
dotnet publish Launcher/Launcher.csproj -c Release -o out/launcher
```

`ptolaunch` self-checks with no Steam/Firebase needed: `demo` (byte tunnel), `matchdemo`
(pairing/election), `queuedemo` / `rankeddemo` (Firebase logic against a local fake RTDB), and
`check` (live Steam + Firebase + server preflight).

---

## What works

- **Accounts** — register/login, salted-SHA-256, persisted under `data/`. Steam build auto-logs
  in via the SteamID (mode 2, get-or-create).
- **Lobby, collection, deckbuilder** — full card collection; decks build and persist per user.
- **Server-authoritative battle engine** — every draw/summon/move/attack/turn is a server-computed
  `container_*` message: waves, melee/ranged, intercept, counter, armor, cover, leader HP and
  win/loss; all hero orders/spells; passives and positional + ongoing auras; traps; scry and
  discard; all 25 leaders' passives and active abilities with themed cast effects.
- **Ranked ladder** — a personal climb ladder; also mirrored to the shared Firebase ladder by the
  authority tailing `data/matches.txt`.
- **Singleplayer campaign** — op55 starts a battle vs the stage's AI leader (difficulty-scaled),
  playable fully offline. See [`SINGLEPLAYER_CAMPAIGN.md`](../SINGLEPLAYER_CAMPAIGN.md).
- **Steam P2P launcher** — symmetric matchmaking, auto-elected authority, Steam relay tunnel,
  offline-first, ranked sync, client-IP enforcement.

## Notes / provenance

- Reverse engineered from `data.win` with UndertaleModTool (decompiled GML). The `1374` magic
  and port `51338` are the client's own constants.
- Clean-room reimplementation for interoperability with an abandoned service; no original server
  code was available (confirmed with the original developer).
- Steam identity is client-asserted (no auth ticket in this build), so the shared ladder is
  spoofable. Fine for a private / friends launch; the hardening ceiling is a Steam-auth-ticket
  Cloud Function.
