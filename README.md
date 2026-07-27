# Pixel Tactics Online (PTO_C151) — private server

A clean-room compatibility server for the abandoned **Pixel Tactics Online: PreAlpha v0.1.72**
client (`PTO_C.exe` / `data.win`). The original server (`ptotemserv.ddns.net`) is gone; this
project reimplements the protocol so the client can connect again.

**Status:** ✅ Login, lobby, **collection, and deckbuilder** working. The stock client connects,
authenticates, reaches the main menu, shows a full card collection, and can build/save decks that
persist across sessions. Actual gameplay (matchmaking + battle rules) is **not** implemented yet —
see *Roadmap*.

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

Requires only the in-box .NET Framework compiler (already on Windows) — no SDK, no internet.

```powershell
# build
./build.ps1            # -> PtoServer.exe

# run
./PtoServer.exe        # listens on 0.0.0.0:51338, verbose logging
./PtoServer.exe --quiet
```

Then launch `PTO_C.exe`. With `settings.ini` pointing at the server's IP, log in with **any**
username/password — this milestone accepts everyone.

- Same machine: `IP=127.0.0.1`.
- LAN / friends: set `IP=` to the server host's LAN or public IP and forward TCP **51338**.

---

## Roadmap

1. **[done]** Framing, login/register, lobby entry, ping keepalive.
2. **[done]** Post-login data load: full collection (cards/backs/lands) + stages, so the
   Collection and Deckbuilder screens populate (opcodes 49/60).
3. **[done]** Deck saving: parse client deck saves (op 47 C→S), persist per-user under `data/`,
   and send saved decks back on login (op 47 S→C).
4. **[done]** Matchmaking (Arena queue): pair two clients (op 0 join / op 1 cancel), send the
   `battle_start` handshake so both fade into `rm_battle`, then deliver both players' `battle_details`
   and each player's opening hand via `battle_data` in response to the client's `op 20` ready signal.
5. Account persistence (real user database, password checks, "already logged in").
6. The hard part: the authoritative **battle engine**. The client is fully server-driven —
   every draw/summon/attack/turn is a `container_*` message. Each must be reimplemented
   server-side per the game's rules (Pixel Tactics ruleset).
   - **[done, first cut]** Board reveal: sending both `battle_details` on the client's `op 20`
     makes `display_UI` reach 2 (the HUD/board only draw at 2), fixing the earlier black screen.
   - **[done, first cut]** Mulligan → turn 1: on `op 37` from both players, the server keeps the
     hand (no redraws yet) and sends `turn_get` (`op 14`) twice per client to end the mulligan and
     begin turn 1. Verified via scripted clients; real-client visual confirmation pending.
   - **[todo]** Mulligan redraws, then the in-turn actions: summon (`op 10`), move (`op 26`),
     attack (`op 35`), orders/spells, wave advance, and win conditions — each relayed and
     rule-checked server-side.

## Verified so far

- Login/lobby/collection/deckbuilder: confirmed against the real client (screenshots).
- Matchmaking: two clients queue → server pairs them → both receive `battle_start` and transition
  into the battle room; each then gets both players' details and its own 5-card opening hand.
  Verified via protocol tests (two scripted clients) and a real client entering `rm_battle`.
  Fully rendering/playing the board is battle-engine work (item 6).

## Notes / provenance

- Reverse engineered from `data.win` with UndertaleModTool (decompiled GML).
- Clean-room reimplementation for interoperability with an abandoned service; no original
  server code was available (confirmed with the original developer).
- The `1374` magic and port `51338` are the client's own constants.
