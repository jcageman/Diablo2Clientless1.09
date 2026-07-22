---
description: "Use when editing ConsoleBot Pickit item selection and keep logic (Classic/Expansion split, stat thresholds, and rule gating)."
applyTo: src/ConsoleBot/Pickit/**/*.cs
---
# Pickit Instructions

- Preserve the existing method pattern per item type:
  - `ShouldPickupItemClassic`
  - `ShouldPickupItemExpansion`
  - `ShouldKeepItemClassic`
  - `ShouldKeepItemExpansion`
- Keep Classic and Expansion behavior explicit; do not silently merge mode-specific logic.
- Follow current threshold style: direct, readable condition chains with minimal indirection.
- Reuse existing helper/stat access patterns (`GetValueOfStatType`, `GetTotalResistFrLrCr`, class-specific life totals) instead of inventing parallel helpers.
- Keep rule ordering intentional. Existing ordering often encodes value priority.
- If a change alters stat thresholds, mode boundaries, or keep/drop semantics and intent is not explicit, ask the user before applying it.
