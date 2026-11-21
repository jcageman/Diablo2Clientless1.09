using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Query;

/// <summary>
/// Whether the items the client holds agree with what the server said about them. Where each own item is, by the server's
/// word alone, follows its item packets (0x9C/0x9D), sales (0x2A), uses (0x3F) and removals (0x0A); a second after the
/// server's last word on an item the client's own state - its containers and cell, belt, body slot and cursor - is held
/// against it. On top, every item the server puts in a grid is checked against the client's grid as it stood: a cell
/// another item still held, a place outside the grid, a container holding more cells than it has, an id the client still
/// holds under another item; and a second after any item change the cells each grid marks taken are held against the
/// cells its items cover.
/// </summary>
public sealed class ItemStateCheck : IncidentCheck, IFamily
{
    public const string Overlap = "item overlap";
    public const string OutsideGrid = "item outside grid";
    public const string Overfull = "container overfull";
    public const string HeldAfterRemoval = "item held after removal";
    public const string Misplaced = "item misplaced";
    public const string CursorNotGiven = "cursor not given";
    public const string IdReused = "item id reused";
    public const string CursorNotTaken = "cursor not taken";
    public const string GridCells = "grid cells wrong";

    public static readonly string[] Kinds = [Overlap, OutsideGrid, Overfull, HeldAfterRemoval, Misplaced, CursorNotGiven, IdReused, CursorNotTaken, GridCells];

    /// <summary>How long after the server's last word on an item the client must agree with it: the bot's own packets of one move arrive within it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LoadWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SameDeal = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SameInstant = TimeSpan.FromMilliseconds(100);

    /// <summary>How long after a transmute request the cube's items it takes still count as its, as the item ledger counts them.</summary>
    private static readonly TimeSpan TransmuteQuiet = TimeSpan.FromSeconds(1.5);

    private enum Place
    {
        Grid,
        Body,
        Cursor,
        Ground,
        Gone,
    }

    private sealed record Truth(Place Place, string Text, TimeSpan At, ContainerType Container = ContainerType.Unspecified, Point Cell = null, DirectoryType Slot = DirectoryType.NotApplicable);

    private readonly Dictionary<uint, Truth> _truth = [];
    private readonly Dictionary<uint, TimeSpan> _due = [];
    private readonly Dictionary<uint, TimeSpan> _used = [];
    private readonly HashSet<uint> _evicted = [];
    private readonly HashSet<(string, uint)> _reported = [];
    private readonly HashSet<ContainerType> _overfull = [];
    private readonly Dictionary<ContainerType, string> _cells = [];
    private TimeSpan? _loadUntil;
    private TimeSpan? _cellsDue;
    private (uint Id, DirectoryType Slot, TimeSpan At)? _unequipped;
    private TimeSpan? _transmuted;
    private readonly Dictionary<uint, TimeSpan> _lifted = [];
    /// <summary>The body slots the client asked to take an item off (0x1C) or swap one into (0x1D), and when.</summary>
    private readonly Dictionary<DirectoryType, TimeSpan> _takeOff = [];
    private uint? _cursor;
    private int _checked;
    private int _cellChecks;

    private static readonly ContainerType[] Grids = [ContainerType.Inventory, ContainerType.Cube, ContainerType.Stash];

    public string Family => "state";

    public override string CacheKey => "ItemStateCheck 4";

    /// <summary>How many item verdicts the check reached: each own item a second after the server's last word on it, held against where the client keeps it.</summary>
    public int Checked => _checked;

    /// <summary>How many times a grid's marked cells were held against the cells its items cover.</summary>
    public int CellChecks => _cellChecks;

    public override void Before(ReplayContext context, TraceRecord record)
    {
        var now = context.Now;
        VerifyDue(context, now);
        var p = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsSent && p.Length > 0)
        {
            if (p[0] == 0x4C || (p[0] == 0x4F && p.Length >= 2 && p[1] == ItemLedger.TransmuteButton))
            {
                _transmuted = now;
            }
            else if (p[0] is 0x19 or 0x24 && p.Length >= 5)
            {
                _lifted[PacketDecoder.U32(p, 1)] = now;
            }
            else if (TakeOffSlot(p) is { } slot)
            {
                _takeOff[slot] = now;
            }
        }

        if (record.Kind != TraceRecordKind.D2gsReceived || p.Length < 8 || p[0] is not (0x9C or 0x9D) || context.Game.Me == null)
        {
            return;
        }

        var action = (Action)p[1];
        if (action is not (Action.PutInContainer or Action.PutInBelt or Action.SwapInContainer) || !Own(context, p))
        {
            return;
        }

        var item = Parse(p);
        if (item == null)
        {
            return;
        }

        _loadUntil ??= now + LoadWindow;
        CheckPut(context, item, action, now);
    }

    public override void After(ReplayContext context, TraceRecord record)
    {
        var now = context.Now;
        var p = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsReceived && p.Length > 0 && context.Game.Me != null)
        {
            switch (p[0])
            {
                case 0x9C or 0x9D when p.Length >= 8 && Own(context, p):
                    Follow(context, p, now);
                    break;
                case 0x2A when p.Length >= 15 && p[1] == NpcTransactionPacket.SellTransaction && PacketDecoder.U32(p, 7) is var sold and not 0xFFFFFFFF:
                    Say(sold, new Truth(Place.Gone, "sold to a vendor (0x2A)", now));
                    break;
                case 0x3F when p.Length >= 6:
                    var used = PacketDecoder.U32(p, 2);
                    _used[used] = now;
                    if (context.Game.Items.TryGetValue(used, out var potion) && IsPotion(potion) && _truth.TryGetValue(used, out var was) && was.Place == Place.Grid && was.Container == ContainerType.Inventory)
                    {
                        Say(used, new Truth(Place.Gone, "drunk from the inventory (0x3F)", now));
                    }

                    break;
                case 0x0A when p.Length >= 6 && p[1] == (byte)EntityType.Item:
                    var removed = PacketDecoder.U32(p, 2);
                    if (_truth.TryGetValue(removed, out var before) && before.Place != Place.Ground)
                    {
                        Say(removed, new Truth(Place.Gone, "removed from the game (0x0A)", now));
                    }
                    else
                    {
                        _truth.Remove(removed);
                    }

                    break;
            }
        }

        if (context.Game.Me == null)
        {
            return;
        }

        var cursor = context.Game.CursorItem?.Id;
        if (cursor != _cursor)
        {
            if (cursor is { } held)
            {
                _due[held] = now + Grace;
            }

            _cursor = cursor;
        }
    }

    public override void Finished(ReplayContext context)
    {
        VerifyDue(context, TimeSpan.MaxValue);
        _truth.Clear();
        _due.Clear();
        _used.Clear();
        _evicted.Clear();
        _reported.Clear();
        _overfull.Clear();
        _cells.Clear();
        _loadUntil = null;
        _cellsDue = null;
        _unequipped = null;
        _transmuted = null;
        _lifted.Clear();
        _takeOff.Clear();
        _cursor = null;
    }

    public override void Save(System.IO.BinaryWriter w)
    {
        base.Save(w);
        w.Write(_checked);
        w.Write(_cellChecks);
    }

    public override void Load(System.IO.BinaryReader r)
    {
        base.Load(r);
        _checked = r.ReadInt32();
        _cellChecks = r.ReadInt32();
    }

    /// <summary>The slot a take-off (0x1C) or a swap onto the body (0x1D) empties.</summary>
    private static DirectoryType? TakeOffSlot(byte[] p) => p[0] switch
    {
        0x1C when p.Length >= 3 => (DirectoryType)PacketDecoder.U16(p, 1),
        0x1D when p.Length >= 9 => (DirectoryType)PacketDecoder.U32(p, 5),
        _ => null,
    };

    /// <summary>Whether the client asked for it recently enough that the server's packet answers it.</summary>
    private static bool Asked(TimeSpan? asked, TimeSpan now) => asked is { } at && now - at <= ItemLedger.AnswerTimeout;

    private static bool IsPotion(Item item) => item.IsPotion || item.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion;

    /// <summary>What a 0x3F uses up: a potion or a scroll. The cube and tomes get a 0x3F each time they are opened and stay.</summary>
    private static bool UsedUp(Item item) => IsPotion(item) || item.Classification is ClassificationType.Scroll or ClassificationType.StaminaPotion
        or ClassificationType.AntidotePotion or ClassificationType.ThawingPotion;

    /// <summary>An item packet about this client's own items: 0x9C (which names no owner) or 0x9D owned by the client's character.</summary>
    private static bool Own(ReplayContext context, byte[] p)
    {
        var action = (Action)p[1];
        if (action is Action.AddToGround or Action.OnGround or Action.AddToShop or Action.RemoveFromShop)
        {
            return false;
        }

        if (p[0] == 0x9C)
        {
            return true;
        }

        return p.Length >= 13 && (EntityType)p[8] == EntityType.Player && PacketDecoder.U32(p, 9) == context.SelfId;
    }

    private static Item Parse(byte[] p)
    {
        try
        {
            return new ParseItemPacket(new D2gsPacket(p)).Item;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static Container Grid(Game game, ContainerType container) => container switch
    {
        ContainerType.Inventory => game.Inventory,
        ContainerType.Cube => game.Cube,
        ContainerType.Stash or ContainerType.Stash2 => game.Stash,
        ContainerType.Belt => game.Belt,
        _ => null,
    };

    private static string GridName(ContainerType container) => container switch
    {
        ContainerType.Stash or ContainerType.Stash2 => "stash",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>The rows of the belt the client wears; one without a belt.</summary>
    private static int BeltRows(Game game)
        => game.Me.Equipment.TryGetValue(DirectoryType.Belt, out var belt) && belt.BeltRows > 0 ? (int)belt.BeltRows : 1;

    private void CheckPut(ReplayContext context, Item item, Action action, TimeSpan now)
    {
        var game = context.Game;
        var grid = Grid(game, item.Container);
        var cell = ItemView.Cell(item);
        if (grid == null || cell == null)
        {
            return;
        }

        var belt = item.Container == ContainerType.Belt;
        int width = belt ? 1 : Math.Max(1, (int)item.Width), height = belt ? 1 : Math.Max(1, (int)item.Height);
        int gridWidth = (int)grid.Width, gridHeight = belt ? BeltRows(game) : (int)grid.Height;
        var name = ItemView.Short(item);
        var where = ItemView.Where(item);
        var loading = now <= _loadUntil;

        if ((cell.X + width > gridWidth || cell.Y + height > gridHeight) && !(belt && loading))
        {
            Once(context, OutsideGrid, item.Id, now,
                $"the server put {name} ({width}x{height}) in the {GridName(item.Container)} at {ItemView.Grid(cell)}, outside the {gridWidth}x{gridHeight} grid the client keeps"
                + (belt ? $" (rows from the belt it wears{(game.Me.Equipment.TryGetValue(DirectoryType.Belt, out var worn) ? $", {ItemView.Short(worn)}" : ", none")})" : ""));
        }

        if (HeldElsewhere(game, item.Id) is { } holder && !string.Equals(holder.Item.Type, item.Type, StringComparison.Ordinal))
        {
            Once(context, IdReused, item.Id, now,
                $"the server put {name} in the {where} under id {Names.Id(item.Id)}, which the client still held as {ItemView.Short(holder.Item)} in {holder.Where}"
                + (_truth.TryGetValue(item.Id, out var last) ? $"; the server's last word on that one: {last.Text} at {TraceTime.Format(last.At)}" : "; the server never said it left"));
        }

        if (action == Action.SwapInContainer || belt)
        {
            return;
        }

        foreach (var other in grid.Items.Where(o => o.Id != item.Id && Overlaps(o, item)).OrderBy(o => o.Id))
        {
            _evicted.Add(other.Id);
            var said = _truth.TryGetValue(other.Id, out var truth) ? $"the server's last word on it: {truth.Text} at {TraceTime.Format(truth.At)}" : "no packet of this file placed it";
            Once(context, Overlap, other.Id, now,
                $"the server put {name} in the {where}, cells the client still held {ItemView.Short(other)} in at {ItemView.Grid(ItemView.Cell(other))} ({other.Width}x{other.Height}); {said}, and no packet took it out - "
                + "the client drops it from its grid without a word");
        }
    }

    private static bool Overlaps(Item a, Item b)
    {
        var ca = ItemView.Cell(a);
        var cb = ItemView.Cell(b);
        if (ca == null || cb == null)
        {
            return false;
        }

        return ca.X < cb.X + Math.Max(1, (int)b.Width) && cb.X < ca.X + Math.Max(1, (int)a.Width)
            && ca.Y < cb.Y + Math.Max(1, (int)b.Height) && cb.Y < ca.Y + Math.Max(1, (int)a.Height);
    }

    private sealed record Held(Item Item, string Where, Place Place, ContainerType Container, Point Cell, DirectoryType Slot);

    /// <summary>Where the client holds an item now: a grid, its body or its cursor; null when nowhere.</summary>
    private static Held HeldElsewhere(Game game, uint id)
    {
        if (game.CursorItem is { } cursor && cursor.Id == id)
        {
            return new Held(cursor, "the cursor", Place.Cursor, ContainerType.Unspecified, null, DirectoryType.NotApplicable);
        }

        foreach (var grid in new[] { game.Inventory, game.Cube, game.Stash, game.Belt })
        {
            if (grid.FindItemById(id) is { } held)
            {
                return new Held(held, ItemView.Where(held), Place.Grid, held.Container == ContainerType.Stash2 ? ContainerType.Stash : held.Container, ItemView.Cell(held), DirectoryType.NotApplicable);
            }
        }

        foreach (var (slot, worn) in game.Me.Equipment)
        {
            if (worn.Id == id)
            {
                return new Held(worn, slot.ToString(), Place.Body, ContainerType.Unspecified, null, slot);
            }
        }

        return null;
    }

    private void Say(uint id, Truth truth)
    {
        _truth[id] = truth;
        _due[id] = truth.At + Grace;
        _cellsDue = truth.At + Grace;
    }

    /// <summary>Where the server's packet leaves the item, and for a swap the item it displaced.</summary>
    private void Follow(ReplayContext context, byte[] p, TimeSpan now)
    {
        var action = (Action)p[1];
        var id = PacketDecoder.U32(p, 4);
        var item = Parse(p);
        if (item == null)
        {
            return;
        }

        switch (action)
        {
            case Action.PutInContainer or Action.PutInBelt or Action.SwapInContainer or Action.SwapInBelt:
                if (action == Action.SwapInBelt && item.Container != ContainerType.Belt && item.Location != null)
                {
                    item.Container = ContainerType.Belt;
                    item.Location = new Point((ushort)(item.Location.X % 4), (ushort)(item.Location.X / 4));
                }

                var container = item.Container == ContainerType.Stash2 ? ContainerType.Stash : item.Container;
                if (container is ContainerType.ForTrade or ContainerType.TradeOffer)
                {
                    Say(id, new Truth(Place.Gone, $"put on a trade page ({action})", now));
                    break;
                }

                Say(id, new Truth(Place.Grid, $"put in the {ItemView.Where(item)} ({action})", now, container, ItemView.Cell(item)));
                _evicted.Remove(id);
                Full(context, container, now);
                break;
            case Action.RemoveFromContainer or Action.RemoveFromBelt:
                var consumed = _truth.TryGetValue(id, out var was) && was.Place == Place.Gone
                    || (_used.TryGetValue(id, out var usedAt) && now - usedAt <= SameDeal && UsedUp(item));
                if (item.Container is ContainerType.ForTrade or ContainerType.TradeOffer)
                {
                    Say(id, new Truth(Place.Gone, $"taken off a trade page as the trade ended ({action})", now));
                }
                else if (item.Container == ContainerType.Cube && now - _transmuted <= TransmuteQuiet && !(_lifted.TryGetValue(id, out var lift) && lift >= _transmuted))
                {
                    Say(id, new Truth(Place.Gone, $"taken by the transmute ({action} after 0x4C)", now));
                }
                else if (consumed)
                {
                    Say(id, new Truth(Place.Gone, was?.Place == Place.Gone ? was.Text : $"used up ({action} after 0x3F)", now));
                }
                else if (Asked(_lifted.TryGetValue(id, out var asked) ? asked : null, now))
                {
                    Say(id, new Truth(Place.Cursor, $"lifted to the cursor from the {ItemView.Where(item)} ({action})", now));
                }
                else
                {
                    Say(id, new Truth(Place.Gone, $"taken out of the {ItemView.Where(item)} with no lift asked ({action})", now));
                }

                break;
            case Action.Unequip when !(_takeOff.Remove(item.Directory, out var takeOff) && Asked(takeOff, now)):
                Say(id, new Truth(Place.Gone, $"taken off {item.Directory} with no request asking it ({action}): to the corpse on a death, or used up", now));
                _unequipped = (id, item.Directory, now);
                break;
            case Action.ToCursor or Action.GroundToCursor or Action.Unequip:
                Say(id, new Truth(Place.Cursor, $"to the cursor ({action})", now));
                break;
            case Action.RefillFromInventory when _unequipped is { } emptied && emptied.Slot == item.Directory && now - emptied.At <= SameInstant:
                Say(emptied.Id, new Truth(Place.Gone, $"used up, its slot refilled from the inventory ({action} of {Names.Id(id)})", now));
                Say(id, new Truth(Place.Body, $"worn in {item.Directory} ({action})", now, Slot: item.Directory));
                break;
            case Action.Equip or Action.SwapBodyItem or Action.IndirectlySwapBodyItem or Action.RefillFromInventory:
                Say(id, new Truth(Place.Body, $"worn in {item.Directory} ({action})", now, Slot: item.Directory));
                break;
            case Action.AutoUnequip:
                _truth.Remove(id);
                _due.Remove(id);
                break;
            case Action.DropToGround:
                Say(id, new Truth(Place.Ground, $"dropped to the ground ({action})", now));
                break;
            case Action.ItemInSocket:
                Say(id, new Truth(Place.Gone, $"socketed into an item ({action})", now));
                break;
        }
    }

    /// <summary>A grid whose items take more cells than it has: only items outside it or on top of each other can do that.</summary>
    private void Full(ReplayContext context, ContainerType container, TimeSpan now)
    {
        var grid = Grid(context.Game, container);
        if (grid == null || container == ContainerType.Belt)
        {
            return;
        }

        var items = grid.Items;
        var cells = items.Sum(i => Math.Max(1, (int)i.Width) * Math.Max(1, (int)i.Height));
        var capacity = (int)(grid.Width * grid.Height);
        if (cells > capacity && _overfull.Add(container))
        {
            Report(context, now, Overfull, $"the client's {GridName(container)} holds {items.Count} items taking {cells} cells, more than its {grid.Width}x{grid.Height} grid has ({capacity})");
        }
    }

    private void VerifyDue(ReplayContext context, TimeSpan now)
    {
        if (context.Game.Me == null)
        {
            return;
        }

        if (_cellsDue is { } cellsDue && cellsDue <= now)
        {
            _cellsDue = null;
            foreach (var container in Grids)
            {
                CheckCells(context, container, now == TimeSpan.MaxValue ? context.Now : now);
            }
        }

        if (_due.Count == 0)
        {
            return;
        }

        List<uint> due = null;
        foreach (var (id, at) in _due)
        {
            if (at <= now)
            {
                (due ??= []).Add(id);
            }
        }

        if (due == null)
        {
            return;
        }

        foreach (var id in due.OrderBy(i => i))
        {
            _due.Remove(id);
            Verify(context, id, now == TimeSpan.MaxValue ? context.Now : now);
        }
    }

    private void Verify(ReplayContext context, uint id, TimeSpan now)
    {
        var held = HeldElsewhere(context.Game, id);
        if (!_truth.TryGetValue(id, out var truth))
        {
            if (held?.Place == Place.Cursor)
            {
                _checked++;
                Once(context, CursorNotGiven, id, now, $"the client holds {ItemView.Short(held.Item)} on its cursor, and no packet of the server's in this file ever named the item");
            }

            return;
        }

        _checked++;
        var name = held == null ? NameOf(context, id) : ItemView.Short(held.Item);
        var server = $"the server: {truth.Text} at {TraceTime.Format(truth.At)}";
        switch (truth.Place)
        {
            case Place.Gone or Place.Ground when held != null:
                Once(context, held.Place == Place.Cursor ? CursorNotGiven : HeldAfterRemoval, id, now,
                    $"the client still holds {name} in {held.Where}; {server}");
                break;
            case Place.Grid when held == null:
                if (!_evicted.Contains(id))
                {
                    Once(context, Misplaced, id, now, $"the client holds {name} nowhere; {server}");
                }

                break;
            case Place.Grid when held.Place == Place.Cursor:
                Once(context, CursorNotGiven, id, now, $"the client holds {name} on its cursor; {server}");
                break;
            case Place.Grid when held.Place != Place.Grid || held.Container != truth.Container || held.Cell?.X != truth.Cell?.X || held.Cell?.Y != truth.Cell?.Y:
                Once(context, Misplaced, id, now, $"the client holds {name} in {held.Where}; {server}");
                break;
            case Place.Body when held?.Place == Place.Cursor:
                Once(context, CursorNotGiven, id, now, $"the client holds {name} on its cursor; {server}");
                break;
            case Place.Body when held == null || held.Place != Place.Body || held.Slot != truth.Slot:
                Once(context, Misplaced, id, now, $"the client holds {name} {(held == null ? "nowhere" : $"in {held.Where}")}; {server}");
                break;
            case Place.Cursor when held?.Place != Place.Cursor:
                Once(context, CursorNotTaken, id, now, $"the client holds {name} {(held == null ? "nowhere" : $"in {held.Where}")}, not on its cursor; {server}");
                break;
        }
    }

    /// <summary>
    /// The cells the client marks taken in a grid against the cells its items cover: the bot plans where things fit by
    /// the marks, so a cell marked taken that no item covers wastes room, and one an item covers marked free invites a
    /// put onto it.
    /// </summary>
    private void CheckCells(ReplayContext context, ContainerType container, TimeSpan now)
    {
        var grid = Grid(context.Game, container);
        if (grid == null || container == ContainerType.Belt)
        {
            return;
        }

        _cellChecks++;
        int width = (int)grid.Width, height = (int)grid.Height;
        var covered = new bool[height, width];
        foreach (var item in grid.Items)
        {
            if (ItemView.Cell(item) is not { } cell)
            {
                continue;
            }

            for (var y = cell.Y; y < Math.Min(height, cell.Y + Math.Max(1, (int)item.Height)); y++)
            {
                for (var x = cell.X; x < Math.Min(width, cell.X + Math.Max(1, (int)item.Width)); x++)
                {
                    covered[y, x] = true;
                }
            }
        }

        List<string> stale = [], free = [];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var marked = grid.IsOccupied(x, y);
                if (marked && !covered[y, x])
                {
                    stale.Add($"({x}, {y})");
                }
                else if (!marked && covered[y, x])
                {
                    free.Add($"({x}, {y})");
                }
            }
        }

        var said = stale.Count == 0 && free.Count == 0 ? null : string.Join(" ", stale) + "|" + string.Join(" ", free);
        if (said == _cells.GetValueOrDefault(container))
        {
            return;
        }

        _cells[container] = said;
        if (said != null)
        {
            Report(context, now, GridCells,
                $"the client's {GridName(container)} grid "
                + string.Join(" and ", new[]
                {
                    stale.Count == 0 ? null : $"marks {stale.Count} cell{(stale.Count == 1 ? "" : "s")} taken that none of its items covers: {string.Join(", ", stale)}",
                    free.Count == 0 ? null : $"marks {free.Count} cell{(free.Count == 1 ? "" : "s")} free that its items cover: {string.Join(", ", free)}",
                }.Where(s => s != null))
                + $"; it holds {string.Join(", ", grid.Items.OrderBy(i => i.Id).Select(i => $"{ItemView.Short(i)} at {ItemView.Grid(ItemView.Cell(i))} ({Math.Max(1, (int)i.Width)}x{Math.Max(1, (int)i.Height)})"))}");
        }
    }

    private static string NameOf(ReplayContext context, uint id) => context.Game.Items.TryGetValue(id, out var item) ? ItemView.Short(item) : Names.Id(id);

    private void Once(ReplayContext context, string kind, uint id, TimeSpan at, string detail)
    {
        if (_reported.Add((kind, id)))
        {
            Report(context, at, kind, detail);
        }
    }
}
