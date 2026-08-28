---
name: d2-bot
description: Build and debug clientless Diablo 2 1.09 bots in this repo. Use when writing or changing a bot or a step, when a character will not move, interact, travel or take quest credit, when a step times out, when adding an outgoing packet, or when reading D2GS logs and captures.
---

# Diablo 2 clientless bots

Bots drive a real 1.09 game server with no game client. The server is the only authority; everything
the bot believes is a cache that can be **stale**. Most failures here are a stale read, not a logic
error — suspect that first.

## The loop

Run a **tight** loop: one step, one change, one log read.

1. **Shrink the run.** Set `reuseExistingCharacter: true`, `character: "<name>"`, and trim `steps` to
   `["login","create","<step>"]`. Reuse prefers the configured character. This turns an 8-minute full
   pass into ~60s. Pick a character already in the right act.
2. **Run it** with `logLevel: "Debug"` — packet lines are the whole point.
3. **Read the packet log before touching code.** See `DEBUGGING.md`.
4. **Change one thing**, and say out loud which packet you expect to appear or change.
5. **Repeat until the step passes twice**, on two different characters.

Done when the step has passed twice and you can name the packet that proves each fix. A step that
passed once on one character is not done — seeds and layouts vary, and several bugs here surface only
on a fresh character or a second act.

## Stale state — the first suspect

Three separate bugs, one cause: the bot's cached view lags the server. Each has bitten in multiple
call sites.

- **`Game.Area` lags every transition** — portal, warp, resurrect, act change. It has reported
  `DurielsLair` for a character standing in town next to Warriv. Decide location by position:
  `RequestUpdate` → wait ~500ms → `IPathingService.IsNavigatablePointInArea(mapId, difficulty, expectedArea, Me.Location)`.
- **`Me.Location` lags movement.** Call `RequestUpdate(Me.Id)` at the top of each attempt, before any
  distance comparison. A character sat 23 units from a portal while its cache claimed 5, and clicked
  ten times into empty space.
- **Objects created or streamed during play are not pathable, and are streamed once.**
  `MoveToAsync` returns false instantly and sends nothing — an all-ping stretch of log with no
  movement packets is this bug. Reach them the way `EnterDurielsLair` does: alternate
  `MovementHelpers.MoveToWorldObject` every third attempt with `MoveToAsync`.

The streaming half has a second edge: the server sends an object to a client **once**, when that
client first comes into range, and walking there later does not make it resend. A second client that
needs the object must be walked into range *before* it matters.

Give area changes room. An act change reloads the act and takes seconds; a one-second inner timeout
re-fires the interact on a portal already taken.

## Escort model

Two clients: a high-level **rusher** and a low-level **rushee**. What the rushee must do itself:

- **Host the game.** The game inherits the host's quest state, so the rushee creates it.
- **Pick up its own quest items.** A character cannot pick up a quest item it has no use for.
- **Be in the same area as the boss when it dies.** Credit is area-bound. A kill with nobody eligible
  present burns both the game and the quest item that opened it.
- **Be verified as partied** — check membership, rather than assuming the invite landed. Send invites
  both ways.

A dead rushee still takes credit, but receives no quest pushes, so its state is unreadable until it
is resurrected — and resurrecting teleports it to town, which strands it away from any NPC or portal
it still needs. Ferry it back through a fresh rusher portal, deciding presence by position.

Movement mode is per-bot: `GetMovementMode` gates on `HasSkill(Teleport)` (and in `AssistBot`, level
> 30). A level 1 rushee always walks, so timeouts tuned for a teleporting rusher are too tight for it.

## Observed bytes

Send only bytes you can point to in a capture. When something does not land, retry the identical
packet a bounded number of times.

Guessing a field is the single most expensive mistake available here: a message id cannot be derived
from an NPC code, and two such guesses cost a session's worth of runs before being disproven. If the
bytes are not in a capture, go find a capture — `DEBUGGING.md` covers where they live.

## Environment

- **Start the map API first**: `D2Map.Api/bin/Debug/net8.0/D2Map.Api.exe`, serving
  `https://localhost:8080`. Runs fail immediately without it.
- **Run a bot** from `src/ConsoleBot/bin/Debug/net10.0`:
  `ConsoleBot.exe config=<path> muleconfig=<path> pickitconfig=<path>` — all three are required.
- **Tests**: `dotnet test` runs all 633 across `D2NG.Core.Tests` and `D2NG.Pickit.Tests`. Running the
  core test executable directly covers only 133 of them, so prefer `dotnet test`.
- **Accounts cap at 18 characters**, and a full account fails as `RealmLogin failed`. The `cleanup`
  step deletes exactly the names in `characterNames`, and only for accounts in `deletableAccounts`.
- Configs hold credentials. Reference them by path; keep them out of documents and commits.

## Reference

- **`DEBUGGING.md`** — reading packet logs, the grep recipes, decoding quest words, finding and
  searching captures. Read it whenever a step fails.
- **`PACKETS.md`** — the packet and quest facts already established: what each opcode carries, the
  quest bit meanings, and the traps in existing helpers. Read it before adding an outgoing packet or
  touching quest logic.
- **`%TEMP%\rush-bot-handoff.md`** — the rush bot's own history: solved chains, measured portal spots,
  what is still open. Project record, not technique.
