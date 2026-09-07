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

### Classic Act IV completion

Diablo credit alone leaves a Classic character at progression `0x03`; Nightmare remains locked.
After Diablo dies and `SpecialQuestEvent 0x0C` arrives, return to the Pandemonium Fortress and talk
to Tyrael. Inside one normal NPC conversation send a single `QuestMessage`, `0x02AC` — see below;
earlier revisions of this file listed three ids and that is what kept act 4 open. Terminate the
conversation, leave the game, and relog. Progression `0x04` proves Nightmare Act I is unlocked.

The reference client sent no `QuestComplete(Act4Outro)` packet. Do not synthesize one. Cached quest
flags are not acceptance: prove completion from the refreshed MCP roster, then physically initialize
the character in an Act I Nightmare game.

**Diablo has to have been killed, but not necessarily in the same game.** Brisara took her kill in
one game and collected from Tyrael in the next one, so the credit carries. What does not carry is a
stale reading of it: never skip the kill because the character's flags look complete, and never gate
the kill on those flags.

**Terror's End needs the rushee at the fight, not merely in the area.** This is the one place where
proximity matters; it does not for Andariel, where 87 units still credited. Four runs left the rushee
at the cleared seal anchor while the rusher teleported to the star, and every one stalled at
`terror=0x5008` - acknowledged and defeated-this-game, but never bit 0. Walking the rushee to the star
before the kill gave `terror=0x3041`, bit 0 set, and progression `0x04` on the next roster read.
Bring the rushee to the star before Diablo dies.

**Terror's End flags do not answer "died in this game".** Brisara joined carrying `0x1008` and later
read `0x5008` in a game, and `0x2000` — the bit Andariel's credit sets — never appears at all. Use the
kill the bot performed and verified as the signal instead of trying to read it back out of the word.

**Tyrael takes exactly one message: `0x02AC`.** Captured from a real 1.09 client that took a
character from progression `0x03` into Nightmare — `InitiateEntityChat`, `QuestMessage 0x02AC`,
`TerminateEntityChat`, and nothing else.

Do not send `0x0298` or `0x029E`, and **do not follow the ids Tyrael advertises.** `0x029E` walks him
into a different branch which answers with an `NPCInfo` offering `0x029F`; `0x02AC` is then ignored
and act 4 never closes. That is what four failed runs looked like. Tyrael also stays silent on
`InitiateEntityChat` — `NPCWantInteract` and no `NPCInfo` — so `GetOfferedMessages` is empty and a gate
waiting on it can never open, the same silence Warriv keeps.

**Leaving the game commits nothing off d2gs.** The client's end-of-difficulty OK dialog sends no BNCS
or MCP packet: a full three-protocol capture of the leave shows only an ordinary realm re-logon
(`LOGONREALMEX`, MCP `STARTUP`, `CHARLOGON`). Progression is settled server-side from the quest state,
so there is nothing extra for a clientless bot to send.

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

## Immunities

**Base resistances are not on the wire.** `AssignNpc` carries id, code, position, life percentage,
state, effects and monster enchantments - no resistances. The client reads immunities from
`monstats.txt` per difficulty, so a clientless bot has to hardcode them or parse the MPQ.
`EntityEffect.Resistcold` is a temporary buff, not a profile.

**Monster enchantments ARE sent**, so anything that follows from an enchantment is detectable at
runtime: `MonsterEnchantment.ColdEnchanted = 18` arrives on the boss like any other flag.

Classic, what matters for a rush:

- **Duriel is cold immune above Normal.** A frozen orb sorceress does literally nothing to him and
  will tank until her potions run out - three runs died that way before the cause was known.
- **De Seis guards the top seal**, is cold immune in Hell, and on Nightmare only sometimes -
  reportedly when he rolls Cold Enchanted, which is the detectable case. Unverified hypothesis.
- **The pack around a seal is not immune.** Suppressing cold for the whole fight cripples the clear;
  reserve it for the boss itself.

Against an immune boss: static field until about 20% life - it is not cold and bites to the difficulty
floor - then whatever non-cold damage the character has, with the cold skills skipped throughout.

## Helper traps

- **`Game.TeleportToLocation` returns true without sending anything** when already within 10 units.
  A true return is not proof a packet went out.
- **The two move helpers fail silently in opposite directions.** `RushBot.MoveTo` returns *true*
  without sending anything inside 10 units; `Game.MoveToAsync`/`MoveTo` return *false* without
  sending anything beyond 20. Any distance threshold picked without allowing for both leaves a band
  where the character neither moves nor acts, and it looks exactly like a hang: a rushee sat at a
  constant 2.8 units from a portal for twenty passes, never moving, never interacting. Interact
  close in, short-hop with `MoveToAsync` up to 10, and path beyond that.
- **`PathingService.GetPath` returns an empty list for an unreachable target**, which is
  indistinguishable from an absent one. An empty path means "cannot route", not "not there" — and for
  objects created during play it is the normal answer.
- **The waypoint bitfield is packed in panel order, not numeric enum order.** Iterate
  `WaypointExtensions.BitOrder`. `Enum.GetValues<Waypoint>()` happens to agree for act 1 and is
  scrambled for every act after, so act 1 tests pass while later acts silently decode wrong.
