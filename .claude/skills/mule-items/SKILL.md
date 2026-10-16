---
name: mule-items
description: Search the mule database for items and judge them against what 1.09 can roll, classic or expansion. Use when hunting a gear upgrade for a character, working out a character's resists or damage, weighing +skills, leech or gold find, asking what the mules hold, or judging whether a magic or rare roll is good.
---

# Mule items

An upgrade is only an upgrade against the item it **replaces**: every answer is a delta on a
slot, priced in the stats the character lives on, and checked against the resists it leaves.

`mule_items.py` beside this file does the lookups and the arithmetic. Run it as
`python .claude/skills/mule-items/mule_items.py --bot-folder <bot folder> <command>` (or set
`D2_BOT_FOLDER`). It reads the connection string from `<bot folder>\muleconfig.json`, counts the
accounts in `muleconfig-expansion.json` as expansion and every other account as classic, and takes
bases and skills from `item-affixes.json` and `skills.json` in `<bot folder>\data` (written by
`tools/MpqData`). `--help` lists every command and option.

## Steps

1. **Read the character.** `gear <character>` prints realm, level, base attributes, every equipped
   item, each extra resist source it found (below) and the resists after the difficulty penalty.
   `account <account>` lists an account's characters. Done when you know the realm, the item being
   replaced and the resists the character stands on.
   When the user quotes an in-game number that `gear` does not reproduce, find the missing source
   before going on: the user's stat screen is the ground truth.
2. **Price the slot.** Turn the request into `--has`, `--min` and `--weights` built from the stats
   on the replaced item plus what the user asked for. A stat the user wants traded away gets weight
   0 or a low one; `Res` is the sum of the four resists. For damage, pass `--skill "<main attack>"`
   and weight `Damage` (average per hit); `MinimumLifeStolenPerHit`, `<Skill> Level`, `ExtraGold`
   and `BetterChanceOfGettingMagicItem` weigh the rest. `Stat=W@cap` stops a stat counting above
   cap, for stats where a little is enough.
3. **Search.** `items --type <ClassificationType> --quality Rare,Magical,Set,Unique,Crafted
   --realm <realm> --has ... --weights ...`. Run the unique/set search separately too: those carry
   no affix names, so recognise them by their stat set.
4. **Combine.** When one item cannot hold the resists on its own, let another slot make up the
   difference: `combo <character> --replace "<slot>=<item id>" --slot "<slot>=<ClassificationType>"
   --weights ...` searches the open slots. Every combination must meet the strength and dexterity
   its bases need and keep the resists the profile guards (below); `--min-total` adds a stat the
   slot must keep (FRW on boots). `calc <character> --replace "<slot>=<item id>"` prints the
   before/after of one exact swap.
5. **Confirm the realm** of each pick's holder with `realm <character> ...`. Items only trade within
   a realm, and the account split in the configs is a convention, not a guarantee.
6. **Compare.** Done when every pick has a `calc` line showing its totals and resists, and you have
   named one pick and the alternative for the other priority (usually dexterity or damage versus
   all four resists at max).

## What each class values

The user's priorities, which the weights should follow:
- **Sorceress**: life, mana, max resists, cast-speed breakpoints, hit-recovery breakpoints, high-block
  shields, and +skills for the tree she plays (weight `<Skill> Level` for her main skills).
- **Necromancer, paladin**: life, resists, high-block shields, hit-recovery breakpoints.
- **Barbarian**: attack rating, damage, life leech, resists, hit-recovery breakpoints.
- **Amazon**: damage, life leech, attack rating, resists, hit-recovery breakpoints.

Breakpoints are stats in their own right: `Cast Frames` and `Hit Recovery Frames` (lower is better,
so weight them negative) move only when a breakpoint is crossed, from the class tables in the
script (the published 1.10 tables, not yet checked on a 1.09 screen). `Block %` is (shield block +
increased blocking + class block factor) x (dexterity - 15) / (2 x level), capped at 75; Holy Shield
and other skills are not counted, and the formula is unconfirmed on a 1.09 screen.

## Resists

**Max resists come first**, in the PvP consensus the user plays by: fire, cold and lightning at
the cap, cold above it as well (a buffer against cold damage and freeze), poison not at all.
`combo` defaults to that profile: those three never drop below where they stand, each point under
the cap scores `--res-weights` (Fire=1,Cold=1,Lightning=1,Poison=0), and cold over the cap scores
`--overflow` (Cold=0.3). Fire and lightning over the cap are what a swap can spend. The cap is 75
plus any maximum-resist bonus (expansion items).

The difficulty penalty is per realm: classic 0 / −20 / −50, expansion 0 / −40 / −100 (normal /
nightmare / hell). The classic Hell figure was confirmed against a character's stat screen.

What `gear`, `calc` and `combo` add up:
- **Equipped items**, slots 1-10. Socketed gems, jewels and runes and runeword stats are already
  folded into the item.
- **Set bonuses.** They sit outside the item's stats (`main_set_bonuses`, and
  `main_set_partial_bonuses` on each piece). They count while every piece stays on and drop when
  one is swapped, which is exact for a two-piece set and pessimistic for a larger one.
- **Inventory charms** (expansion). Charms in the stash or cube do nothing.
- **Anya's scroll** (expansion, Prison of Ice): +10 all resists for each difficulty the armory marks
  `consumed_scroll`.
- **Natural Resistance** (barbarian, both realms): `skills.bin` (patch_d2.mpq, record 153) gives
  min 0 / max 80 on the integer diminishing curve `(110·lvl / (lvl+6)) · 80 / 100`, so levels 1-20 read
  12 21 28 35 40 44 47 49 52 54 … 67. The level is the hard points plus all-skills,
  barbarian-skills and Combat Masteries bonuses on the gear being judged, so swapping a +skills
  item moves it. `gear` prints the armory's own level beside the computed one; they must agree.

Left out: the weapon-swap slots, the mercenary, and auras or buffs from the party.

Immunity is absolute on 1.09: nothing breaks it.

## Damage

`--skill` on `gear`, `calc` and `combo` computes one skill's damage per hit:
- **Physical**, as the character screen shows it: weapon base (patched `weapons.bin`; ethereal
  +50%) times the weapon's enhanced damage, plus flat damage from all gear, times
  `100 + stat bonus + off-weapon ED + skill %`. The stat bonus is the base's strength/dexterity
  bonus per point (bows 1% per dexterity; daggers and throwing weapons 0.75%/0.75% after the patch).
  **A weapon with enhanced damage multiplies base + 1**, not the base: `(base + 1) × (100 + ED) / 100`.
  That matched 98 of 99 ladder weapons whose damage the armory computes (the item's `damage`
  block), and a character screen to the point for both a plain attack and Guided Arrow.
  A barbarian sword held with the other hand empty uses its two-handed damage.
- **Weapon masteries** add 28% at level 1 plus 5% per level after to the multiplier, confirmed to
  the point on screens for Sword 22 (one-handed), Mace 21 (two-handed maul) and Axe 23 (dual
  wield). Hammers and maces get 1.1% damage per strength point (`StrengthBonus` 110).
- **Smite** hits with the shield's own damage (patched `armor.bin`; Barbed Shield 18-35, a
  Gothic Shield 2-6), times 100 + Smite's percent (15% + 15% per level after the first) + auras;
  strength and the weapon do not count. **Auras** (`--aura Fanaticism`, `Might`, `Concentration`)
  add their damage percent at the level the gear gives them; Fanaticism is 50% + 17% per level
  after the first, plus attack rating 40% + 5% per level and attack speed 10-40% on the diminishing
  curve (526% / 180% / 37% at level 29, all three confirmed by the skill screen). Smite was confirmed
  to the point on a screen with Fanaticism running; a screen reading taken
  with an aura on needs the same `--aura` to compare.
- **Attack rating** (`Attack Rating`): flat AR from gear (the hand's own weapon only) + 4 per
  dexterity point above 7 + the class's base to-hit (charstats `ToHitFactor`: barbarian and paladin
  20, amazon and druid 5, assassin 15, necromancer -10, sorceress -15), times 100 + mastery AR % +
  gear AR % + the skill's own to-hit. Mastery AR is 28% at level 1 (sword, axe, mace) or 30% (spear,
  pole arm), plus 8% per level after. The 1.10 code counts 5 per dexterity point; four 1.09 screens
  matched only at 4. Confirmed on five readings (sword, axe both hands, mace, spear). For the
  amazon, Penetrate and bow attack rating are not confirmed yet.
- **Defense** (`Defense Total`): each armor piece's defense times its enhanced defense, plus all flat
  defense, plus dexterity / 4, times Iron Skin (30% + 10% per level after the first). An item with
  enhanced defense always rolls the base's maximum + 1; a mule item without it is counted at the
  middle of its range, because the database keeps no roll. Confirmed to the point on one screen.
- **Dual wield**: each hand uses its own weapon's damage, attack rating and +skills; the other
  weapon's strength and other stats still count. Confirmed for the main hand; the second hand's
  screen reading is not reproduced yet.
- When a reading will not fit, check the character's last parse first: two characters whose
  snapshots predated their current gear read about 1% off, while every freshly parsed one matched.
- **Damage**: physical averaged with Critical Strike (same curve as Natural Resistance, 5-80) and
  Deadly Strike, which double it, plus the elemental damage gear adds and an elemental skill's own
  damage (EMin/EMax with per-level damage for levels 2-8, 9-16 and 17 on).
- **Leech stays a percentage** (`Life Leech %`, `Mana Leech %`); never quote it as life or mana
  per hit. The game leeches from the damage the target actually takes: capped at its remaining
  life and taken after its damage reduction (D2MOO `SUnitDmg.cpp`), so no per-hit figure is true.
  Life leech matters more than mana leech, in PvP especially; a few percent of mana leech is enough
  for most physical characters, so cap its weight (`MinimumManaStolenPerHit=3@4`).
- **Skill levels** are hard points plus all-skills, class-skills and the skill's tab, and must
  match the armory's own level, which `gear` prints beside them.

1.09 keeps each skill's damage rule in the game code, not the tables. `SKILL_PERCENT` in the
script holds the attack skills read from their parameter descriptions (base + per level after the
first). Guided Arrow (+5% per level after the first) is confirmed on a character screen; the others
are not yet, so when a decision rests on one of them, ask for the screen damage of that skill and
fix the rule until it matches, the way the base + 1 rule was found. Screen readings are only
comparable to the armory when the gear has not changed since the character's last login. Not
modelled: Throwing Mastery (`--skill-bonus` adds a percent), auras, attack speed (IAS and weapon
speed are shown, frames are not), and hit chance against a specific target's defense. The mule DB
keeps only the minimum of "adds X-Y" elemental damage, so a mule item's elemental damage is
undercounted.

## The database

Marten documents in Postgres (`mt_doc_muleitemdb`, one jsonb `data` column per item):
`Id` (what `--replace` takes), `AccountName`, `CharacterName`, `ItemName` (base, e.g. `RuneBow`),
`QualityType` (`Magical`, `Rare`, `Crafted`, `Set`, `Unique`, `Normal`, `Superior`),
`ClassificationType` (`Ring`, `Amulet`, `Boots`, `SmallCharm`, `Jewel`, ...), `Level` (item level),
`Sockets`, `Ethereal`, `Stats` (`[{Type, Value}]`) and `StatTypes`. `mt_doc_mulecharacterdb` holds
`SeenAt` per mule: a row is only as fresh as that visit.

Reading the stats:
- Min/max damage appear as plain, `Throwing` and `Secondary` (the two-handed stat, where a bow's
  flat damage lives); the script keeps the largest as `MinimumDamage`/`MaximumDamage`.
- Weapon enhanced damage is `EnhancedMaximumDamage`, and applies to both ends.
- Res-all is stored as four single resists, so a Prismatic 15 plus Coral 25 reads as lightning 40.
- Uniques with +all skills also list every class skill; the script drops those copies.
- Uniques and sets have no name. The 1.09 Stone of Jordan is the `Mana, EnhancedMana,
  MinimumLightningDamage, AllSkills` set.

The armory (`armory.diablo09.com/armory/api/armory?character=<name>`, `account?account=<name>`)
re-reads a character every time it logs in, so its parse date (printed by every command) is the
last login and the gear shown is current however old that date is. `StatType` has four extra entries from id 88 on, so ids 88-160 map four places down the enum
(IAS 93, FRW 96, FHR 99, FCR 105, all skills 127, deadly strike 141); past 160 the script keeps
the armory's label. Armory +tab labels are renamed to the database's `<Class><Tab>Skills`.

## What good looks like on 1.09

The realm's tables are in `<bot folder>\nips`:
- `affixes-classic.md` / `affixes-expansion.md`: per item type, every affix, its range, level and
  group. The authority on what can roll on a slot.
- `PICKIT-RESEARCH.md` section 4: the reachable maximum per slot, and section 3 the group rules.
- `LADDER-ARMORY.md`: what top ladder characters actually wear, per class.

A rare carries at most three prefixes and three suffixes, one per group. On rings and amulets the
offensive stats compete for the suffix slots: life (of the Mammoth, 40), dexterity (of Precision,
15), strength (of the Giant, 15), life leech (of the Leech, 7 classic / 8 expansion), minimum damage
(of Excellence, 9), max damage, mana leech. Resists, AR (Meteoric 150), res-all (Prismatic 15, ilvl
62) and mana are prefixes. So swapping one suffix stat for another keeps the rest of the item's
shape, and a ring with four wanted suffix stats does not exist.

The server's rolls can exceed the client bins (5% FRW small charms exist), so a roll outside the
affix tables is a reason to look twice, not proof it is impossible.

## Hand-off

Name the holder (account and character) and the item id of each pick so the user can fetch it;
moving the item is a trade, not part of this skill.
