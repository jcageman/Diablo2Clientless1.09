# Party bots

Lessons from multi-client bots - a taxi that opens portals and followers that fight - each paid for in
failed games. They hold for any bot that runs several clients in one game.

## Shared state between clients

Every client runs on its own thread against one shared state object, so a value one client writes can
land after another client has already moved past it.

- **Share facts, not flags.** A follower that saw the seal boss die set a shared "boss down" flag
  60ms after the taxi had reset it for the next seal, and the next seal ended the instant its boss
  spawned. Store *what* happened (the boss itself) and judge it when it is **read**, against what the
  reader knows now (`IsEarlierBoss`). A late write then counts for nothing.
- **Share what only one client can see.** A client only receives units near it, so a corpse 50 units
  from the taxi never reaches her. Whichever client sees the death decides for everyone.
- **A commitment has to move the body.** Pointing a follower's targeting at a far boss changes what it
  shoots, not where it stands: ranged followers fired at an escort 50 units out while the melee one
  fought alone. Committing to a target includes walking to a reach that suits the class.

## Portals

- **One portal per character.** Casting a town portal replaces the caster's previous one, and the
  owner walking back through it from town **closes** it. A taxi that restocks or heals through the
  party's portal leaves every follower who later goes to town stranded ("never saw a portal").
  Restock before casting the party portal, and cast a fresh one after any town trip of her own.
- **Arrival is a position, and the server sends it unasked**: 0.03-0.07s after the click, measured
  over 600 trips. Poll `IsNavigatablePointInArea` on `Me.Location` every 20ms; keep `RequestUpdate`
  (which blocks 400ms) as the fallback for a click that did not take.
- **Followers look a portal up by owner, newest first**, so a fresh portal from the taxi is all a
  stranded follower needs - no id change, which would also pull back followers already through.

## Leaving the game

Once a client has left (a chicken, a disconnect), every game call fails at once. Retry loops built on
`TryWithTimeout` then spin for their whole budget against a game that is gone, and the run task holds
the party in it - 18 to 20 seconds per taxi chicken. Every retry loop that a leave can interrupt checks
`IsInGame()` and stops.

## Survival

- **Healing potions heal over a few seconds, and drinking faster does not heal faster.** Drinking
  every 700ms, a character drank 9 to 11 potions per 10 seconds at 62-70% life and her life never
  rose; at 1500ms the same fights took a third of the potions. Drink at most every 1500ms.
- **Rejuvenations heal at once**, so they never wait for the healing interval -
  a taxi locked out by it fell from 769 to 471 life and chickened with fifteen in her inventory. A
  short gap (0.5s) lets the new life value arrive before a second is spent.
- **Read thresholds as a ladder with one hit between rungs**: rejuvenation line, then chicken floor,
  at least one typical hit apart (measure it from the "burst" lines). A 50% rejuvenation line over a
  45% floor on a 1041-life amazon left 52 life between them and she chickened in one hit.
- **Battle Orders raises the maximum, never the current life.** Everyone stands at 53-60% of the new
  maximum after the shouts, so any rule keyed to a fraction of maximum fires right then.
- **Iron Maiden returns what the cursed character deals** - 1511 life in 96ms off a barbarian's own
  whirl. Under it, hold off attacking; standing near the fight is safe and keeps the experience.
- **Experience reaches only party members in range of the kill.** A character in town earns nothing,
  so a town trip costs its whole duration in experience, not just the walk.

## Drops

A drop is reserved for the nearest client for its first seconds (`ItemClaimGrace`). A sweeper that
stops when only reserved items remain leaves them for clients that are already walking away; it waits
for the reservation to resolve instead.

## Measuring

Fix only what a log line proves. When the log cannot tell two causes apart, add a timing line that
can, run a batch, then change the code - the portal fix came from "position known after 0.06s,
noticed after 0.67s", not from the first plausible theory, and two plausible-looking fixes in this
file were wrong before a log line said why.

- Judge a change over 30 games against a 30-game baseline on the same server; ten games read about
  twice too good, and one failed game moves a small batch by several percent.
- Change one thing per batch, so the result has one cause.
- Bound options (`IOptions<T>`) are read once at startup: a config edit reaches the bot only on
  restart. Wait about 60 seconds after a stop before restarting, or the realm refuses the joins.
