# AGENTS.md

## Building

The start scripts in `D:\projects\diablo2bot` run
`src\ConsoleBot\bin\Release\net10.0\ConsoleBot.exe`, so that binary is the only build that counts:

```
dotnet build D2NG.slnx -c Release
```

Done when `ConsoleBot.exe` is newer than your edits. Confirm that before asking anyone to start a
run: a stale binary keeps running the old code silently, and the run then reads as a behaviour bug
in whatever you just changed.

A live run holds the exe and Release fails with MSB3027. Build `-c Debug` to keep checking that the
code compiles, and rebuild Release once the run stops. Leave `-p:BaseOutputPath` out of it —
redirecting the output makes the build pass while the stale binary stays where the scripts look.

When two bots need to be worked on at once, give one of them its own binary instead of taking turns:

```
dotnet publish src/ConsoleBot/ConsoleBot.csproj -c Release -o "D:/projects/diablo2bot/meph-build" --artifacts-path "<scratch dir outside the repo>"
```

`--artifacts-path` is the part that matters: with `-o` alone the intermediates still go through
`src/*/bin/Release` and the build fails with MSB3027 anyway. With both, nothing in the repo tree is
written, so this builds while another bot is mid-run out of the shared folder. The stale-binary trap
above still applies and is only avoided by launching the new folder's exe explicitly — see
`start_meph_isolated.ps1`, which does that, rather than going through `start_classic.ps1`.

The realm is still shared even when the binaries are not. Game creation is where that bites: four
bots cycling ~30s runs create games far faster than a single-run bot, and a batch running alongside
someone else's will land join refusals in their failure counts.

## Running tests

```
dotnet test --solution D2NG.slnx -c Release
```

Flags are forwarded to the test executable, which rejects any it does not own. `--nologo` is the
trap: the app exits 5 with `unknown option: --nologo`, and `dotnet test` then reports
`Zero tests ran` for **every** project, which reads as broken projects rather than a bad flag. Exit
code 5 always means a rejected argument, so check what is being forwarded before touching project
files.

## Tests

`src/ConsoleBot.Tests` holds the game-free logic lifted out of `ConsoleBot`. To test a bot class,
extract the pure part rather than reaching through a `Client`. New projects go in `D2NG.slnx`.

## Measuring the cow bot

```
python D:/projects/diablo2bot/scripts/cow_kpi.py src/ConsoleBot/bin/Release/net10.0/testlog.txt
```

The KPI is experience per minute of wall clock. Experience is level wide, so one character's
gain measures what the whole party killed, and it prices the tradeoff the kill count cannot see:
mopping up three stragglers spread across the map earns almost nothing for the minutes it costs,
while a dense clear scores high. Judge it over ten games at least - per-game rates swing widely
with how the packs fall, and a promising eight-game figure has twice failed to survive to
seventeen. The report also breaks down idle time, holds, deaths, chickens, potions per character
and attacks per character, which is where a bad number gets explained.

`--character` picks whose experience to score; by default it is whoever was sampled in the most
games, so a character that died or chickened part way through a batch does not become the
yardstick. Experience comes off packets 0x1A-0x1C, not attribute 0x0D which the server never
sends, and what arrives on joining is the lifetime total - so the figure logged is a running
total and a game's gain is the difference between two samples of it. Sampling starts when a
character reaches the cow level, so the seconds of town setup are counted in the wall clock but
anything killed on the way in is not.

Muling is excluded: a game starts at the first `Joining game:` line, not at `Joining next game`,
because a mule detour sits between the two.

`ConsoleBot.exe` deletes the log at startup, so copy it somewhere before restarting the bot if the
batch still matters.

## One-off tooling

`ConsoleBot` is the farming bot. Setup and experiment code - creating mule characters, probing
packet formats, one-off migrations - does not belong in it. Reusable tooling gets its own project in
`tools/` (`MpqData`, `MpqDump`). Analysis scripts and true one-offs live outside this repo, in
`D:\projects\diablo2bot\scripts`, so the bot stays the thing that farms and the repo does not
accumulate scripts that only ran once.

## Muling

Rounds are planned by `D2NG.Mule/Packing` (planner, server room check, stash packer, repack) and executed by
`ConsoleBot/Mule/MuleTransfer`. Every mule pass writes the farmer's and each mule's containers as JSON to
`mule-fixtures/` next to the log; load one with `MuleFixture.Load` to replay a real situation in
`D2NG.Mule.Tests`.

## Game data

The game data files live in a `data` folder beside the run configs (`D:\projects\diablo2bot\data`),
not in the build. `ConsoleBot` resolves that folder from the `--config` file's directory, overridable
with `--datadir`, and a file missing there falls back to a `data` folder beside the assembly - which
is how the test projects supply their own copies. `GameDataLocation` is the only thing that knows
this; do not build data paths by hand.

`item_data.txt` and `item_properties.txt` are required: no item can be parsed without them, so
startup fails immediately if they are absent rather than dying on the first drop. They are not
generated by anything, so `src/D2NG.Core.Tests/data` keeps a copy for the tests. `PacketSniffer`
decodes item packets too and needs `datadir=<folder>` for the same reason.

`monster-resists.json` is optional and generated. Regenerate it after the realm patches:

```
dotnet run --project tools/MpqData -c Release -- --gamedir "C:\Diablo II 1.09" --out "D:\projects\diablo2bot\data"
```

Without it the bot still runs, logs a warning, and falls back to the immunity cases hardcoded in
`AttackService` and `RushBot`, so a missing table shows up as slower kills rather than a crash.

The same run also writes `shrines.json`: the shrine rows of `patch_d2.mpq\data\global\excel\objects.bin`
(record index = object class id, Parm0 dword at +392 in 1.09's 464 byte records) as `ShrineTable`.
Parm0 is what decides the shrine: 1 always health, 2 always mana, 3 rolled from `shrines.txt`
(`ShrineType`) when the level is populated. Do not go by the row name - plain "Shrine" rows include
fixed health (84, 206) and mana (164-168) shrines. `areaMap.Shrines(table)` lists the preset shrines
of a level with their positions for the current seed; shrines from a level's random object groups
(Travincal's) are not preset and only exist as world objects once seen. Without the file shrines are
simply not recognised.

The same run writes `item-affixes.json` (`ItemAffixTable`): magic prefixes, suffixes and automagic,
item types, bases and cube recipes, decoded from the patch bins. Add `--report <dir>` to also get
`affixes-classic.md` and `affixes-expansion.md`, which list per item type every affix that can roll,
for magic and for rare/crafted, with the best range of each property. Those are the reference for
pickit rules - they live in `D:\projects\diablo2bot\nips`. The d2exp txt files are an older revision
than these bins (fewer item types and bases, different ranges and levels), and the bins carry
recipes for items no wiki knows (`rcr`, `rca`, `rce`, `rcp`), so take affix values from the report
and not from a wiki. Nothing reads `item-affixes.json` at runtime yet.

The server rolls items from its own tables, and those do not always match the client's
`patch_d2.mpq`. The bin says small charms roll 3% faster run/walk ("of Inertia"), yet the realm
hands out 5% ones (77 in the mules, 63 on top ladder characters). Nothing local explains it: the launcher
`Diablo 09.exe` is an Avalonia UI app with no game data inside, and there are no loose excel
overrides. Treat the affix report as a strong guide, and real items (the mule database, the
armory at `armory.diablo09.com/armory/api/armory?character=<name>`) as the ground truth: never
call a roll impossible on the bins alone. The armory folds socketed runes and jewels into an
item's own stats (a Lem rune adds 50 gold find), so subtract them before reading a roll off it.

Read `patch_d2.mpq\data\global\excel\monstats.bin`, never the readable `monstats.txt` in
`d2exp.mpq`. The patch archive overrides the txt and they disagree on Hell physical resistance for
502 of 575 monsters, and the txt has separator rows that break the class-id alignment from about row
417. The bin's record index is the class id the server sends, which is what `NPCCodes.cs` is indexed
by.

On 1.09 immunity is absolute: Conviction and Lower Resist only started breaking immunities in 1.10,
so resistance >= 100 means that element does nothing at all.
