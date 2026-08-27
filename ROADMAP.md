# PTO roadmap (Steam P2P branch)

Status board for the clean-room Pixel Tactics Online server (`PtoServer.cs`) and the
`ptolaunch` Steam P2P launcher. Checkpoint: 2026-08-28.

## Done

- **Accounts**: register / login, salted-hash passwords (`data/accounts.txt`). Steam build
  auto-logs in via the SteamID (login mode 2 = get-or-create).
- **Decks**: per-account persisted decks (`DeckStore`).
- **Full battle engine**: every card in the client is implemented (orders, spells, passives,
  auras, all leaders). Card/effect coverage audited against the effects PDF.
- **Ranked ladder**: personal climb ladder, persisted rank, wired into login/battle end.
- **Concede** (op57); **singleplayer campaign** (op55): battle vs the stage's AI leader,
  difficulty-scaled, playable offline.
- **Steam P2P launcher** (`Launcher/ptolaunch`), the whole networking layer:
  - Transport tunnel: byte-for-byte pump; `demo` self-check.
  - Steam relay (SteamNetworkingSockets, AppID 480) wraps a relay connection as a Stream.
  - Firebase metaservice (RTDB over REST): symmetric match queue + shared ranked ladder.
  - Symmetric matchmaking + auto-elected authority (lower SteamID runs its local server);
    no host/joiner roles.
  - Offline-first: local server always; go online only on Op.Queue. Match-end returns both
    sides to passive offline, re-armed for another match (no restart).
  - Runtime + packaging: net10 x64, `check` preflight, client-IP enforcement.

## In progress / next

- **First real two-box run.** Everything above is unit/loopback self-checked; the live Steam
  relay hop and the full go-online → play → match-end → offline cycle need two machines with
  Steam + a shared Firebase project to exercise end to end.
- **Friends**: use Steam's friends list (real identity, free on AppID 480). Not wired into the
  launcher UI yet.
- **Campaign polish**: port the full Steam stage tables (3 worlds / 25 nodes / 76 stages) and
  set `StageCount` so the op60 unlock bitmap matches; progress persistence (op60). Detail:
  `SINGLEPLAYER_CAMPAIGN.md`.

## Known caveats

- Steam identity is client-asserted (no auth ticket), so the shared ladder is spoofable. Fine
  for a private / Spacewar server. Hardening ceiling: a Steam-auth-ticket Cloud Function.
- **DisableSandbox can't work**: the Steam client is GameMaker Studio 2, which always keeps
  ini/save under `%LOCALAPPDATA%\<name>` and ignores the GMS1 DisableSandbox flag. The launcher
  manages `%LOCALAPPDATA%\ptoc\settings.ini` instead (this is the correct approach, not a hack).
- Firebase writes are unauthenticated (open dev rules); use a dedicated project for a playtest.

## Superseded

- The old "move off Tailscale to a public cloud VM" plan is replaced by the Steam P2P relay.
  That central-server hosting path lives on `archive/tailscale` (+ tag `tailscale-final`); its
  guide `DEPLOY.md` is legacy.
