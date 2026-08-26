# PTO private server roadmap

Status board for the clean-room Pixel Tactics Online server (`PtoServer.cs`) and its
patched clients. Checkpoint: 2026-08-27.

## Done

- **Accounts**: register / login, salted-hash passwords (`data/accounts.txt`).
- **Decks**: per-account persisted decks (`DeckStore`).
- **Full battle engine**: every card in the client is implemented (orders, spells,
  passives, auras, all leaders). Card/effect coverage audited against the effects PDF.
- **Ranked ladder**: personal climb ladder, persisted rank, win/loss updates, wired
  into login (op48), battle details (op50), and battle end (op3).
- **Companion website API**: HTTP+JSON (`/login`, `/players`, `/player/{user}`),
  live online list, match history (`MatchStore`), HMAC session tokens.
- **Concede** (op57): sender concedes, opponent wins.
- **Steam client** (`Clients/Steam_Ver-PTO-C`, the latest build): auto-login via
  Spacewar (AppID 480), keyed on the immutable Steam id, persona shown as the display
  name (`DisplayNames`); server login mode 2 = get-or-create. Hidden cards unlocked.
- **Deploy config**: `DEPLOY.md`, `pto-server.service` (systemd), `Caddyfile`,
  `PtoServer.csproj` (net8) for a Linux VM.

## In progress / next

### Multiplayer: hosting + reachability
Move off Tailscale to a public host so testers connect with zero setup. Server
already binds `0.0.0.0:51338`. Blocked: Oracle Cloud unavailable right now; PH ISP is
CGNAT so port-forward/DDNS is out. Steam relay (SDR) is not reachable from this build.
Plan: stand up a free cloud VM (Oracle Always Free per `DEPLOY.md`, or GCP e2-micro)
and bake its IP into the client default. Detail: `NETWORKING.md` (dev workspace).

### Singleplayer campaign (op55)
Client-complete, server-stubbed. TODO in order:
1. Handle **op55 (StartStage)**: start a battle vs the stage node's AI leader (reuse
   the existing bot plumbing). Currently unhandled, so stages hang on "Loading...".
2. Port the **Steam** stage tables (3 worlds / 25 nodes / **76 stages**) and set
   `StageCount` 49 -> 76 so the op60 unlock bitmap matches the Steam client.
3. **Difficulty**: Easy / Hard / Challenge scale the AI deck/level.
4. **Progress persistence** (op60): per-account completed/unlocked stages; mark a stage
   done on a win and unlock the next node. Detail: `SINGLEPLAYER_CAMPAIGN.md`.

## Known caveats

- Steam identity is client-asserted (no auth ticket in this build), so it is spoofable
  by a crafted client. Fine for a private / Spacewar server; not hardened.
- Client `DisableSandbox` (settings.ini in the exe folder) not yet confirmed honored at
  runtime for the Steam build.
- Companion API CORS is open (`*`); tighten to the site origin before public use.
