---
version: 1
slug: "src-d2ng-mulemanager-ui"
primary_target: "src/D2NG.MuleManager.UI"
related_targets: ["src/D2NG.MuleManager"]
---

# MuleManager UI

Mode: Operate. Desktop browser beside the running bots, 1280 px and up; below that the side column collapses.

## Job

The maintainer and anyone who clones the repo answer three questions without asking Claude:
- **Stock**: is there an item like this on any mule, and where is it?
- **Upgrades**: what is the best item on the mules for this character's slot, and what does the swap gain and cost?
- **Mules**: how full is each mule, and how current is what we know?

Content: about 30,800 items on 353 mule characters in 33 accounts, 12 of them expansion. Gems, potions and runes are about two-thirds of the items and show as counts per type. Items carry up to 16 stats. Bot characters' gear comes from snapshots, which are stored by the bots and refreshed by MuleManager. There is no armory.

## Constraints

- Classic and expansion never mix on one screen: a hard switch in the top bar.
- Upgrades: a PvP/PvM resist switch. PvP guards fire, cold and lightning, scores cold over the cap at 0.3, and ignores poison. PvM counts all four up to the cap and nothing over it. Strength and dexterity requirements are checked after reduced requirements and ethereal. One slot at a time; combos come later.
- Change colours: a loss is red with "−" and a gain is bright with "+". The quality colours keep their meaning, so green never means a gain.
- No game art, no Exocet, no copied game frames. Committed screenshots and fixtures carry no names.

## Direction contract

THESIS: MuleManager reads like the item reference pages players learned the game from: dark tables, carved gold heads, every name in its quality colour, and each roll set against its range. It refuses the admin dashboard in a dark skin: no grey cards, no chip rows, no accent-blue chrome.

OWN-WORLD: A near-black page (#0a0a0a) framed by oxblood bars (#3a1a12 to #24110c) with a gold rule (#c7b377). Carved-capital heads in Cinzel, gold. Data in Verdana at 13 px with tabular figures. Item names keep the game's quality colours: white, grey, blue #6969ff, yellow #ffff64, gold #c7b377, green, orange. Stat lines are blue. Hairline oxblood rules and square corners.

STORY: You pick a character and a slot and see the swap that wins, what it gains, what it costs, where the item lies, and whether the character can wear it. You can trust it because every roll shows where it sits in what the game can roll.

FIRST VIEWPORT: Upgrades. The oxblood top bar holds the mark, the Stock/Upgrades/Mules tabs, the Classic/Expansion switch and how fresh the stock is. A 280 px left column holds the character, how old its gear snapshot is with Refresh, and its ten slots with the worn item in quality colour. The main area has a Cinzel title for the slot, the item being replaced, the weights and the PvP/PvM switch, then the top candidates as centred tooltip cards with range bars and deltas, a resist strip for the selected swap, and the ranked table beneath. Main action: Show on mule.

FORM: Item reference tables of 2000, the pick, position 1 of 7; seed:d1ff964a23d93a22. Signature: each stat's roll drawn as a tick on its affix range.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Open

- Item graphics: none for now (public repo).
- Combo search: a later round.
