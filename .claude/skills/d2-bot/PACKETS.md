# Packets, quests and helper traps

Established facts. Prefer these over anything re-derived; each cost real runs to establish.

## Talking to NPCs

Two opcodes that look interchangeable and are not:

- **`0x31 QuestMessage(entityId, messageId)`** carries a real dialogue id and is what advances
  quests. Ids are not derivable from anything else in the code: the working Andariel message is
  `0xB7` (183) while `WarrivAct1` is 155, and 183 is `GiantLampreyYoung`.
- **`0x4D PlayNPCMessage(npcCode)`** carries an **NPC code**, not a message id, and only plays that
  NPC's gossip. Confirmed against `AssignNPC1`: Jerhyn's assign carries code `0x00C9` (201), and the
  captured `0x4D` payload is the same 201. Every other captured `0x4D` is likewise an NPC code
  (`0xB2` Fara, `0xB1` Drognan, `0xCA` Lysander).

Wrap a conversation in `0x2F InitiateEntityChat` and `0x30 TerminateEntityChat`.

**`GetOfferedMessages` returns nothing for most quest NPCs.** Code that only sends what the server
advertises sends nothing at all — and can still report success. Send the captured message list
unconditionally, and verify by state rather than by the call returning.

## Travel

NPC travel is `0x38 EntityAction(type, entityId, action)` with the act's travel action, via
`Game.TravelWithNpc`. It is gated on quest completion: act 1's Warriv only sails once
`IsComplete(SistersToTheSlaughter)`. A travel NPC that refuses is nearly always an incomplete quest
upstream, not a broken travel packet — check the quest word before touching the travel code.

## Quest words

Per-character flags, read with `Quests.GetCharacterFlags(QuestId)`:

- bit 0 `0x0001` — complete
- bit 1 `0x0002` — deed done, awaiting reward
- bit 12 `0x1000` — completed in a previous game
- bit 13 `0x2000` — credited this game; **does not persist**, so it reads differently in the next game

Kill credit is **area-bound**: the character must be in the boss's area at the moment of death. A
character that is dead at the kill still takes credit, but receives no quest pushes, so its word is
unreadable until it is resurrected.

## Items

- **An item on the cursor survives leaving and rejoining a game**, and is invisible to inventory,
  cube and equipment checks. Clear the cursor on join, and count the cursor when asking whether a
  character holds something.
- **Unequipping is `0x1C RemoveBodyItem(slot)`**, not `0x19`. Worn items land on the cursor —
  `GameData` must set `CursorItem` on `Action.Unequip`, or the bot waits forever for an item that is
  already in hand.
- **Inserting the horadric staff takes three steps and the packet alone does nothing**:
  `EntityInteract` the orifice, `0x19 RemoveItemFromBuffer` to put the staff on the cursor, then
  `0x44` once. A second `0x44` after a successful one drops the connection.

## Helper traps

- **`Game.TeleportToLocation` returns true without sending anything** when already within 10 units.
  A true return is not proof a packet went out.
- **`PathingService.GetPath` returns an empty list for an unreachable target**, which is
  indistinguishable from an absent one. An empty path means "cannot route", not "not there" — and for
  objects created during play it is the normal answer.
- **The waypoint bitfield is packed in panel order, not numeric enum order.** Iterate
  `WaypointExtensions.BitOrder`. `Enum.GetValues<Waypoint>()` happens to agree for act 1 and is
  scrambled for every act after, so act 1 tests pass while later acts silently decode wrong.
