# Pixel Tactics Online (PTO_C151), private server

A clean-room compatibility server for the abandoned **Pixel Tactics Online: PreAlpha v0.1.72**
client (`PTO_C.exe` / `data.win`). The original server (`ptotemserv.ddns.net`) is gone; this
project reimplements the protocol so the client can connect again.

**Status:** ✅ **v1.0.0, playable.** Login/register with real accounts, lobby, collection,
deckbuilder, matchmaking, and a full server-authoritative **battle engine** are working. The stock
client connects, players register a username + password, build decks, queue, and play complete
matches. Every hero card's orders/spells, passives, positional and ongoing auras, traps, scry,
discard, and all 25 leaders (passives + actives) are implemented, with themed cast effects.

---

## How it works

The client is a GameMaker Studio 1.x game. It talks to the server over a **raw TCP socket** on
**port 51338**. The server address is read from `settings.ini` in the game folder:

```ini
[NETWORK]
IP=127.0.0.1
```

(Default when absent is `ptotemserv.ddns.net`.) The port `51338` is hardcoded in the client.

### Wire format (little-endian, identical both directions)

Every packet:

| offset | type | meaning                                         |
|-------:|------|-------------------------------------------------|
| 0      | u8   | opcode                                           |
| 1      | u16  | magic = `1374` (client reads but ignores it)     |
| 3      | u32  | total length in bytes, **including this header** |
| 7…     | …    | payload                                          |

`buffer_string` = raw UTF-8 bytes followed by a single `0x00` terminator.
Client buffer type codes seen: `u16=3`, `bool=10`, `string=11`.

### Opcodes (resolved from the client's `packet_init` map)

| op | name                     | direction        | payload |
|---:|--------------------------|------------------|---------|
| 46 | login / register         | C→S then S→C      | **C→S:** bool register, str user, str pass, u16 version(=72). **S→C:** u8 status |
| 48 | loaded (open door→lobby) | S→C              | bool legend, u16 rank |
| 47 | add_deck                 | S→C              | u8 id, str name, u16 back, u16 land, 31× u16 card ids (0 = empty) |
| 49 | add_card_to_collection   | S→C              | bool back, bool land, u16 cardId, u8 amount |
| 60 | stages                   | S→C              | per stage: bool completed, bool unlocked |
| 62 | orbs                     | S→C              | u8 amount |
| 52 | ping                     | both             | empty (client sends ~1/sec; echo it for latency) |

Login status bytes (op 46, S→C): `0`=username exists, `1`=not registered, `2`=bad password,
`3`=**success** (+ str username), `4`=incorrect version, `5`=already logged in.

The full opcode table and every `container_*` handler live in the client; see
`docs/PROTOCOL.md` for the complete map extracted during reverse engineering.

---

## Build & run

Requires only the in-box .NET Framework compiler (already on Windows), no SDK, no internet.

**One press:** double-click **`PTO Server.bat`**. It kills any stale server, builds fresh, and opens
the server on `0.0.0.0:51338`. Close the window to stop it.

Or by script:

```powershell
./build.ps1            # -> PtoServer.exe
./PtoServer.exe        # listens on 0.0.0.0:51338, verbose logging
./PtoServer.exe --quiet
./start-lan.ps1        # LAN host: opens the firewall (if admin) and prints the LAN IP
```

Then launch `PTO_C.exe` with `settings.ini` pointing at the server's IP, and click **Register** in
the client to create an account (username + password, stored under `data/`).

- Same machine: `IP=127.0.0.1`.
- LAN / friends: set `IP=` to the server host's LAN or public IP and forward TCP **51338**.

---

## What works (v1.0.0)

- **Accounts**, register/login with username + password, salted-SHA-256, persisted under `data/`.
- **Lobby, collection, deckbuilder**, full card collection; decks build and persist per user.
- **Matchmaking**, Arena queue pairs two clients (op 0 join / op 1 cancel); a lone queuer can play a
  server-side **bot** (`PTO_BOT=1`) for solo testing.
- **Server-authoritative battle engine**, every draw/summon/move/attack/turn is a server-computed
  `container_*` message. Implemented: waves, melee/ranged, intercept, counter, armor, cover,
  leader HP and win/loss; all hero card orders/spells; passives and positional + ongoing auras;
  traps; scry and discard; and all 25 leaders' passives and active abilities, each with its themed
  op41 cast effect.
- **Two client patches** ship separately (`data.win` via UndertaleModTool): a required crash fix and
  the Batrov/Pendros targeting patch. See the project notes.

## Notes / provenance

- Reverse engineered from `data.win` with UndertaleModTool (decompiled GML).
- Clean-room reimplementation for interoperability with an abandoned service; no original
  server code was available (confirmed with the original developer).
- The `1374` magic and port `51338` are the client's own constants.
