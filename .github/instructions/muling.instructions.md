---
description: "Use when editing ConsoleBot muling flow, mule rule/filter matching, stash-full triggers, or mule configuration behavior."
applyTo: src/ConsoleBot/Mule/**/*.cs, src/ConsoleBot/TownManagement/**/*.cs
---
# Muling Instructions

- Preserve the current separation of concerns:
  - Mule rule/filter matching in Mule domain types.
  - Trigger and town-state decisions in TownManagement.
- Keep mule decision semantics stable unless requested:
  - Rule matching precedence and filter interpretation.
  - Stash-full and "needs mule" trigger behavior.
- Do not broaden or narrow what gets muled by default through implicit logic changes.
- Keep configuration schema compatibility in mind when changing mule config models.
- If uncertain about item-routing criteria, trigger timing, or precedence between filters/rules, ask the user before changing behavior.
