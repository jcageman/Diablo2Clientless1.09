# D2NG Copilot Instructions

## Scope
- This repository implements a headless Diablo II bot runtime targeting patch 1.09d.
- Use "Classic" and "Expansion" terminology in code-facing changes; "LoD" may be mentioned as an alias in comments or docs when useful.
- ConsoleBot behavior is packet-driven and depends on external map/navigation API services. Preserve these architectural seams.

## Work Style
- Keep changes minimal and local to the requested domain.
- Prefer existing project patterns over introducing new abstractions.
- If uncertain about gameplay intent, thresholds, rule precedence, or bot behavior, ask the user before changing logic.

## Domain Instruction Files
- Pickit rules: see [.github/instructions/pickit.instructions.md](.github/instructions/pickit.instructions.md)
- Muling rules and triggers: see [.github/instructions/muling.instructions.md](.github/instructions/muling.instructions.md)
- Bot type architecture: see [.github/instructions/bot-types.instructions.md](.github/instructions/bot-types.instructions.md)
