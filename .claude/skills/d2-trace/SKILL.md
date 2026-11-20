---
name: d2-trace
description: Diagnose recorded bot games with the Trace tool, which replays every recorded packet through the real Game class. Use before grepping a log whenever a bot died, chickened, stalled or ran slow, a step failed, a character would not move, reach or interact, the client's state looked wrong, a question turns on what a packet or unit said, or speed and experience over recorded games are asked about.
---

# Diagnosing recorded games

Every game a bot plays is recorded: each client's BNCS, MCP and D2GS packets, every log line at the
bot's log level, and typed bot events (step, target, wait, move, decision). `Trace` replays the
packets through the bot's own `Game`, so the state it shows is the state the bot held. Start every
diagnosis here; the text log is the fallback.

## Start

```
dotnet build tools/Trace -c Debug
```

The tool is then `tools/Trace/bin/Debug/net10.0/Trace.exe`, written `Trace` below; skip the build when
that exe is newer than the checkout's last commit. It has its own output folder, so building while a bot
runs is fine. `Trace` with no arguments prints every command and option; that usage text is the reference.

Traces live in a `traces` folder beside the bot's log file - for the start scripts,
`src/ConsoleBot/bin/Release/net10.0/traces`, or `<bot folder>\<build folder>\traces` for a bot on its
own build - as `traces/<yyyyMMdd>/<game>/<client>.d2trace`, one client per character. Startup never
deletes them. Run `Trace` from the folder holding `traces`, so the game refs and `next:` lines it
prints run as printed. Pass `--datadir "<bot folder>\data"` (or set `D2NG_DATADIR` once) or item
packets do not parse.

1. **Find the incident.** `Trace incidents traces/<yyyyMMdd> --datadir <data>` opens each party with
   the worst of the day (deaths, chickens, longest stalls, failed steps, lost connections, with game and
   time), then lists every game in start order. A game marked `IN PROGRESS` is still being recorded, so
   counts grow between runs. Add `--kind death` (or any kind it printed, quoted when it has a space:
   `--kind "stuck cursor"`) to keep one kind.
2. **Run its `next:` line.** Each incident prints the command that shows its window, usually a
   `timeline` of 20 seconds around it.
3. **Read the state at the moment.** `Trace state <game> --client <client> --at <mm:ss>` prints what
   that client held: position, area, life, effects, `cursor` (only present when an item is on it),
   nearby units with life and distance, portals.

## Task to command

| question | command |
|---|---|
| which game is the log line about | `find <date> --game <name> --at <hh:mm:ss>` (the log's local clock; `--at "yyyy-MM-dd hh:mm:ss"` over a root): the ref the other commands take, its clock span, clients and their trace files (`client (client-2.d2trace)`); without a match, the nearest games |
| why did X die or chicken | `incidents <date> --kind death` (or `chicken`), then its `next:` line, then `damage <game> --client X --from <t-5s> --to <t>`; a `death` names its killer, the signals that fired and the last 3 s, and a bot leaving right after dying is a detail of it; a chicken's biggest hit and every `damage` drop name the curses on the client then (`under Amplifydamage`: a Cursed pack doubles each hit) |
| is the party ever not fighting, and doing what | `activity <date> --party-idle`: from the party's first attack to its last in the area it fought in most, the time no member was fighting and what each was doing then (a single client's idle says little in a party: walkers stop while the sorceresses clear) |
| which member held a party bot's start | `activity <date> --prepare`: per game whose prepare step ended last and how long after the one before it (a party's run starts when the last prepare ends), per client how often it was last and its prepare's median and p90 |
| where does town time go | `activity <date or game> --town` (stays by cause, time inside per part) |
| which skills does X fight with, is it out of mana | `activity <date or game> --skills` |
| where did X's mana go | `mana <game> --client X [--from T --to T]`: falls by own cast (skill, typical cost), mana burn, maximum falling; rises by potion, leech, regeneration; the rest unattributed; each change with what the client was doing and the nearest ManaBurn monster (`--all` lists regeneration too) |
| did a whirl, charge, leap or teleport go anywhere, hit anything | `activity <game> --client X --skill-moves`: each move the server played out, the point asked and how often, the server's positions before and after, monsters hit along it, its mana; `WENT NOWHERE` marks under 3 moved with nothing hit. Over a date folder, one line per client: moves per skill, how many went nowhere and their seconds a game, the mean move - teleports that go nowhere are recasts reaching the server after the move |
| is X attacking into walls or at nothing | `incidents <date> --kind "attack into a wall"` (target - the unit, or for a point cast the nearest monster in its direction - across unwalkable ground and nothing hit in the skill's window; then its `next:` tiles), `--kind "attacks hitting nothing"`, `--kind "skill move went nowhere"`; each one incident per stretch with count, span, target and mana |
| where does a step's time go | `activity <date> --by-step` (slowest median first; names only failed runs' games), `state <date> --at-events "step:<name>" --select self` for the game and end time of each run, then `activity <game> --by-step` (start, split, longest idle) and `timeline <game> --client X --idle` |
| why can't it find, reach or interact with Y | `timeline <game> --client X --from <t> --to <t>` (moves, interacts, reassigns, `cursor holds`, log lines; `--limit 0 \| grep` to find the moment), `state ... --select self,portals,objects:30` (`cursor` in `self`), `incidents <date> --kind "stuck cursor"`, `unit <game> --client X <code or id>` |
| is the client's state wrong | `incidents <game> --checks state`; `replay <game>` reports live/replay digest mismatches |
| what did the server send about unit U | `unit <game> --client X npc:0x1C9` (or a code name), every packet naming it with the state after each |
| which packets went out or came in | `packets <game> --client X --from <t> --to <t> [--type 0x28] [--sent]`; `--realm` for BNCS and MCP |
| who hurt the client | `damage <game> --client X --from <t> --to <t>` |
| what is at a map position | `tiles <game> --client X --at <t>` or `tiles <game> --point x,y --area <Area>` (`#` wall, `~` blocks walking only: missiles cross it) |
| where does object or NPC code C appear | `seen <date> --code object:<Code>` |
| is a monster where we think it is | `state ... --motion legacy\|anchored\|simulated\|server` places the units by that model (`server`: the last packet's position, with its kind and age; `legacyAt` when it differs); `unit <game> --client X <id>` for the server's positions; `accuracy <game or date> --by class` scores the models: legacy (what bots held before 15 Nov 2026), anchored (last exact position), simulated (moved along its path; needs monster speeds in the data folder; what every bot runs now); its `self` section scores the client's own position against the server's |
| quest words at a moment | `state ... --select quests`, or `packets ... --type 0x28` for each change |
| what a client held at a moment | `state <game> --client X --at <t> --select equipped,cursor,bag,cube,stash,belt` (`inventory` is bag, cube and stash in one list); `--items full` adds stats, sockets, durability, requirements |
| where did item I go, what did town cost or bring | `items <game> [--client X] [--item 0x1C9 or a name] [--kind sold]`: every pick up, lift, put (with the cell), equip, drop, sale and purchase or gamble (with gold), identify, transmute, use and trade, each with when it was asked and answered; a summary per client and for the game first |
| a request the server ignored | `items <game> --kind UNANSWERED` |
| does the client's idea of its items match the server's | `incidents <game> --checks state` (kinds `item overlap`, `item outside grid`, `container overfull`, `item held after removal`, `item misplaced`, `cursor not given`, `item id reused`); `items` says per client how many verdicts it reached |
| speed or xp over a day | `activity <date>`, `kpi xp <date>`; narrow with `--game-prefix`, `--clients`, `--since` |
| would leaving games earlier earn more | `kpi xp <date> --leave-after`: xp/min in each 15 s after a game's first experience gain (rows below the day's average marked) and what every game would have made had it been left 10, 20, 30 ... s after it, best cut last. Choose on one day, check on another: 20 Nov 2026 cow games peaked at 130 s after the first gain, +2.6% |
| does the party stay together, who walks alone | `activity <date> --party-spread [--alone 30]`: each second of the hunt (the area the party spent most time in), every member's distance to the nearest other one; per member the share of it alone and the median, per game a line. Teleporting sorceresses read as alone by design; the walkers' share is what straggler waits change: 20 Nov 2026 the cow amazon went from 9% alone (85 games) to 1% once the lead's walks waited too |
| which area spell takes the most life, is a rotation right | `activity <date> --area-spells [--client C]`: each Static Field, Nova and Frost Nova credited with the monster life (in % of a monster) that fell within its radius (Static 20, Nova and Frost Nova 10) of the caster until the next cast, at most 0.6 s; per client and skill the share of casts that took life, per cast, per second, kills. A party member's damage there counts too, so compare skills, and at an equal radius: 20 Nov 2026 cow sorceresses, within 10, Nova ~105%/s against Static ~60%/s while they cast Static 60% of the time |
| did a setting help (monster motion, a bot switch) | `activity <date> --phases --split monsterMotion`, `kpi xp <date> --split monsterMotionEffective`, `incidents <date> --split identifyWithTome`; `--where key=value` keeps one value. A game shows its settings as a `bot settings:` line in `timeline` |
| turn a recorded moment into a regression test | `export <game> --client X --at <t> [--radius 40] --out <file.json>` (needs the map service): the raw collision grid around the client, the client and every monster in sight under each motion model; no names (the client is `self`, others `player1`...). Check it for names, put it in `src/ConsoleBot.Tests/data/fixtures/` and load it with `MomentFixture.Load(name)`: `.Map()` is the `AreaMap` the attack code reads, `.Monsters("legacy")` the `WorldObject`s; `MomentFixtureTests` is the example (the Travincal whirl wall, 14 KB at radius 30) |
| why are some games slow, where does standing still go | `activity <date> --phases` (town, travel, kill, loot, return per game; the slowest games against the median game, each with a `cause:` line - stuck at a wall / skill moves went nowhere, death, chicken / out of potions, connection lost, waypoint or portal refused, path failure, monster accuracy issue, gambling, muling, vendor trip, long town stay, else the slowest phase - and every failed step with what ended it), `activity <game> --idle` (each stand-still: cause, server/typed/bot wait, what was sent around it), `--latency` (request to answer per kind) |
| is the bot wasting potions | `incidents <date> --kind "potions wasted"` (healing potions closer than 1.5 s, mana potions within 5 s of the previous one, or at full life or mana); `mana <game> --client X` for what each potion refilled |
| was loot left on the ground | `incidents <date> --kind "loot left behind"`: an item of magical quality or better dropped fresh (a monster's death or a chest; a player's drop is left out) within 40 of a client that no client of the game ever held; one per item, from the client nearest to it then, with every client's pick-up requests, a refused pick-up (the server drops the item back on its spot) and how that client left it (town, another area, out of sight, trace end); `items <game> --item <id>` for the requests and refusals. 20 Nov 2026, 78 leveling games: 298, half asked for (a refused pick-up, a walk that failed) and half never asked for (the pickit's choice, or the party leaving as it did at Diablo) |
| did a member stand by a boss without attacking it | `incidents <date> --kind "boss not attacked"`: 8 s or more with an act boss or super unique alive within 30 of the client, out of town and alive, and no attack at it - at the unit, at a point within 6 of it or meant for it, or Static Field (20), Nova and Frost Nova (10), Blessed Hammer (14) around the caster; each with the bosses' distances and life, the attacks at other targets and what ended it. A stretch with an attack at other targets every 4 s or more often is the bot fighting the pack first (it takes a boss last) and is left out, as are other uniques. 20 Nov 2026, 78 leveling games: 164, 78 of them the sorceress and amazon at Diablo, whom the bot's line-of-sight test refused |
| what every client held at each event of a kind | `state <date> --at-events step:<name>` (one JSON line per client per event) |

## Reading the output

- Times are `mm:ss.fff` offsets from the game's start, the same in every command of one game.
- A client is its character name, the trace file name; its class is `class` in `state --select self`.
  In `timeline`, `?` is a log line naming no client; in `incidents`, `-` is an incident of the whole
  party or game.
- `<folder>#N` is the N-th game of that name that day. Names are reused and a reused name spreads
  over `<name>`, `<name>~2`...; the tool reads them together, so use refs exactly as printed.
- `... N more lines left out (limit 200); narrow with ...` - narrow as it says, or `--limit 0`.
- `--json` gives one object per line for piping.
- `note: recorded by build <sha> (<date>), client rules N; replayed under rules M` heads every replaying
  command: state is what that bot held only when M is N. `--rules latest|N` replays under other rules.
- `state` answers at the last packet at or before the time asked: `at` is that packet, `asked` your time.
- `unknown (last known: X)` as an area: the client's believed position has left the map the server
  revealed to it - it walked on alone while the server holds the character elsewhere. Treat it as a
  desync: look for `position mismatch` incidents and the server's reassign in `timeline`.
  `X (server position)` is the area of the server's last stated position while the client's own is off
  its map; `position off its map` and `position unconfirmed` incidents mark such stretches.
  `server holds position` marks the other desync: the server keeps answering with one position while
  the client walks away from it, each answer snapping it back, so neither of those two fires.
- `damage` credits a drop to units whose attack named the client in the second before (each with its
  attack packet's time; the drop's own time is the later life packet); `aimed nearby or closing in` is
  inference, and the rest is `unattributed`. Its `INFERENCE likely:` lines name what may explain an
  unattributed drop: an enchanted unit dying or hit close by, a skill cast beside the client, poison.
- A typed move marked `NOT CONFIRMED` (and a `move not confirmed` incident when it is over 10 off) was
  reported arrived by the bot while the server's position said otherwise; `(destination not set)` is
  a move the bot really asked to (0, 0).

## Limits

- Hits in `activity --skills`: an NPCHit on a unit the client attacked in the second before, else on the unit its latest cast at
  a point was meant for (the nearest monster within 30 degrees of the cast's direction and 30 of the client) within that skill's hit
  window, or within 5 of the client during its own skill move. A party member's hit on that unit is credited too.
- The combat incidents and skill moves count as attacks only attack packets with a known skill (none before the server's SetSkill)
  and not at a dying or dead unit; corpse skills (Find Item, Find Potion, Corpse Explosion, Raise Skeleton or Skeletal Mage, Revive,
  Grim Ward) are left out. A hit is an NPCHit, a monster's life lower in a later packet (a monster that does not flinch gets no
  NPCHit) or a death, as this client saw it, on the unit the cast was meant for or within 6 of its target - for a skill move, of its
  path - within the skill's hit window: 2 s Frozen Orb, Meteor, Guided Arrow, Bone Spirit; 2.5 s Blizzard; 3 s Hydra; 1.5 s
  Whirlwind, Blessed Hammer, Charged Bolt, Fire Ball, Immolation and Freezing Arrow; 1.2 s any other. A party member's hit there
  counts, so these incidents undercount in a party.
- Collision: the map grid holds Diablo 2's collision bits. Bit 0x1 blocks walking (odd values), bit 0x2 blocks line of sight and so
  missiles; `tiles` draws a cell with 0x2 as `#` (a wall) and one with only 0x1 as `~` (water, a ledge, the void: walking stops,
  missiles cross). Evidence: Travincal's temple edge is one row of 7 (0x1, 0x2, 0x4) with gaps, then three rows of 1 that arrows
  cross; the Chaos Sanctuary star is 17 (0x10 and 0x1) and its void 1, both shot across; its entrance edge is 5 (0x1, 0x4) between
  pillars of 7. The bot's `AttackService.IsInLineOfSight` blocks every odd value but 1, so it differs on 5, 17 and 37. The recorded
  games cannot settle 5 and 37: in a party a hit on the target by anyone counts, so casts across 0x2 cells land as often as others
  (the bow amazon, 92%). Values seen: Travincal 0, 1, 3, 5, 7, 16, 23, 37; Chaos 0, 1, 5, 7, 16, 17, 23.
- `attack into a wall` (not skill moves - see `skill move went nowhere`, which names a path across any unwalkable cell) needs the line
  from the caster to the target to cross a `#` cell and no hit in the window; one incident runs until a cast of the client lands or
  5 s pass. A Water Watcher in water is no longer a wall (46 of 71 on 13 Nov 2026 were); casts at it that hit nothing show under
  `attacks hitting nothing` (8 or more attacks over 3 s or more, none 3 s apart, none followed by a hit), which also catches what the
  map cannot prove, and is all that remains without the map service.
- A skill move is the Skillmove effect on the client's own player from added to removed (Whirlwind, Charge, Leap) or a Teleport cast
  answered by a reassign; its positions are the server's (life updates every 0.4 s, reassigns), so a short move between two of them
  may read as shorter. Its mana is the falls charged to it: each fall once, to the earliest move ended at most 0.8 s before its packet
  that has no charge yet, split over those when it is 1.5 times the median fall or more; mana before is the previous move's mana after
  while a fall charged to that one is still to come.
- `mana` reads mana from the life and mana packets (0x95, 0x18) only, which come every 0.4 s and lag the casts. A cast explains a fall
  when one was sent in the second before, so with the bot sending casts every 100 ms nearly every fall is a cast; mana burn needs a
  ManaBurn monster's attack (0x6C, 0x4C) naming the client in that second. Leech is a rise within 0.5 s after (0.1 s before) a hit
  its own casts may have landed, as above. A mana potion refills over 5 s, and one drunk while another refills queues behind it
  (measured: two or three drunk moments apart kept mana rising at the same rate for 8 to 9 s), so "potions wasted" for mana potions
  means drunk early, not lost.
- `loot left behind` knows an item was taken only from the item packets of the game's recorded clients (the picker's own
  0x9C, or a 0x9D of it on anyone); an item a client saw leave the ground within 40 and come back is a refused pick-up, not a
  take. Alone, a client counts one taken off the ground within its sight as taken.
- `find` over a root folder needs a date in `--at`; a clock time alone is matched against the date folder's games only.
- Bot settings: the bot writes a `settings` event to the sidecar each time a client enters a game, from config: botType, monsterMotion
  (configured), monsterMotionEffective and the switches its type lists in `ConsoleBot/Helpers/TracedSettings.cs`, each with the value
  in effect, defaults included (only listed keys - a section also holds accounts). A new switch goes into that list. Travincal's
  `wallAwareWhirl` switch is gone: games recorded while it existed carry it (false before the trav-sim merge), every Travincal game
  since fights wall-aware and records no such key, so `--where wallAwareWhirl=none` keeps them (and the games from before settings were traced). Older games have only monsterMotion and
  monsterMotionEffective, from the startup log line of every sidecar of their process key (two bots started in the same second share
  it: "a|b" when they differ), and a game whose clients disagree shows "a|b". Nothing in the replay reads them, so no client rules
  version changed.
- Cause lines (`activity --phases`, from 5 games of a client on) read the game's incidents without the map checks (so `attack into a
  wall` is not among them; went-nowhere and hitting-nothing are), its phases against the median game's and the client's item ledger.
  Seconds explained, never more than the window: the merged spans of the wall, skill-move and hitting-nothing incidents; the rest of
  the window after a death, chicken, closed connection, silent server, stranger exit or refused portal (the bot's own failure lines:
  "Moving to town failed", taking or creating a waypoint or portal that failed); the spans of record gaps, path and accuracy
  incidents; the town phase over the median for gambling (items gambled), muling (items traded), vendor trip (bought, sold,
  repaired) or a long town stay. A death is always first. A failed step is judged on its own window, without phases: whatever of
  those terminal causes falls in its last 5 s ended it and comes first, with that incident's time; a step that never ended with
  nothing to explain it reads "trace ends mid-step". Checked on
  14 and 15 Nov 2026: the slowest Travincal games are gambling, the aborted runs are stuck at the council wall or ended by a chicken or a
  refused town portal.
- Slow-step and new-warning checks compare with earlier games of the same bot and stay silent below
  10 earlier games in 7 days or with `--no-history`; the output then carries a note saying so.
- `incidents` writes a history cache in `%LOCALAPPDATA%\D2NG\Trace` (`--no-history` writes and reads none);
  its map checks and `tiles` need the map service on `https://localhost:8080` and cache maps in
  `%TEMP%\d2trace-maps`, which grows by tens of MB per day of games (`--map-cache` moves it; `D2NG_MAP_CACHE` sets the default, which keeps it off a full system drive).
  Without the service those checks are skipped, not failed.
- `state` places monsters by the legacy movement model unless `--motion` says otherwise, whatever the bot ran with; `unit`
  shows the positions the server actually sent. `--motion simulated` needs monster speeds in the data folder, else it is legacy.
- A client only receives units within about 55 units, so a monster missing from `state` may simply
  be out of sight (`d2-109` skill).
- A `trace gap` incident means records were dropped; state after it is unreliable.
- `items` works from the packets alone. A request is answered by the first fitting server packet within 5 s, and
  one sent again while open is a repeat of it (timed from the first, expiring 5 s after the last); one sent again
  after the server answered it is a `repeat`, not `UNANSWERED`. The items a character joins with are one
  `loaded` line. Gold comes from the vendor answer (0x2A, gold on the person after) and the gold attributes and
  adds (0x19): a purchase whose before-figure the server never refreshed says `price unknown`, and purchases paid by
  one stash debit share it by the price table (`item-prices.json`), evenly without it. The attribute reports what
  moved since its last report a moment later - as an add when 1-255 - so an add after vendor answers first repeats
  what they moved; only the rest is new gold. A gold pile is
  `picked up` by its own 0x0A plus the pick-up sound only the picker gets; `gold received` is what the person's
  gold actually rose by - in a party the realm splits every pile among the members, so a picker gets a share and
  the others get theirs unasked. A fall of the person's gold is `dropped` (a 0x50 the bot sent, e.g. gold over its
  carry limit or for the party) or `gold lost` (on a death, or unexplained); one matched by the stash rising is a
  deposit and not listed. Per client the gold fields of `items --json` sum to the change of person plus stash; on
  13 and 14 Nov 2026 they did for all 532 client-games. `used` is a 0x3F whose item then went away; the cube and tomes are `activated`.
  `transmuted` lists the cube items the server took and put within 1.5 s of the request (0x4C or the cube's
  button, 0x4F 0x18).
- The item state check holds the client's containers, belt, body slots and cursor against where the server's
  own packets left each item (cell, body slot, cursor), one second after the server's last word on it, every put
  against the grid the client kept, and each grid's marked cells against its items. On 13 and 14 Nov 2026 it
  found only real errors of the client under rules 1: an item the server took unasked (a quest item, Khalim's
  flail) or took off unasked (an emptied quiver, the body on a death) left on the client's cursor. From rules 4
  (`QuiverCountFollowsServer`) a worn quiver's count follows the server, so an emptied one no longer goes on the
  cursor; a replay under rules 1-3 still reports it as `cursor not given`.
- A typed event that names a game belongs to it for up to 10 s after the game's last packet, or until the process's next game
  starts if that is sooner: the bot ends its run step after a connection the server closed or a long town trip. When the next game
  has the same name, events naming it stop at this game's last packet. Log lines without a game name stop at the game's last packet
  when the next game starts within 10 s. The history cache was bumped for this (version 4).
- Log lines and bot events come from the `events-*.jsonl` sidecars in the date folder. A game
  recorded before the sidecar existed has packets only.

## Typed trace events

When the question is *why the bot chose* something the packets cannot show - which target it passed
over, what it waited for, where a step began and ended, how a move ended - add an event at that
decision in bot code (`src/D2NG.Core/Tracing/TraceEvents.cs`):

```csharp
TraceEvents.Step(game, name, StepPhase.Start);  // and StepPhase.End with attempt, outcome, reason, duration
TraceEvents.Target(game, unitId, code, reason, rejected);
TraceEvents.Wait(game, reason, entered: true);   // and entered: false when the wait ends
TraceEvents.Move(game, from, to, MoveKind.Walk, MoveOutcome.Arrived, fallback);
TraceEvents.Decision(game, name, ("key", value), ...);
```

Bot work first, trace after: the call sits after the action it records and never gates it. The calls
cannot throw and do nothing when tracing is off, so they need no guard; pass a lambda for a reason or
outcome that costs work, and it is computed only while tracing.

## When the trace is not enough

- Debug log lines are in the trace only when the bot ran with `logLevel: "Debug"`
  (`timeline --debug` shows them).
- A decision nobody logged or traced is invisible: add a typed event, rerun, and read it with
  `timeline`.
- Packets of a real game client, for bytes the bot has never sent, come from a sniffer capture
  (`d2-bot` skill, `DEBUGGING.md`). The sniffer writes traces (source Sniffer) that every command reads;
  `import-capture` turns an old text capture into one, and `compare <trace A> <trace B>` shows where a real
  client and the bot part for the same interaction.
