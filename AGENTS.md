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
python tools/cow_kpi.py src/ConsoleBot/bin/Release/net10.0/testlog.txt
```

The KPI is burning souls per minute of wall clock. Judge it over several games only: per-game
rates range from 4 to 18 depending on how the packs fall, so a single game says nothing. The
report also breaks down idle time, holds, deaths, chickens, potions per character and attacks per
character, which is where a bad number gets explained.

Muling is excluded: a game starts at the first `Joining game:` line, not at `Joining next game`,
because a mule detour sits between the two.

`ConsoleBot.exe` deletes the log at startup, so copy it somewhere before restarting the bot if the
batch still matters.

## One-off tooling

`ConsoleBot` is the farming bot. Setup and experiment code - creating mule characters, probing
packet formats, one-off migrations - does not belong in it. Put such things in `tools/` as a
script, or in their own project, so the bot stays the thing that farms.

## Muling

Rounds are planned by `D2NG.Mule/Packing` (planner, server room check, stash packer, repack) and executed by
`ConsoleBot/Mule/MuleTransfer`. Every mule pass writes the farmer's and each mule's containers as JSON to
`mule-fixtures/` next to the log; load one with `MuleFixture.Load` to replay a real situation in
`D2NG.Mule.Tests`.
