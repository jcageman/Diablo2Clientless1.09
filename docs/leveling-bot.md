# Self-leveling party bot

A party of characters that starts at level 1 and plays itself to the end of Hell, without help from
anything outside the party. Agreed design, November 2026.

## Goal and rules

- Classic softcore first, hardcore as the end goal. Every death is reported as a failure even in
  softcore, and the safety rules (chicken, retreat, boss margins) are written for hardcore.
- Finish line: Hell Diablo dead and a target level per character. Classic's highest area level is 78
  (Hell Chaos Sanctuary and the cow level), and experience falls off beyond five levels above the
  monsters and again from level 70, so the realistic target is 75-80. Milestones on the way: Normal
  done around level 25, Nightmare around 40-45.
- The party is a closed economy. Items, gold and potions move freely between its members; nothing
  comes from other accounts, mules or rushers, and nothing goes to the mules.
- A session runs to the completion criteria. `--until <milestone>` sets a smaller goal for tests.
  A death does not stop the run: in softcore it is a corpse run and the party goes on.
- A stranger stops it at once: a whisper from an account outside `known-accounts.json` leaves the
  game straight away, the same as a stranger joining (`StrangerEscape.FleeOnWhisper`, as the rush
  bot does - a leveling game lasts too long to finish first). Nothing restarts it.

## Party

- The config lists existing characters: account, character, build plan, role. The bot never creates
  characters. The leader is configured explicitly; without one the bot does not start.
- Roles: melee members lead the engagement, ranged members stay 8-12 units behind the front and fall
  back towards the melee when attacked. Everyone stays within experience range of the leader (about
  two screens).
- A member in trouble leaves and rejoins the same game while the rest of the party waits in town. A
  corpse run is done as a party: clear around the body, then the owner picks it up. A member that is
  not back within the time limit sends everyone to a new game.

## Progression

- Quest driven with level gates: a table per difficulty lists the steps and the lowest level the
  party needs for each. Below a gate the party farms the spot the table names for that level range.
- Progress is per character and always read from the game (quests, waypoints, level, skills, stats,
  gear), so there is no save file and a restart continues from what the characters have. The planner
  picks the next step the members present still need, and goes back for a member that missed a quest,
  waypoint or skill-point reward.
- A game lasts as long as there is something useful in it. A new game only when re-farming an area
  is the efficient choice.

Area monster levels (classic `levels.txt`, Normal; Nightmare +25, Hell +50 - still to be checked
against the patch's `levels.bin`):

| Character level | Areas (monster level) |
|---|---|
| 1-6 | Den of Evil (1), Cold Plains caves (2-4), Burial Grounds (3) |
| 6-12 | Tristram (6), Forgotten Tower (7), Jail to Catacombs (10-12), Andariel |
| 12-18 | Sewers (13-14), Halls of the Dead (12-14), Arcane Sanctuary (14), Tal Rasha's Tombs (17) |
| 18-25 | Jungle (21-22), Kurast (22-24), Travincal (24), Durance of Hate (25) |
| 25-28+ | River of Flame and Chaos Sanctuary (26-28), cow level (28) |

## Build plans

A build plan is a data file per build; the bot knows a class only through its plan. The plans end at
the builds the realm's farmers already use: orb sorceress, nova sorceress, whirlwind barbarian (Find
Item after Whirlwind and Battle Orders), hammerdin, bowazon, lightning javazon, and a necromancer with
Amplify Damage, Corpse Explosion, Bone Armor and a golem.

- **Skills** are spent automatically in the plan's order. Prerequisites are filled in from
  `skills.json`. A skill holds at most `character level - required level + 1` hard points, so a step
  that cannot take a point yet is passed over for the next one. A `save` step holds every point until
  its level, for when several final skills open together.
- **Stats** have a `base` that is always spent, a `cap`, and one `rest` stat that takes everything
  else. Between base and cap, points go only to the requirement of an item the character wants to
  wear.
- **Combat** lists the attack and aura per level range.
- 1.09 has no respec, so a plan check runs before anything is spent: skills of the class, reachable
  levels, prerequisites, base within cap, totals that fit. At startup each character's spent points
  must match its plan so far, or the bot stops.

## Gear

- Items are scored per build: weights for skills, cast rate, life and resistances (resistances by how
  far the character is from the cap in the current difficulty), breakpoints as steps, and weapon
  damage per second computed for the physical builds from the skill's animation frames, the base
  weapon speed and increased attack speed. Life and mana leech weigh heavily for the physical builds:
  they keep a melee or bow character alive and casting without potions.
- An item counts for a character who meets its requirements now or will soon by their plan.
- Pickup is by score against the whole party. A stash box keeps upgrades for later levels until
  something better fills the slot.
- Gold is scarce while leveling, so the pickit is far looser than the farmers' nips: anything that
  sells, magic and plain items alike, is picked up by sell value per inventory cell, best first, and
  sold at every town visit. Upgrades always win the space, and the bar for junk rises as gold stops
  being short (enough for the spending order below, or the character's gold cap is near).
- Items and gold change hands in town: the giver drops, the receiver picks up, the giver checks the
  ground.
- Spending order: potions and scrolls, repairs, a party reserve, vendor upgrades, gambling with a clear
  surplus. Mercenaries only in expansion: the act 2 Nightmare Holy Freeze mercenary.

## Measuring

- Headline: wall clock per milestone.
- Diagnosis: party experience per minute per area and level range, and time per phase (clearing,
  town, waiting, walking).
- Deaths with their cause, detected by lifetime experience falling; chickens separately.

## Code and order

A `Leveling` bot type on `MultiClientBotBase`; the game-free logic (plans, allocator, plan check, item
score, weapon damage, party planner) in `D2NG.Leveling` with its own tests. Quest steps move out of the
rush bot into a shared place when the leveling bot needs them.

1. Plan format, allocator, plan check and the plans, reviewed before anything is spent.
2. Item score with weapon damage, tested on real items.
3. The bot: party roles, clearing on foot, town, pickup, sharing, chicken and rejoin, corpse runs.
   First live run: a fresh party through Den of Evil.
4. The act 1 progression table and quest steps: level 1 to Andariel.
