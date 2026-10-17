---
name: d2-109
description: Game rules of Diablo 2 1.09 as this realm's server applies them - aura pulses and linger, Baal wave trigger, cast timing, sight range, join limits, state packets, deaths. Use when a game mechanic decides a bot's timing, positioning or survival, or when D2MOO, a wiki or a newer patch disagrees with what the realm did.
---

# Diablo 2 1.09 mechanics

The realm runs 1.09. Wikis describe 1.10+ and D2R, and D2MOO is a decompilation of 1.10-1.13 DLLs, so
every rule from them is a **lead** until the realm confirms it. Each fact below carries its provenance:

- **measured** - seen on the realm in packet logs or batch results; trust it.
- **D2MOO** - read from the decompiled server; true for 1.10+, plausible for 1.09, verify before
  building on a fine detail.
- **told** - the user's knowledge of 1.09, not yet measured.

When a lead and the realm disagree, the realm wins: record the measurement here and mark the lead.
Measure from Debug packet logs (`d2-bot` skill, `DEBUGGING.md`) rather than from bot behaviour.

## Connection and casting

- **Five game connections per IP per ~15 s** (measured). A sixth is refused as the server closing the
  connection (first bytes `FF FF` instead of `A7 01`). `GameServerConnectionGate` shares the count across
  bot processes.
- **A run packet repeated to the same spot gets the connection cut for good** (measured): hundreds of
  zero-distance `0x03` in milliseconds, then the server goes silent with no pong and no FIN. `Game.MoveTo`
  skips a repeat within 150 ms.
- **The server drops a cast sent during the previous animation** (measured): no reply at all. An accepted
  teleport answers with `ReassignPlayer` ~230 ms later; recasting every 60 ms until it arrives gives a hop
  every 320-360 ms.
- **Your own casts are never echoed** (measured): `0x4C` carries other units only. Use `NPCHit` (`0x0C`)
  or the mana drop as the receipt.
- **Teleport reaches about 30 units** per hop; longer moves go through pathing.
- **Cast delays come from `patch_d2.mpq` `skills.bin`**, not `skills.txt`; FCR never shortens them.
  Frozen Orb is 25 frames (1.0 s), Blizzard 45, Meteor 30; Static, Nova and Teleport have none.
- **Whirlwind runs to its end point** (measured): it cannot be cut short, and a potion sent mid-whirl is
  ignored.

## The realm's servers

- **Every game server restarts every 3 hours at hh:15:15** (measured), each server on its own hour. Every game on
  it is cut; clients see an `IOException` ("connection aborted") and the bot carries on with the next game.
- **A game name can stay taken for over an hour** (measured): create is refused with d2cs `NAME_EXIST` (result
  `0x1F`) until the next restart frees it. The bot moves on to the next name.
- **One of the game servers refuses town portals** (measured): a bot that drifted onto it after a restart lost 9
  of 12 games to refused portals, on an unshared account. See `d2-bot`, Environment.

- **A gambling refresh is bound by the round trip** (measured): the server answers ~90 ms after the refresh and
  the window arrives as one burst. Rings and amulets are in every window, a rare base such as a scepter in about
  one of 5-10, so gambling a rare base costs far more time per gold.

## Sight and streaming

- **A client only receives units within ~55 units** (measured). A boss further away never enters
  `WorldObjects`; scout toward it instead of declaring it missing.
- **Objects streamed during play are sent once**, when a client first comes into range (see `d2-bot`).

## States and auras

- **State packets** (measured): `0x12` is the full 160-bit state mask of a unit and is how a state
  *ends*; `0x13` adds one state with a value (Battle Orders `0x62` = 98 %); `0x14` adds one with two
  dwords. `0xA9 EndState` belongs to the newer protocol and never arrives here.
- **Aura pulse** (D2MOO `SKILLS_SrvDo065_BasicAura`, `SKILL_ComputePeriodicRate`): every aura pulses
  every 50 frames (2 s) on a grid shared by all auras, and each pulse applies its state until the next
  pulse. By that code a switched-off aura ends at the next 2 s mark.
- **On 1.09 an aura lingers ~8 s** (measured, told): after a paladin switched from Fanaticism to
  Conviction, the party lost the Fanaticism state 7.7 s and 7.8-8.1 s later, about four pulses. The new
  aura's state arrived in the same millisecond as the switch. So a paladin alternating two auras every
  6 s keeps both on the party without a gap; `BaalBot` does this with Conviction and Fanaticism, and its
  `Aura trace` lines re-measure it.
- **Conviction and Lower Resist never break an immunity on 1.09** - see `AGENTS.md`.

## Life, potions and death

- **Maximum life is computed client-side** (measured): base from attributes 7/9 in 256ths, plus
  socketed gems (owner field of the `ItemInSocket` packet) and the Battle Orders percentage.
- **Battle Orders raises the maximum, never the current life**, and healing potions do not stack - the
  thresholds that follow from both live in `d2-bot` `PARTY.md`, Survival.
- **Life 0 is not a death** (measured): 12 of 13 leaves at life 0 lost no experience. A death is the
  server assigning your corpse (`0x8E`) after the death animation, and it shows as lifetime experience
  falling on the next join (`0x1A`-`0x1C` carry the lifetime total).
- **Monster life bytes carry a type flag** (measured, D2MOO `MonsterMsg.cpp`): mask `NPCHit` life with
  `0x7F` before dividing by 1.28. A monster taking no damage sends no life update.

## Monsters

- **A unique fire-enchanted monster explodes 160 ms after it dies** (measured, D2MOO
  `MonsterUnique.cpp`): radius difficulty + 4 (6 in Hell), physical and fire damage. Minions do not.
- **The Chaos Sanctuary spawns Diablo ~15.6 s after the last seal boss dies** (measured, 134 games).
- **Server item and monster tables can differ from the client's MPQs** - see `AGENTS.md`, Game data.

## Baal's throne

The throne room is a preset, so these coordinates hold in every game (map origin 15000,5000):

- **Throne** (`NPCCode.BaalThrone`) at (15090, 5011); `EntityCode.BaalPortal` at (15090, 5005).
- **Next wave** (D2MOO `AITHINK_Fn134_BaalThrone`, measured): the throne raises a wave only while no
  living monster is within **64** of it, at least 100 frames after the previous one, and spawns it at
  its own position + 13 Y. A bot that ignored monsters 50-64 away stalled the waves until it timed out;
  clearing the full 64 brought every wave.
- **Hall geometry** (map data): the walkable cells within 64 are the hall (x 15071-15119, y 5001-5074)
  plus the mouth of the entrance corridor. Anything beyond ~67 is behind the hall walls, so 64 is also
  the radius that reaches nothing unreachable.
- **The usual portal spot** (`BaalPortal` + (27, 65)) is 65 from the throne, just outside the trigger.
  Fighting there drags monsters to the hall edge.
- **The hammer spot** is throne + (4, 18): 5 below where waves appear.
- **Waves are over** when `NPCCode.BaalThrone` disappears (Baal leaves for the Worldstone Chamber).
  `EntityCode.BaalPortal` is streamed from the start and is no signal.
