# Debugging a bot run

The packet trace is **ground truth**. What the code intended, what the log line says, and what went
on the wire are three different things, and only the third decides what the server did. Read the wire
before forming a theory. Every game is recorded; the `d2-trace` skill finds the game and the window.

## Read the wire first

Start with what the bot **sent**, not what it logged, over the failing window of one client:

```bash
Trace packets <game> --client <client> --from <t> --to <t> --sent --limit 0
```

Then count them - the shape of the counts identifies the bug on sight:

```bash
Trace packets <game> --client <client> --from <t> --to <t> --sent --limit 0 | awk '{print $3}' | sort | uniq -c | sort -rn
```

- **Only pings** — the bot never even tried. A movement or pathing call bailed and returned false
  without sending. Suspect an unpathable streamed object.
- **Movement plus many interacts, no result** — it arrived nowhere near the target and clicked from
  out of range. Suspect a stale `Me.Location`; `Trace timeline` shows each walk verify and server
  reassign against what the client thought.
- **A flood of `RemoveObject`** — objects dropping out of range as the character walks. This looks
  like an area unload but is not; confirm with coordinates, since each act sits in its own range.

## Interleaved clients

The text log carries every client, tagged `Instance N`, and two clients' quest or position lines
interleave into what looks like one value flipping back and forth. The trace keeps one file per
client: pass `--client` and the question does not arise.

## Decoding quest state

`QuestInfo` (`0x28`) is a 7-byte header — id, update type, `UnitGid` (4), timer — then 96 bytes of
quest words. Quest `i` sits at raw offset `7 + 2*i`, little endian. `TheSevenTombs` is quest 14, so
offset 35. `Trace` decodes them: `packets ... --type 0x28` prints each `QuestInfo` as `i=word` pairs,
and `state ... --select quests` names every quest's word at one instant.

A value that appears once, or one carrying bit 0 when the others do not, is the moment worth reading
around.

A quest word that does not move after a step is weak evidence. Several chains only settle at the very
end — a message can look inert and still be a required link. Judge a chain by whether it completes,
not by whether each step visibly moves the word.

## Captures

Captures are the source for **observed bytes**. Before concluding something was never measured,
enumerate every capture and search all of them — including ones whose names look unrelated. The act 2
answer sat unread in a differently-named capture while a capture harness was built to re-measure it.

```bash
ls -la <scratchpad>/cap-*/ ; ls -la <scratchpad>/*.txt
python -c "
import io,re,sys
r=re.compile(r'Outgoing packet (PlayNPCMessage|QuestMessage|EntityAction) with data: (.+)')
for i,l in enumerate(io.open(sys.argv[1],encoding='utf-8',errors='replace'),1):
    m=r.search(l)
    if m: print(i,m.group(1),m.group(2).strip())
" <capture>
```

Two traps:

- **A capture file caps at exactly 1024.0 MB** and then stops silently. A big file can end before the
  interesting moment; check its size and last timestamp before trusting its absence of evidence.
- **The sniffer's exclude list drops packet types** — `RequestEntityUpdate`, `Walk`, `Run`,
  `UpdatePlayerLocation`, `Ping` outgoing among them. A type missing from a capture may have been
  filtered rather than never sent. The options line at the top of each capture lists the exclusions.

Existing constants in the code are themselves captured evidence: a message id already in a bot was
derived from a capture in an earlier session, so prefer it over anything you would derive yourself.

## Running the sniffer

```bash
PacketSniffer.exe device=4 protocols=d2gs direction=both raw=true "exclude=..."
```

It writes through Serilog, so the 1 GiB cap applies to the new capture too. Confirm it is actually
capturing — check the process exists and the file is growing — before telling anyone a measurement is
armed.

## Cheap experiments

- **Isolate the variable across characters.** A character that already carries a quest credit tests a
  later leg without redoing the earlier one. A fresh character tests the full same-game path. Both
  are needed: passing on one proves less than it appears.
- **Probe two candidate encodings in one run** by sending the first, logging the state, then the
  second, and logging again. One run then eliminates one of them — as long as the state you read
  actually moves at that point in the chain.
