---
description: "Use when editing ConsoleBot bot architecture, bot registration, bot factory selection, or single-client vs multi-client bot lifecycle logic."
applyTo: src/ConsoleBot/Bots/**/*.cs, src/ConsoleBot/Program.cs
---
# Bot Types Instructions

- Preserve core bot contracts and startup patterns:
  - `IBotInstance` contract shape and behavior expectations.
  - Bot registration through `BotTypeExtensions`.
  - Selection and validation behavior in `BotFactory`.
- Keep lifecycle differences explicit:
  - Single-client flow remains in `SingleClientBotBase`-style loops.
  - Multi-client coordination remains in `MultiClientBotBase`-style orchestration.
- Avoid cross-cutting refactors that collapse bot-type boundaries unless explicitly requested.
- Keep bot names and type wiring deterministic to avoid runtime resolution regressions.
- If uncertain about lifecycle sequencing, event handling ownership, or bot-type behavior parity, ask the user before changing logic.
