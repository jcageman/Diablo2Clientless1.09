# Debugging a bot run

The packet log is **ground truth**. What the code intended, what the log line says, and what went on
the wire are three different things, and only the third decides what the server did. Read the wire
before forming a theory.

## Read the wire first

Set `logLevel: "Debug"`. Logs run to hundreds of thousands of lines — grep, never read whole.

Start with what the bot **sent**, not what it logged:

```bash
grep -n "send D2GS packet of type" run.log | grep -vE "Ping|Run |UpdatePlayerLocation" | tail -40
```

Then count them over the failing window — the shape of the counts identifies the bug on sight:

```bash
python -c "
import io,collections,sys
c=collections.Counter()
for i,l in enumerate(io.open(sys.argv[1],encoding='utf-8',errors='replace'),1):
    if int(sys.argv[2])<=i<=int(sys.argv[3]) and 'send D2GS packet of type: ' in l:
        c[l.split('send D2GS packet of type: ')[1].split(' with')[0]]+=1
for k,v in c.most_common(): print(v,k)
" run.log 5219 5556
```

- **Only pings** — the bot never even tried. A movement or pathing call bailed and returned false
  without sending. Suspect an unpathable streamed object.
- **Movement plus many interacts, no result** — it arrived nowhere near the target and clicked from
  out of range. Suspect a stale `Me.Location`; find the last `UpdatePlayerLocation` it sent and
  compare that against the target's coordinates by hand.
- **A flood of `RemoveObject`** — objects dropping out of range as the character walks. This looks
  like an area unload but is not; confirm with coordinates, since each act sits in its own range.

## Interleaved clients

One log carries every client. Lines are tagged `Instance N`, and two clients' quest or position
packets interleave into what looks like one value flipping back and forth. Filter by instance before
concluding anything about a value that appears to oscillate.

Map instance to character from the join lines:

```bash
grep -E "Joining game .* with .* as " run.log | head
```

## Decoding quest state

`QuestInfo` (`0x28`) is a 7-byte header — id, update type, `UnitGid` (4), timer — then 96 bytes of
quest words. Quest `i` sits at raw offset `7 + 2*i`, little endian. `TheSevenTombs` is quest 14, so
offset 35.

```bash
python -c "
import io,re,sys
qi=re.compile(r'type: QuestInfo with data (.+)')
prev=None
for i,l in enumerate(io.open(sys.argv[1],encoding='utf-8',errors='replace'),1):
    m=qi.search(l)
    if not m: continue
    b=[int(x,16) for x in m.group(1).replace('0x','').split(',')]
    if len(b)<40: continue
    w=b[35]|(b[36]<<8)
    if w!=prev: print(i,'0x%04X'%w); prev=w
" run.log
```

Collecting the **distinct values** is often faster than tracking transitions, because it survives the
interleaving: swap the print for a `Counter`. A value that appears once, or one carrying bit 0 when
the others do not, is the moment worth reading around.

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
