# PTO_C protocol — full opcode map

Extracted from the client's `packet_init` (opcode → script index) and the script index table.
Direction is from the server's point of view where a handler exists on the client (S→C), i.e.
these are the client's receive handlers. Opcodes the client *sends* reuse the same numbers
(e.g. login=46, ping=52).

| opcode | client handler (`container_*`)        |
|-------:|----------------------------------------|
| 46 | login |
| 52 | ping |
| 48 | loaded |
| 47 | add_deck |
| 49 | add_card_to_collection |
| 50 | battle_details |
| 2  | battle_start |
| 3  | battle_end |
| 4  | battle_data |
| 5  | summon_unit |
| 6  | summon_unit_get |
| 7  | mouse_hover |
| 8  | draw_card |
| 9  | draw_card_get |
| 12 | hand_card_remove |
| 13 | hand_card_remove_get |
| 14 | turn_get |
| 15 | action |
| 16 | action_get |
| 17 | wave_update |
| 18 | update_unit |
| 19 | update_unit_get |
| 21 | battle_attackphase |
| 23 | battle_casualties |
| 24 | clear_corpse |
| 25 | clear_corpse_get |
| 26 | move_unit |
| 27 | move_unit_get |
| 28 | order_slot |
| 29 | order_slot_get |
| 30 | discard_card |
| 31 | discard_card_get |
| 32 | destroy_unit |
| 33 | destroy_unit_get |
| 34 | draw_other_arrow |
| 35 | attack |
| 36 | attack_get |
| 37 | mulligan |
| 38 | update_buff |
| 39 | update_buff_get |
| 40 | can_action |
| 41 | effect |
| 42 | order_create |
| 43 | order_remove |
| 44 | spell_create |
| 45 | spell_remove |
| 53 | battle_cover |
| 54 | deck_update |
| 56 | activate_arrow |
| 58 | emoticon |
| 59 | draw_other_slot |
| 60 | stages |
| 61 | unlock |
| 62 | orbs |
| 63 | orbs_get |
| 64 | draw_other_slot |
| 65 | disconnect |

## Post-login data payloads (implemented)

Sent by the server after a successful login (op 46 status 3), before `loaded` (op 48):

- **op 49 add_card_to_collection** — `bool back, bool land, u16 cardId, u8 amount`.
  cardId is a card-DB index 0..231 (116 cards × {normal even id, holographic odd id}), or a
  back id 0..10 when `back=1`, or a land id 0..4 when `land=1`. The client filters what is
  displayable by card `_special`/`_collection`, so granting every id is safe.
- **op 60 stages** — `StageCount × (bool completed, bool unlocked)`; StageCount = 49 (stages 0..48).

### Deck saving (op 47 is bidirectional)

- **C→S (net_save_deck):** `bool flag, str name, u8 deckId, u16 back, u16 land, 31× u16 cards`.
  `flag` is 0 (saved from deck list) or 1 (saved from within the deck builder). cards are DB ids,
  0 = empty slot. The server persists these under `data/<user>.decks`.
- **S→C (container_add_deck):** `u8 deckId, str name, u16 back, u16 land, 31× u16 cards` (no flag).
  Sent on login to restore saved decks.

## Key client scripts (reference)

- `packet_header(op)` → writes `u8 op`, `u16 1374`, `u32 0` (length placeholder)
- `packet_write(type, value)` → `buffer_write(net_buffer, type, value)`
- `packet_send()` → pokes total length into the u32 at offset 3, then `network_send_raw`
- `network_read_data(buffer, size)` → loops: read `u8 id`, `u16 key`, `u32 size`, dispatch via
  `packet_map[id]`, then seek to `packet_start + size`
- Connection: `obj_client` Create → `network_create(global.__ip, 51338)`
- Door → lobby: `container_loaded` flips `obj_menu_login.door_open`, then `create_menu()` spawns
  `obj_menu_controller` (main menu with `global.back_room = 155`)
