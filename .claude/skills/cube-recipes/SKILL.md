---
name: cube-recipes
description: Horadric Cube recipes of this realm and the items each one accepts. Use when planning a transmute, cubing runes or gems up, rerolling or socketing an item, crafting Blood/Caster/Safety/Hit Power items, adding a cube bot recipe, or deciding whether an item is worth keeping as a cube input.
---

# Cube recipes

The realm's recipe table is `cubemain.bin` from `patch_d2.mpq`, extracted by `tools/MpqData` into
`<bot folder>\data\item-affixes.json` (`CubeRecipes`, 300 rows; `#n` below is the row index). That
file is the source of truth; this skill caches what is slow to read out of it.

## Steps

1. **Look the recipe up.** `cube_recipes.py` beside this file resolves every input code to the items
   it accepts:
   `python .claude/skills/cube-recipes/cube_recipes.py --bot-folder <bot folder> show <#>...`,
   `uses <item>` (every recipe the item can go into), `makes <item>`, `list`. `uses` matches the code
   a row names, so look an exceptional or elite base up by its normal-tier name (Crafting table). Done
   when each input of the recipe is named down to quality, tier and count.
2. **Count the stock.** Check the mules for every input with the `mule-items` skill or the mule
   database before planning a run. Done when the scarcest input is known: it caps the run.
3. **Prove it on the realm once.** The server rolls from its own tables, which do not always match
   the client's bins (AGENTS.md, Game data). Before the cube bot repeats a recipe it has never run,
   watch one transmute succeed and read the result. Done when the logged result matches the table.

## The recipes that matter

Only recipes with a use on this realm: something we farm, keep or cube today.

| Recipe | Inputs | Result | Row | Use here |
|---|---|---|---|---|
| Gem upgrade | 3 of one gem or skull at one tier | next tier | #22-49 | cube bots run Flawless → Perfect for crafts and rerolls |
| Rune upgrade | see Rune chain | next rune | #50-58, #99-121 | cube bot runs Lum→Ko, Fal→Lem, Lo→Sur→Ber→Jah |
| Sword reroll | any sword (white or magic) + 3 Chipped gems or skulls, any mix | magic sword, same base | #15 | cube bot "Sword reroll"; table says up to 6 sockets, 52 rolls of a 3-socket Colossal Sword all kept 3 |
| Magic reroll | any magic item + 3 Perfect gems or skulls | magic item, same base, new affixes | #59 | grand charms of item level 91+ (kept for this), magic amulets and rings |
| Rare reroll | any rare item + 6 Perfect Skulls | rare item, same base | #60 | |
| Socket a magic | magic without sockets + 3 Perfect Skulls + Stone of Jordan | the same item, 2 sockets | #125 | expansion |
| Socket a rare | rare without sockets + 3 Perfect Skulls + Stone of Jordan | the same item, 1 socket | #62 | |
| Socket a unique | unique without sockets + Standard of Heroes | the same item, 1 socket | #238 | Standards of Heroes drop and get kept |
| Crafted item | see Crafting | crafted, same base | | |

The realm adds reagents to the item tables: Rare Ruby, Rare Amethyst, Rare Emerald, Rare Saphire,
Rare Skull, Rare Diamond, Rare Topaz (`rcr rca rce rcp rcs rcd rct`). Each replaces a whole recipe's
gems: Rare Skull + rare item rerolls it (#239), Rare Diamond + magic item rerolls it (#240), Rare
Topaz + rare without sockets adds one (#241), and the four gem reagents craft (Crafting). None has
been seen in the mules yet. They are in `item_data.txt` as
classification `Reagent` (the bot's names say Sapphire, e.g. `RareSapphire`), the pickit keeps them
(`Common.nip`) and they mule with the gems (`gems.nip`).

## Rune chain

| Rune | From | Row | | Rune | From | Row |
|---|---|---|---|---|---|---|
| Eld | 3 El | #50 | | Lem | 3 Fal + Flawed Ruby | #108 |
| Tir | 3 Eld | #51 | | Pul | 3 Lem + Flawed Emerald | #109 |
| Nef | 3 Tir | #52 | | Um | 2 Pul + Flawed Diamond | #110 |
| Eth | 3 Nef | #53 | | Mal | 2 Um + Topaz | #111 |
| Ith | 3 Eth | #54 | | Ist | 2 Mal + Amethyst | #112 |
| Tal | 3 Ith | #55 | | Gul | 2 Ist + Sapphire | #113 |
| Ral | 3 Tal | #56 | | Vex | 2 Gul + Ruby | #114 |
| Ort | 3 Ral | #57 | | Ohm | 2 Vex + Emerald | #115 |
| Thul | 3 Ort | #58 | | Lo | 2 Ohm + Diamond | #116 |
| Amn | 3 Thul + Chipped Topaz | #99 | | Sur | 2 Lo + Flawless Topaz | #117 |
| Sol | 3 Amn + Chipped Amethyst | #100 | | Ber | 2 Sur + Flawless Amethyst | #118 |
| Shael | 3 Sol + Chipped Sapphire | #101 | | Jah | 2 Ber + Flawless Sapphire | #119 |
| Dol | 3 Shael + Chipped Ruby | #102 | | Cham | 2 Jah + Flawless Ruby | #120 |
| Hel | 3 Dol + Chipped Emerald | #103 | | Zod | 2 Cham + Flawless Emerald | #121 |
| Io | 3 Hel + Chipped Diamond | #104 | | | | |
| Lum | 3 Io + Flawed Topaz | #105 | | | | |
| Ko | 3 Lum + Flawed Amethyst | #106 | | | | |
| Fal | 3 Ko + Flawed Sapphire | #107 | | | | |

Price an upgrade in the rune it eats: a Lem costs 9 Ko, so cubing Lems competes with Wealth (Lem Ko
Tir) for Ko.

## Crafting

A craft is **magic base + Jewel + Perfect gem + the base's rune**, or the base + the family's
realm reagent (#242-299), which replaces jewel, rune and gem. The gem names the family and its
fixed mods; on top come 4 random rare affixes once the crafted item level passes 70.

Item level: the jewel recipes give half the crafter's level plus half the base's, the reagent
recipes 99. With a level 90 crafter a base needs item level 52 for 71+; `CraftBases.nip` keeps 60+.

The crafts we keep bases for (`CraftBases.nip`):

| Craft | Base (normal / exceptional / elite) | Rune + gem | Fixed mods | Rows |
|---|---|---|---|---|
| Blood amulet | magic amulet | Amn + Perfect Ruby | life steal 1-4, life 10-20, run/walk 5-10% | #78, #254 |
| Blood ring | magic ring | Sol + Perfect Ruby | life steal 1-3, life 10-20, strength 1-5 | #79, #255 |
| Blood gloves | Heavy Gloves / Sharkskin Gloves / Vampirebone Gloves | Nef + Perfect Ruby | life steal 1-3, life 10-20, crushing blow 5-10% | #74, #250 |
| Blood belt | Belt / Mesh Belt / Mithril Coil (we keep Mesh Belt: 4 rows, low strength) | Tal + Perfect Ruby | life steal 1-3, life 10-20, open wounds 5-10% | #75, #251 |
| Blood boots | Light Plated Boots / Battle Boots / Mirrored Boots | Eth + Perfect Ruby | life steal 1-3, life 10-20, replenish life 5-10 | #73, #249 |
| Caster belt | Light Belt / Sharkskin Belt / Vampirefang Belt (we keep the last two) | Ith + Perfect Amethyst | mana regeneration 4-10%, mana 10-20, faster cast 5-10% | #84, #266 |

The other families exist for every slot (Caster: Perfect Amethyst, Safety: Perfect Emerald, Hit
Power: Perfect Sapphire); `uses <base>` finds a base's row and rune when one becomes worth making.

## Names that trip matching

- The game data spells the gem **Saphire** and calls the bot's `ColossusSword` **Colossal Sword**;
  the bot's `ItemName` uses Sapphire and ColossusSword. Match on codes or on the bot's names, never
  on a mix.
- A tier word alone (Amethyst, Topaz) is the standard tier, between Flawed and Flawless.
- `upg` on an input accepts the exceptional and elite versions too, which is why the craft table
  names all three tiers.
