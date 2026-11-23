---
name: MuleManager
description: Item reference pages for the mules' stock - dark tables, carved gold heads, every roll set against its range.
colors:
  page: "#0a0a0a"
  panel: "#0f0d0c"
  raised: "#141010"
  card: "#0e0e0e"
  track: "#262020"
  oxblood: "#3a1a12"
  oxblood-deep: "#24110c"
  rule: "#2c2523"
  rule-strong: "#4a2a20"
  gold: "#c7b377"
  gold-bright: "#e2cf94"
  ink: "#d8d2c4"
  dim: "#8d857a"
  faint: "#5f5852"
  quality-inferior: "#8d857a"
  quality-magic: "#6969ff"
  quality-rare: "#ffff64"
  quality-unique: "#c7b377"
  quality-set: "#00d000"
  quality-crafted: "#ffa800"
  stat-blue: "#6969ff"
  gain: "#ffffff"
  loss: "#ef4b37"
  warn: "#e0a030"
  warn-edge: "#5a4210"
  warn-wash: "#1a1208"
typography:
  display:
    fontFamily: "Cinzel, Trajan Pro, serif"
    fontSize: "28px"
    fontWeight: 700
    lineHeight: 1.15
    letterSpacing: "0.04em"
  headline:
    fontFamily: "Cinzel, Trajan Pro, serif"
    fontSize: "14px"
    fontWeight: 700
    letterSpacing: "0.1em"
  title:
    fontFamily: "Cinzel, Trajan Pro, serif"
    fontSize: "15px"
    fontWeight: 700
    letterSpacing: "0.03em"
  body:
    fontFamily: "Verdana, Tahoma, DejaVu Sans, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.5
    fontFeature: "tnum"
  body-small:
    fontFamily: "Verdana, Tahoma, DejaVu Sans, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    fontFeature: "tnum"
  label:
    fontFamily: "Cinzel, Trajan Pro, serif"
    fontSize: "11px"
    fontWeight: 700
    letterSpacing: "0.08em"
rounded:
  none: "0px"
spacing:
  "1": "4px"
  "2": "8px"
  "3": "12px"
  "4": "16px"
  "5": "24px"
  "6": "32px"
  "7": "48px"
components:
  button:
    backgroundColor: "transparent"
    textColor: "{colors.gold}"
    typography: "{typography.label}"
    rounded: "{rounded.none}"
    padding: "7px 14px"
  button-hover:
    backgroundColor: "{colors.gold}"
    textColor: "{colors.page}"
  button-primary:
    backgroundColor: "{colors.gold}"
    textColor: "{colors.page}"
    typography: "{typography.label}"
    rounded: "{rounded.none}"
    padding: "7px 14px"
  button-primary-hover:
    backgroundColor: "{colors.gold-bright}"
    textColor: "{colors.page}"
  button-quiet:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.none}"
    padding: "7px 14px"
  input:
    backgroundColor: "{colors.page}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.none}"
    padding: "7px 9px"
  top-bar:
    backgroundColor: "{colors.oxblood}"
    textColor: "{colors.gold}"
    height: "60px"
    padding: "0 24px"
  nav-tab-active:
    backgroundColor: "{colors.page}"
    textColor: "{colors.gold}"
    padding: "8px 16px"
  realm-switch-on:
    backgroundColor: "{colors.gold}"
    textColor: "{colors.page}"
    padding: "6px 14px"
  side-column:
    backgroundColor: "{colors.panel}"
    width: "280px"
    padding: "24px"
  item-card:
    backgroundColor: "{colors.card}"
    textColor: "{colors.stat-blue}"
    rounded: "{rounded.none}"
    padding: "12px 16px 16px"
  item-card-worn:
    backgroundColor: "transparent"
  table-head:
    backgroundColor: "{colors.page}"
    textColor: "{colors.gold}"
    typography: "{typography.label}"
    padding: "8px"
  table-row-selected:
    backgroundColor: "{colors.oxblood-deep}"
  item-card-empty:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.none}"
    padding: "16px"
  range-track:
    backgroundColor: "{colors.track}"
    height: "3px"
  range-tick:
    backgroundColor: "{colors.gold}"
    width: "2px"
    height: "11px"
  range-tick-over:
    backgroundColor: "{colors.gold-bright}"
    width: "2px"
    height: "11px"
  mini-range:
    backgroundColor: "{colors.track}"
    width: "22px"
    height: "3px"
  table-copies:
    textColor: "{colors.dim}"
    typography: "{typography.body-small}"
  chip:
    backgroundColor: "{colors.raised}"
    textColor: "{colors.ink}"
    typography: "{typography.body-small}"
    rounded: "{rounded.none}"
    padding: "2px 4px 2px 8px"
  segmented-on:
    backgroundColor: "{colors.oxblood}"
    textColor: "{colors.gold}"
    padding: "6px 12px"
  tab-active:
    backgroundColor: "{colors.page}"
    textColor: "{colors.gold}"
    padding: "8px 16px"
  level-input:
    backgroundColor: "{colors.page}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.none}"
    width: "64px"
    padding: "7px 9px"
  resist-cell:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.none}"
    padding: "8px 12px"
  slot-selected:
    backgroundColor: "{colors.oxblood}"
    padding: "6px 8px"
---

# Design System: MuleManager

## Overview

**Creative North Star: "The Item Reference of 2000"**

MuleManager reads like the fan-made item reference pages players learned Diablo II from around 2000: a near-black page, an oxblood bar with a gold rule across the top, carved gold capitals for heads, and dense Verdana tables beneath them. Every item name keeps the game's quality colour and every stat line is the game's magic blue. The one thing those pages never had is the system's signature: each roll drawn as a gold tick on the range one affix can roll, best always to the right, so the reader sees at a glance whether a roll is poor, good or perfect.

It is a data tool for a desktop browser, dense by intent. Hierarchy comes from typeface (Cinzel heads against Verdana data), quality colour and hairline rules, never from cards, shadows or fills. The system refuses the admin dashboard in a dark skin: no grey cards, no chip rows as decoration, no accent-blue chrome, no rounded corners. It also refuses imitation of the game itself: no game art, no Exocet face, no copied game frames.

Change is the subject of most screens, so the colours that say "better" and "worse" are reserved: a worse change is red, a better one is bright white, and the quality colours never double as change colours. An upgrade is always shown against what it replaces: the worn item beside the candidates, the stats a swap gives up, the frame count it was.

**Key Characteristics:**
- Near-black page, oxblood top bar with a 2px gold rule, square corners everywhere.
- Cinzel (self-hosted, 500-700) for heads, tabs, labels and buttons; Verdana 13px with tabular figures for all data.
- Item names in quality colour, stat lines in magic blue, numbers right-aligned.
- Each roll shown as a tick on its affix range, best at the right.
- Every change set against the worn item: deltas, "Gives up", "was N".
- Depth by tone and hairline rules only; no shadows.
- One game mode per screen: classic and expansion never mix.

## Colors

A near-black page lit only by gold, oxblood and the game's own item colours; everything else is warm grey.

### Primary
- **Carved Gold** (gold): heads, table head text and its underline, the top bar's rule, active tab and realm-switch borders, button outlines, focus outline, the range tick, the card score. Also the unique quality colour, as in the game.
- **Bright Gold** (gold-bright): hover on gold text, primary buttons and sortable heads; the tick of a roll above one affix's range (two affixes stacked).

### Secondary
- **Oxblood** (oxblood) to **Deep Oxblood** (oxblood-deep): the top bar's vertical gradient, the selected slot, the pressed segment, the selected table row and highlighted rows, the frame of item cards, h2 underlines and tab-strip rules.

### Tertiary: the game's item colours
- **Magic Blue** (quality-magic, stat-blue): magic item names and every stat line, on cards and in the stock table.
- **Rare Yellow** (quality-rare), **Unique Gold** (quality-unique), **Set Green** (quality-set), **Crafted Orange** (quality-crafted), **Inferior Grey** (quality-inferior); normal and superior items use ink.

### Change and state
- **Gain White** (gain): a better change, bold.
- **Loss Red** (loss): a worse change, bold; also resists under the cap, blocking reasons, full mules, failed refreshes, the "Gives up" key and the line-through on given-up stats.
- **Warning Amber** (warn) on **warn-edge** / **warn-wash**: a stale gear snapshot, soft warnings, warning notices, stale mule reads, accounts missing from the configuration.

### Neutral
- **Page** (page): the body, table heads, inputs, the active tab. **Panel** (panel): the side column, the weights panel and expanded table rows. **Raised** (raised): row and slot hover, chip and notice fills. **Card** (card): item-card fill. **Track** (track): the empty bar under range ticks and meters.
- **Rule** (rule): hairline row and section dividers, resist cells, the empty card. **Strong Rule** (rule-strong): input, chip, tag, segmented and quiet-button borders, the dashed empty state.
- **Ink** (ink): body text, normal-quality names, the score column. **Dim** (dim): secondary text, labels in data, inactive tabs, copy counts. **Faint** (faint): placeholders, unchanged deltas ("·"), blocked rows.

### Named Rules
**The Colour Tells Rule.** Bold white is better and bold red is worse on every change, whatever its sign. The sign gives the change's direction: frame counts show the raw change, so one frame fewer reads "−1" in white, and a frame box shows "was 11" beside the new count. Reduced requirements on cards are the one inverted stat: a larger reduction reads "+" in white. Minus is always U+2212, on deltas and on negative values such as a resist below zero. Score is a ranking, not a change: signed, never coloured as gain or loss.

**The Green Means Set Rule.** Green only ever means a set item: set names and set-bonus lines. It never marks a gain, a success or a healthy state.

**The Item Colours Are the Game's Rule.** Quality colours keep exactly the game's meanings and are applied to item names (and blue to stat lines); no other UI element borrows them except gold, which is both the unique colour and the chrome accent.

## Typography

**Display Font:** Cinzel (self-hosted woff2, weights 500-700, OFL; falls back to Trajan Pro, serif)
**Body Font:** Verdana (with Tahoma, DejaVu Sans, sans-serif)

**Character:** Carved Roman capitals over the plain screen sans of the era. Cinzel carries every head, label, tab and button; Verdana carries every piece of data, always with tabular figures.

### Hierarchy
- **Display** (700, 28px, 1.15, 0.04em): page and slot titles (h1), in gold; 20px inside empty states; 24px for the bar mark.
- **Headline** (700, 14px, 0.1em): section heads (h2), gold over an oxblood hairline.
- **Title** (700, 15px, 0.03em): item names on cards, in quality colour; 16px for account heads on Mules.
- **Body** (400, 13px, 1.5, tabular figures): all data, table cells, notices, the empty card. Ledes cap at 75ch.
- **Body small** (12px): stat lines, slot list, sub-lines, copy counts, chips, summary, status; 11px for base lines, requirement lines, the "Gives up" block and tags.
- **Label** (700, 11px, 0.08-0.1em): table heads, field labels, weight-row heads; buttons use 12px, in-page tabs 13px, nav tabs 14px at 500.

### Named Rules
**The Two Faces Rule.** Cinzel for anything that names or commands, Verdana for anything that is data. Numbers are never set in Cinzel, except the 18px figures in resist cells and frame boxes; their moves ("+15", "was 11") drop back to Verdana 11px. A chip that names a sort key inside a table head is data and stays Verdana.

## Layout

Desktop first, 1280px and up. A sticky 60px top bar holds the mark, the Stock / Upgrades / Mules tabs, the Classic / Expansion switch and the stock's freshness. Below it a two-column work area: a 280px sticky side column (panel fill, hairline right rule, its own scroll) and a main column padded 24px / 32px / 48px.

The Upgrades side column holds the character picker, a "Hide up to level" field (label and a 64px right-aligned number input on one row, with a dim count of hidden characters below), the gear-snapshot freshness with Refresh, the slot list, and the "In Hell now" summary. The slot list has ten equipment slots; expansion characters get an eleventh, Charms, which reads "N carried". A slot the right hand's two-handed weapon holds shows "two-handed" and is disabled at 50% opacity.

The Upgrades main column runs: slot title and lede ("Replacing [worn item]", or for charms the count carried and how a candidate swaps), controls (build, PvP / PvM, difficulty), the collapsible weights panel, the card row (worn card first, then up to three candidates, then a selected row from further down), the swap line with Show on mule, the resist strip, and the full candidate table.

The Stock main column runs: title and lede, an Items / gems tab strip, a sort bar, the item table and a pager.

Below 1280px the side column stacks above the main column, the bar wraps and stops being sticky, and the bar status hides. Table heads stick under the bar on desktop.

Spacing is a 4px-based scale (4, 8, 12, 16, 24, 32, 48). Rows are tight: 7px cell padding, 6px slot rows. Item cards sit in an auto-fill grid with a 215px minimum and 12px gaps. The resist strip is one row: four equal resist cells, then a narrower column (0.7 of a resist cell) for each frame box, so an Attack frames box adds a column rather than a row. Every spacing and layout value lives in the stylesheet; only per-item values (a tick's position, a meter's width) are set inline.

### Named Rules
**The One Realm Rule.** Classic and expansion never mix on one screen. The game mode is a hard switch in the top bar and every list, card and summary below it belongs to that mode. Expansion-only parts (the Charms slot, "Gems and runes") appear only in expansion.

## Elevation & Depth

Flat. There are no shadows. Depth comes from tone (page, panel, raised, oxblood) and from 1px rules: hairline rules between rows, gold under table heads and the bar, oxblood under section heads. Selection is a fill change (oxblood) plus, for slots and cards, a gold border or inset outline. The only ring in the system is the 1px gold halo on an over-range tick.

### Named Rules
**The Hairline Rule.** Structure is drawn with 1px rules, never with boxes or shadows. A second weight (2px) exists only for the gold rule under the top bar.

## Shapes

Square corners everywhere (0px), including MudBlazor's own components through its theme. Borders are 1px solid; the worn item card and the empty state are the only dashed borders. Range bars are 3px tracks with a 2px x 11px tick on cards, and 22px x 3px tracks with a 2px x 7px tick inline in the stock table; meters are 4-6px flat bars. The select arrow is drawn in gold with two gradients, not an icon. Icons are hand-drawn line SVGs with square caps (refresh, close, locate, plus), 14px in buttons and 10-12px in chips and icon buttons.

## Components

### Buttons
Outlined in gold, filled on hover: confident but quiet.
- **Shape:** square (0px), 1px gold border, 7px 14px.
- **Default:** gold Cinzel label on transparent; hover fills gold with page-coloured text.
- **Primary:** gold fill, page text; hover bright gold. The one main action per view ("Show on mule", "Refresh every mule", the empty-state call).
- **Quiet:** strong-rule border, ink text, for secondary actions (Back to quality, Add a stat, paging, more types).
- **Icon button:** bare dim close icon that turns red on hover, for removing a weight row.
- **Disabled:** 45% opacity. **Focus:** 1px gold outline, 2px offset (global).

### Top bar and navigation
- Oxblood gradient bar, 2px gold rule beneath, sticky at 60px.
- Tabs in Cinzel 14px, dim; hover bright gold; active tab gold text, gold 1px border, page fill, as if cut out of the bar.
- **Realm switch:** a gold-outlined two-button group; the pressed mode fills gold.
- **In-page tabs** (Stock's Items / gems): Cinzel 13px on an oxblood rule; the pressed tab takes an oxblood border and page fill, the count in its label after "·".

### Inputs / Fields
- **Style:** page fill, 1px strong-rule border, square, 7px 9px; label above in gold Cinzel 11px.
- **Inline field:** label and a narrow input on one row (the 64px "Hide up to level" number, right-aligned).
- **Hover:** border goes oxblood. **Focus:** global gold outline. Checkboxes use gold accent, counts dim at the row's end.
- **Segmented control:** strong-rule group, pressed segment oxblood with gold text (PvP / PvM, difficulty).
- **Weights panel:** a collapsible panel-filled box whose summary line states the build and its weights; open, a five-column grid of stat, per point, cap, slot and remove.

### Tables
- Gold Cinzel 11px heads on page fill with a gold underline, sticky under the bar; sortable heads underline in bright gold on hover and when pressed.
- Rows separated by hairline rules; hover raised, selected deep oxblood. Numbers right-aligned; an unchanged cell is a faint "·".
- Item names bold in quality colour; a dim "×N" after the name when several identical copies rank together; tags (Ethereal, sockets) as on cards; sub-lines dim 12px (base name, item level, "no level requirement" for a pre-requirement unique or set), worded the same on Stock and Upgrades. Blocking reasons red, soft reasons amber.
- A blocked row fades to faint and strikes its item name through.
- **Change columns** (Upgrades): one per weighed stat that any candidate moves, then Resists; every cell coloured by the Colour Tells Rule, frame columns showing the raw change.
- **Properties cell** (Stock): stat lines in magic blue, each followed by its mini range; four lines, then a dim ellipsis. Clicking the row opens the full item card in a panel-filled row beneath.

### Sort bar and sort chips (Stock)
A dim "Sort by" and one select, grouped by what players look for (Resists, Life and mana, Attributes, Skills, Speed, Damage, Defense, Find, Other). With no key it reads "quality · add a stat…" and the table's last column is Quality; each pick adds a key and the select then reads "then by…". Each key becomes a numbered chip ("1. Fire res") heading its own right-aligned column, removable by its close icon; a quiet "Back to quality" button clears them all.

### Filter chips
Removable "Must have" stat filters on Stock: square, strong-rule border, raised fill, 12px text with a close icon that turns red on hover. They are functional filters and sort keys, never decoration.

### Item card (signature)
The game's tooltip, rebuilt as reference data.
- Centred name in Cinzel 15px quality colour; a dim 11px base line: a note when there is one, the base type, the holder and container, then tags (Ethereal, sockets) in small strong-rule boxes.
- The note says where the card stands: "Worn" on the worn card, "Row N" for a selected candidate below the top three, and on charms what the candidate takes the place of ("swaps the carried [charm] ([its first line])" or "fills a free spot").
- Stat lines left-aligned in magic blue, each with its range bar beneath and, when compared, its delta in a 38px right column.
- 1px oxblood frame on the card fill; hover strong rule; selected gold border; blocked 55% opacity.
- **Worn card:** dashed border, transparent fill, not clickable. The "Worn ·" note in the base line is its only marker; there is no label above the card.
- **Gives up:** a compact block under the lines: a red "Gives up" key, then the replaced item's lost stats in dim 11px, each struck through in red.
- **Set bonus:** a block under the lines, after a hairline: a dim line naming the set and pieces worn (with "after the swap" on candidates), then the active bonus lines in set green, or a dim line when no bonus is active.
- Requirement line dim 11px: "Required level: N", or "No level requirement" for a pre-requirement unique or set; a reason below it, amber when soft, red when blocking. Score in a footer over a hairline, gold.

### Empty card
When nothing on the mules beats the worn item, the card row's candidate place holds a plain box: 1px rule border, no fill, centred ink text saying so, and a dim line pointing to the table, which still lists the closest items.

### Range tick (signature)
Every stat with a known affix range shows a 3px track with a 2px gold tick at the roll's position, which slides in from the left (520ms ease-out). A roll above one affix's maximum gets a bright gold tick with a gold halo. Stats with no known range show no track. The tooltip states the roll and its range.

**The Best-Right Rule.** The right end of a range is always the best roll. A range wholly at or below zero (reduced requirements) puts its most negative value at the right.

### Resist strip and frame boxes
Under the swap line, one cell per resist and one per frame count, each a 1px-ruled box with a dim 11px label.
- **Resist cell:** the effective resist over its cap ("58 / 75") in Cinzel 18px with its signed move (or "+N over" when only the excess moves), and a 4px meter to the cap. Under the cap, figure and meter turn red. A resist the build does not weigh says "· not weighed" in its label.
- **Frame box:** Cast frames, Hit recovery and, for builds that attack, Attack frames: the count after the swap, and "was N" beside it when it changed, white when it fell and red when it rose. No meter.

### Side column summary
A dim/ink definition list (12px) of the character's current life, mana, resists, cast, hit-recovery and (when known) attack frames, and block in the chosen difficulty; resists under cap in red, excess over the cap in parentheses.

### Notices, freshness and empty states
- Notices: raised fill, strong-rule border; warning notices amber on warn-wash; error notices red-tinted. A second paragraph is dim.
- Freshness line: hairline box with a small Refresh button; stale turns amber.
- Empty state: dashed strong-rule box, a 20px Cinzel head, a dim line of guidance and, where one action fixes it, a single button.
- Refresh progress: a pulsing gold square marks the account being read; failures red.

## Do's and Don'ts

### Do:
- **Do** set every item name in its quality colour and every stat line in magic blue (#6969ff).
- **Do** show a roll against its affix range wherever the range is known, best at the right.
- **Do** colour every change bold white when better and bold red when worse; show frame changes raw ("−1", "was 11") and invert only reduced requirements.
- **Do** show an upgrade as a change against the worn item: the worn card dashed with "Worn ·", the swap's deltas, what it gives up as a struck-through block, and frames as "was N".
- **Do** say in the base line what a charm candidate replaces ("swaps the carried …", "fills a free spot").
- **Do** label a unique or set from before level requirements "no level requirement" rather than "Required level: 0".
- **Do** put set bonuses in a block under the item's lines, in set green.
- **Do** keep classic and expansion on separate screens behind the top-bar switch.
- **Do** design for 1280px and up, and let the side column stack below that.
- **Do** use Cinzel for heads, labels and buttons and Verdana 13px with tabular figures for data.
- **Do** refer to characters and accounts by role in anything committed: docs, screenshots, fixtures.

### Don't:
- **Don't** use green for a gain, a success or anything but a set item.
- **Don't** colour the score as a gain or loss; it ranks, it does not compare.
- **Don't** add eyebrows or kicker labels above titles or cards.
- **Don't** add shadows, rounded corners, grey cards or accent-blue chrome.
- **Don't** use game art, the Exocet face, or copied game frames and panels.
- **Don't** mix classic and expansion items, characters or counts on one screen.
- **Don't** put account or character names in committed docs or screenshots.
