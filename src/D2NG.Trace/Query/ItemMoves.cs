using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.ItemData;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Query;

/// <summary>
/// Something that happened to an item of one client. <see cref="Asked"/> is when the client sent the request the server's
/// packet answered (the first of its repeats); null when the server did it unasked. Unanswered requests are events of
/// their own, timed at the request.
/// </summary>
public sealed record ItemEvent(TimeSpan At, string Client, string Kind, uint Item, string Name, string Detail)
{
    public TimeSpan? Asked { get; init; }

    /// <summary>The request, as "pick up (0x16)".</summary>
    public string Request { get; init; }

    /// <summary>How many times the request was sent again before the answer.</summary>
    public int Repeats { get; init; }

    /// <summary>The server did this with no request of the client's asking for it.</summary>
    public bool Unasked { get; init; }

    /// <summary>What the item packet decoded beyond name and quality: level, sockets, durability, stats; null when nothing.</summary>
    public string Details { get; init; }

    /// <summary>Gold this event moved: what a sale or the gold attribute brought in (positive) or a purchase or repair cost (negative); null for none.</summary>
    public long? Gold { get; init; }

    /// <summary>For a gold pile picked up: what the pile held on the ground, which is not always what reached the person.</summary>
    public long? Pile { get; init; }
}

public static class ItemKinds
{
    public const string PickedUp = "picked up";
    public const string ToCursor = "to cursor";
    public const string Put = "put";
    public const string Equipped = "equipped";
    public const string Unequipped = "unequipped";
    public const string Swapped = "swapped";
    public const string Dropped = "dropped";
    public const string Sold = "sold";
    public const string Bought = "bought";
    public const string Gambled = "gambled";
    public const string Repaired = "repaired";
    public const string Identified = "identified";
    public const string Transmuted = "transmuted";
    public const string Used = "used";
    public const string Activated = "activated";
    public const string GoldReceived = "gold received";
    public const string GoldLost = "gold lost";
    public const string Mercenary = "mercenary";
    public const string Socketed = "socketed";
    public const string Stacked = "stacked";
    public const string WeaponSwitch = "weapon switch";
    public const string TradedAway = "traded away";
    public const string Offered = "offered to it";
    public const string Received = "received";
    public const string Moved = "moved by server";
    public const string Removed = "removed by server";
    public const string Refused = "refused";
    public const string Unanswered = "UNANSWERED";
    public const string Repeat = "repeat";
    public const string Loaded = "loaded";
}

/// <summary>
/// Every item event of one client, worked out from the packets alone: the client's item requests (0x16-0x2A, 0x32-0x37,
/// 0x4C, 0x61, 0x63) and the server's item packets (0x9C/0x9D), vendor answers (0x2A), uses (0x3F), item removals (0x0A)
/// and gold attributes. A request is answered by the first server packet that names its item with a fitting action
/// within <see cref="AnswerTimeout"/> of its last send; a request sent again while open is a repeat of it. What the
/// server sends in the first two seconds of a file before any request - the items a character joins with - is one
/// "loaded" event.
/// <para>
/// Gold reaches the person by two answers the realm sends separately: a pile leaves the ground (0x0A, to every client
/// in sight, with the pick-up sound only for the one who took it) and the gold attribute rises later, often once for
/// several piles and, in a party, for every member's share. A pile is picked up by its own 0x0A and sound; the gold
/// is what the attribute says arrived.
/// </para>
/// <para>
/// The gold attribute reports, a moment after the change, everything that moved the gold on the person since its last
/// report: as an add (0x19) when that is 1 to 255, as the new figure otherwise. A vendor answer carries the figure at once,
/// so an add after one first repeats what the answers since the last report already moved; only the rest is new gold.
/// </para>
/// </summary>
public sealed class ItemLedger : ICachedObserver
{
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoadWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransmuteQuiet = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SameDeal = TimeSpan.FromSeconds(1);

    /// <summary>The gold attribute packets that follow a vendor answer arrive within a millisecond of it, not at the same tick.</summary>
    private static readonly TimeSpan SameInstant = TimeSpan.FromMilliseconds(100);

    /// <summary>How long after its "was slain by" message a fall of the gold on the person is the death's.</summary>
    private static readonly TimeSpan DeathGold = TimeSpan.FromSeconds(2);
    private const uint NoItem = 0xFFFFFFFF;
    private const byte GoldOnPerson = 0x0E;
    private const byte GoldInStash = 0x0F;

    /// <summary>The vendor's answer that refuses a deal; its result byte says why.</summary>
    private const byte RefusedTransaction = 0;

    /// <summary>The vendor's answer to repairing everything, which names no item.</summary>
    private const byte RepairTransaction = 1;

    /// <summary>The client's request to drop gold from the person (0x50); each one drops its own pile.</summary>
    private const byte DropGold = 0x50;

    /// <summary>The sound the realm plays for the client who picked an item up, sent only to that client.</summary>
    private const ushort PickUpSound = 1;

    private readonly List<ItemEvent> _events = [];
    private readonly List<Pending> _open = [];
    private readonly Dictionary<uint, bool> _identified = [];
    private readonly Dictionary<uint, TimeSpan> _consumed = [];
    private readonly Dictionary<uint, int> _bought = [];
    private readonly Dictionary<string, int> _loadedCounts = [];
    private readonly Dictionary<uint, (byte Type, TimeSpan At)> _lastAnswer = [];
    private readonly Dictionary<uint, Activation> _activations = [];
    private readonly Dictionary<uint, Leaving> _leaving = [];
    private readonly Dictionary<uint, string> _leftFor = [];
    private readonly List<(Pending Pick, long Amount)> _piles = [];
    private readonly List<(uint Id, long Amount, TimeSpan At, bool Own)> _gone = [];
    private readonly HashSet<uint> _repaired = [];
    private TimeSpan _repairedAt;
    private Transmute _transmute;
    private (List<int> Indices, long TotalBefore, TimeSpan At)? _price;
    private (Pending Request, TimeSpan At)? _lastBuy;
    private (long Amount, long After, TimeSpan At)? _rise;
    private (long Amount, TimeSpan At)? _stashFall;
    private (long Amount, TimeSpan At)? _stashRise;
    private (long Amount, long After, TimeSpan At, bool Death)? _fall;
    private (int Index, DirectoryType Slot, TimeSpan At)? _unequipped;
    private bool _goldKnown;
    private bool _requestedInFile;
    private ClientFile _file;
    private TimeSpan? _slainAt;
    private (Pending Request, TimeSpan At)? _identifyAll;
    private readonly Dictionary<uint, Item> _boughtItems = [];

    /// <summary>What vendor answers moved the gold on the person since the gold attribute last reported it, and when the last of them came.</summary>
    private long _unreported;
    private TimeSpan _unreportedAt;
    private long _goldPerson;
    private long _goldStash;
    private TimeSpan? _loadStart;
    private TimeSpan? _loadUntil;
    private bool _requested;
    private TimeSpan? _lastOffer;
    private TimeSpan? _tradeAccepted;

    /// <summary>How long after the other side's offer or the client's accept a trade's items still count as the trade's.</summary>
    private static readonly TimeSpan TradeEnd = TimeSpan.FromSeconds(10);

    /// <summary>The trade window's accept button (0x4F 4).</summary>
    private const byte AcceptTrade = 4;

    /// <summary>The cube's transmute button (0x4F 0x18), which the bot clicks rather than sending 0x4C.</summary>
    public const byte TransmuteButton = 0x18;
    private string _client;

    public string CacheKey => "ItemLedger 4";

    public IReadOnlyList<ItemEvent> Events => _events;

    private sealed class Pending
    {
        public byte Type;
        public uint Item;
        public TimeSpan At;
        public TimeSpan Last;
        public int Repeats;

        /// <summary>When the server had already answered the same request about the same item, shortly before this one was sent.</summary>
        public TimeSpan? AfterAnswer;
        public string What;
        public string Name;
        public bool Gold;
        public uint Amount;
        public int Extra;

        /// <summary>For a pick up: when the item's removal from the ground (0x0A) came, which precedes the item packet that brings it in.</summary>
        public TimeSpan? LeftGround;
    }

    /// <summary>A 0x3F: the item was used or activated. It was used up only if it leaves the client within <see cref="SameDeal"/>.</summary>
    private sealed record Activation(TimeSpan At, Pending Request, string Name, string Where);

    /// <summary>An item the server took out of a container unasked; a put of it soon after makes the two one move.</summary>
    private sealed record Leaving(TimeSpan At, string Name, string Where);

    private sealed class Transmute
    {
        public TimeSpan At;
        public TimeSpan Last;
        public int Repeats;
        public string What;
        public readonly List<string> Inputs = [];
        public readonly List<string> Outputs = [];
    }

    public void Before(ReplayContext context, TraceRecord record)
    {
        _client = context.Client;
        var now = context.Now;
        if (context.File != _file)
        {
            NewFile(context.File);
        }

        if (_price is { } price && now - price.At > SameInstant && (Paid(price) != 0 || now - price.At > SameDeal))
        {
            SettlePrice();
        }

        SettlePiles(now);
        if (_rise is { } rise && now - rise.At > SameInstant)
        {
            SettleRise();
        }

        if (_fall is { } fall && now - fall.At > SameDeal)
        {
            SettleFall();
        }

        SettleActivations(now);
        SettleLeaving(now);
        Expire(now, null);
        if (_transmute != null && now - _transmute.Last > TransmuteQuiet)
        {
            FlushTransmute();
        }

        var p = record.Payload;
        if (p.Length == 0 || context.Game.Me == null)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            Sent(context, p, now);
        }
        else if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            Received(context, p, now);
        }
    }

    public void After(ReplayContext context, TraceRecord record)
    {
    }

    public void Finished(ReplayContext context)
    {
        _client = context.Client;
        SettlePrice();
        SettlePiles(TimeSpan.MaxValue);
        SettleRise();
        SettleFall();
        SettleActivations(TimeSpan.MaxValue);
        SettleLeaving(TimeSpan.MaxValue);
        FlushTransmute();
        Expire(TimeSpan.MaxValue, "the client's trace ended first");
        if (_loadStart is { } start && _loadedCounts.Count > 0)
        {
            var total = _loadedCounts.Values.Sum();
            _events.Add(new ItemEvent(start, _client, ItemKinds.Loaded, 0, $"{total} items",
                string.Join(", ", _loadedCounts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}"))) { Unasked = true });
        }

        var sorted = _events.OrderBy(e => e.At).ToList();
        _events.Clear();
        _events.AddRange(sorted);
        _open.Clear();
        _identified.Clear();
        _consumed.Clear();
        _bought.Clear();
        _loadedCounts.Clear();
        _lastAnswer.Clear();
        _activations.Clear();
        _leaving.Clear();
        _leftFor.Clear();
        _piles.Clear();
        _gone.Clear();
        _repaired.Clear();
        _repairedAt = default;
        _transmute = null;
        _price = null;
        _lastBuy = null;
        _rise = null;
        _stashFall = null;
        _stashRise = null;
        _fall = null;
        _unequipped = null;
        _unreported = 0;
        _unreportedAt = default;
        _goldKnown = false;
        _requestedInFile = false;
        _file = null;
        _slainAt = null;
        _identifyAll = null;
        _boughtItems.Clear();
        _goldPerson = 0;
        _goldStash = 0;
        _loadStart = null;
        _loadUntil = null;
        _requested = false;
        _lastOffer = null;
        _tradeAccepted = null;
    }

    private int Emit(TimeSpan at, string kind, uint id, string name, string detail, Pending answered = null, Item item = null, long? gold = null)
    {
        if (answered is { Type: not 0, Item: not 0 } && kind != ItemKinds.Refused)
        {
            _lastAnswer[answered.Item] = (answered.Type, at);
        }

        _events.Add(new ItemEvent(at, _client, kind, id, name, detail)
        {
            Asked = answered?.At,
            Request = answered?.What,
            Repeats = answered?.Repeats ?? 0,
            Unasked = answered == null,
            Details = item == null ? null : DetailLine(item),
            Gold = gold,
        });
        return _events.Count - 1;
    }

    /// <summary>A rejoin is a fresh game state: the gold the character joins with is where its gold starts again.</summary>
    private void NewFile(ClientFile file)
    {
        if (_file != null)
        {
            SettleRise();
            SettleFall();
            _goldPerson = 0;
            _goldStash = 0;
            _goldKnown = false;
            _unreported = 0;
        }

        _file = file;
        _requestedInFile = false;
    }

    private void Open(byte type, uint item, string what, string name, TimeSpan at, bool gold = false, int extra = 0, uint amount = 0)
    {
        _requested = true;
        _requestedInFile = true;
        foreach (var open in _open)
        {
            if (open.Type == type && open.Item == item && type != DropGold)
            {
                open.Repeats++;
                open.Last = at;
                return;
            }
        }

        TimeSpan? afterAnswer = item != 0 && _lastAnswer.TryGetValue(item, out var answer) && answer.Type == type && at - answer.At <= AnswerTimeout ? answer.At : null;
        _open.Add(new Pending { Type = type, Item = item, At = at, Last = at, What = what, Name = name, Gold = gold, Extra = extra, Amount = amount, AfterAnswer = afterAnswer });
    }

    /// <summary>The oldest open request of one of the types naming the item (or naming none, when <paramref name="anyItem"/>), taken off the open list.</summary>
    private Pending Take(uint item, bool anyItem, params byte[] types)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            if (Array.IndexOf(types, _open[i].Type) >= 0 && (anyItem || _open[i].Item == item))
            {
                var found = _open[i];
                _open.RemoveAt(i);
                return found;
            }
        }

        return null;
    }

    private bool IsOpen(uint item, params byte[] types) => _open.Any(o => o.Item == item && Array.IndexOf(types, o.Type) >= 0);

    private void Expire(TimeSpan now, string why)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var open = _open[i];
            if (now != TimeSpan.MaxValue && now - open.Last <= AnswerTimeout)
            {
                continue;
            }

            _open.RemoveAt(i--);
            var sent = open.Repeats > 0 ? $", sent {open.Repeats + 1} times" : "";
            if (open.LeftGround is { } left && !open.Gold)
            {
                _events.Add(new ItemEvent(left, _client, ItemKinds.Refused, open.Item, open.Name,
                    $"{open.What}{sent}: the item left the ground (0x0A) but no item packet brought it to the client - another player took it?")
                { Asked = open.At, Request = open.What, Repeats = open.Repeats });
                continue;
            }

            if (open.AfterAnswer is { } answered)
            {
                _events.Add(new ItemEvent(open.At, _client, ItemKinds.Repeat, open.Item, open.Name ?? Names.Id(open.Item),
                    $"{open.What}{sent}: sent again after the server had answered the same request at {TraceTime.Format(answered)}; it answers once")
                { Request = open.What, Repeats = open.Repeats });
                continue;
            }

            _events.Add(new ItemEvent(open.At, _client, ItemKinds.Unanswered, open.Item, open.Name ?? (open.Item == 0 ? "-" : Names.Id(open.Item)),
                $"{open.What}{sent}: the server never answered{(why == null ? $" (waited {AnswerTimeout.TotalSeconds:0}s)" : $" before {why}")}")
            { Request = open.What, Repeats = open.Repeats });
        }
    }

    private static string NameOf(ReplayContext context, uint id)
        => context.Game.Items.TryGetValue(id, out var item) ? ItemView.Short(item) : Names.Id(id);

    private static Item Known(ReplayContext context, uint id) => context.Game.Items.TryGetValue(id, out var item) ? item : null;

    private void Sent(ReplayContext context, byte[] p, TimeSpan now)
    {
        var type = p[0];
        uint At(int offset) => p.Length >= offset + 4 ? PacketDecoder.U32(p, offset) : 0;
        string Request(string what) => $"{what} (0x{type:X2})";
        switch (type)
        {
            case 0x16 when p.Length >= 9:
                var picked = Known(context, At(5));
                Open(type, At(5), Request("pick up"), NameOf(context, At(5)), now, gold: picked?.IsGold == true, amount: picked?.Amount ?? 0);
                break;
            case 0x17 when p.Length >= 5:
                Open(type, At(1), Request("drop"), NameOf(context, At(1)), now);
                break;
            case 0x18 when p.Length >= 17:
                Open(type, At(1), Request($"put at ({At(5)}, {At(9)}) in {Buffer(At(13))}"), NameOf(context, At(1)), now);
                break;
            case 0x19 when p.Length >= 5:
                Open(type, At(1), Request("lift"), NameOf(context, At(1)), now);
                break;
            case 0x1A when p.Length >= 7:
                Open(type, At(1), Request($"equip in {(DirectoryType)PacketDecoder.U16(p, 5)}"), NameOf(context, At(1)), now);
                break;
            case 0x1C when p.Length >= 3:
                var slot = (DirectoryType)PacketDecoder.U16(p, 1);
                var worn = context.Game.Me.Equipment.TryGetValue(slot, out var wornItem) ? wornItem.Id : 0;
                Open(type, worn, Request($"take off {slot}"), worn == 0 ? $"({slot})" : NameOf(context, worn), now, extra: (int)slot);
                break;
            case 0x1D when p.Length >= 9:
                Open(type, At(1), Request($"swap with {(DirectoryType)At(5)}"), NameOf(context, At(1)), now);
                break;
            case 0x1F when p.Length >= 9:
                Open(type, At(1), Request($"swap with {NameOf(context, At(5))}"), NameOf(context, At(1)), now);
                break;
            case 0x20 or 0x26 when p.Length >= 5:
                Open(type, At(1), Request(type == 0x20 ? "use" : "use from belt"), NameOf(context, At(1)), now);
                break;
            case 0x21 when p.Length >= 9:
                Open(type, At(1), Request($"stack onto {NameOf(context, At(5))}"), NameOf(context, At(1)), now);
                break;
            case 0x23 or 0x63 when p.Length >= 5:
                Open(type, At(1), Request("to belt"), NameOf(context, At(1)), now);
                break;
            case 0x24 when p.Length >= 5:
                Open(type, At(1), Request("lift from belt"), NameOf(context, At(1)), now);
                break;
            case 0x27 when p.Length >= 9:
                Open(type, At(1), Request($"identify with {NameOf(context, At(5))}"), NameOf(context, At(1)), now);
                break;
            case 0x28 when p.Length >= 9:
                Open(type, At(1), Request($"socket into {NameOf(context, At(5))}"), NameOf(context, At(1)), now);
                break;
            case 0x2A when p.Length >= 5:
                Open(type, At(1), Request("to cube"), NameOf(context, At(1)), now);
                break;
            case 0x32 when p.Length >= 13:
                var gamble = (p[9] & 0x02) != 0;
                Open(type, At(5), Request(gamble ? "gamble" : "buy"), NameOf(context, At(5)), now, extra: gamble ? 1 : 0);
                break;
            case 0x33 when p.Length >= 9:
                Open(type, At(5), Request("sell"), NameOf(context, At(5)), now);
                break;
            case 0x34:
                Open(type, 0, Request("identify all at an NPC"), null, now);
                break;
            case 0x35 when p.Length >= 9:
                Open(type, 0, Request("repair"), At(5) is var repaired and not 0 ? NameOf(context, repaired) : null, now);
                break;
            case 0x37 when p.Length >= 5:
                Open(type, At(1), Request("identify gambled"), NameOf(context, At(1)), now);
                break;
            case 0x4C:
            case 0x4F when p.Length >= 2 && p[1] == TransmuteButton:
                _requested = true;
                _requestedInFile = true;
                if (_transmute != null)
                {
                    _transmute.Repeats++;
                }
                else
                {
                    _transmute = new Transmute { At = now, Last = now, What = type == 0x4C ? "transmute (0x4C)" : "transmute (0x4F 0x18)" };
                }

                break;
            case 0x60:
                Open(type, 0, Request("swap weapons"), null, now);
                break;
            case DropGold when p.Length >= 9:
                Open(type, 0, Request($"drop {At(5):N0} gold"), "Gold", now, gold: true, amount: At(5));
                break;
            case 0x4F when p.Length >= 2 && p[1] == AcceptTrade:
                _tradeAccepted = now;
                break;
            case 0x61 when p.Length >= 3:
                var held = context.Game.CursorItem;
                Open(type, 0, Request($"mercenary {(DirectoryType)PacketDecoder.U16(p, 1)}"), held == null ? null : ItemView.Short(held), now);
                break;
        }
    }

    private static string Buffer(uint buffer) => buffer switch
    {
        0 => "inventory",
        2 => "trade page",
        3 => "cube",
        4 => "stash",
        _ => $"buffer {buffer}",
    };

    private void Received(ReplayContext context, byte[] p, TimeSpan now)
    {
        switch (p[0])
        {
            case 0x9C or 0x9D when p.Length >= 8:
                ItemPacket(context, p, now);
                break;
            case 0x2A when p.Length >= 15:
                Vendor(context, p, now);
                break;
            case 0x3F when p.Length >= 6:
                var used = PacketDecoder.U32(p, 2);
                var request = Take(used, false, 0x26, 0x20);
                var item = Known(context, used);
                if (_activations.Remove(used, out var earlier))
                {
                    EmitActivation(used, earlier);
                }

                _activations[used] = new Activation(now, request, request?.Name ?? NameOf(context, used), item == null ? null : Holder(context, used) ?? ItemView.Where(item));
                break;
            case 0x0A when p.Length >= 6 && p[1] == (byte)EntityType.Item:
                Removal(context, PacketDecoder.U32(p, 2), now);
                break;
            case 0x1D or 0x1E or 0x1F when p.Length >= 3 && p[1] is GoldOnPerson or GoldInStash:
                Gold(context, p, now);
                break;
            case 0x19 when p.Length >= 2:
                GoldAdded(p[1], now);
                break;
            case 0x5A when DeathCheck.SlainSelf(context, p):
                _slainAt = now;
                break;
            case 0x2C when p.Length >= 8 && p[1] == (byte)EntityType.Player && PacketDecoder.U32(p, 2) == context.SelfId && PacketDecoder.U16(p, 6) == PickUpSound:
                PickedUpPiles(now);
                break;
            case 0x3E when p.Length >= 5:
                if (now - _repairedAt > SameInstant)
                {
                    _repaired.Clear();
                }

                _repaired.Add(PacketDecoder.U32(p, 1));
                _repairedAt = now;
                break;
        }
    }

    /// <summary>The pick-up sound came with the 0x0A: the gold piles that just left the ground are this client's.</summary>
    private void PickedUpPiles(TimeSpan now)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var pick = _open[i];
            if (pick.Type != 0x16 || !pick.Gold || pick.LeftGround is not { } left || now - left > SameInstant)
            {
                continue;
            }

            _open.RemoveAt(i--);
            _piles.Add((pick, pick.Amount));
            for (var g = 0; g < _gone.Count; g++)
            {
                if (_gone[g].Id == pick.Item)
                {
                    _gone[g] = _gone[g] with { Own = true };
                }
            }

            _events.Add(new ItemEvent(left, _client, ItemKinds.PickedUp, pick.Item, "Gold", $"pile {Names.Id(pick.Item)} of {pick.Amount:N0} gold, gone from the ground (0x0A)")
            {
                Asked = pick.At,
                Request = pick.What,
                Repeats = pick.Repeats,
                Pile = pick.Amount,
            });
            _lastAnswer[pick.Item] = (pick.Type, left);
        }
    }

    /// <summary>A gold pile that left the ground without the pick-up sound went to another player.</summary>
    private void SettlePiles(TimeSpan now)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var pick = _open[i];
            if (pick.Type != 0x16 || !pick.Gold || pick.LeftGround is not { } left || (now != TimeSpan.MaxValue && now - left <= SameInstant))
            {
                continue;
            }

            _open.RemoveAt(i--);
            var sent = pick.Repeats > 0 ? $", sent {pick.Repeats + 1} times" : "";
            _events.Add(new ItemEvent(left, _client, ItemKinds.Refused, pick.Item, "Gold",
                $"{pick.What}{sent}: the pile of {pick.Amount:N0} left the ground (0x0A) without the pick-up sound - another player took it")
            { Asked = pick.At, Request = pick.What, Repeats = pick.Repeats });
        }
    }

    private void EmitActivation(uint id, Activation activation)
        => Emit(activation.At, ItemKinds.Activated, id, activation.Name, activation.Where == null ? "" : $"in {activation.Where}", activation.Request);

    /// <summary>A 0x3F whose item stayed with the client: the cube or a tome opened, not something used up.</summary>
    private void SettleActivations(TimeSpan now)
    {
        if (_activations.Count == 0)
        {
            return;
        }

        foreach (var (id, activation) in _activations.Where(a => now == TimeSpan.MaxValue || now - a.Value.At > SameDeal).OrderBy(a => a.Value.At).ThenBy(a => a.Key).ToList())
        {
            _activations.Remove(id);
            EmitActivation(id, activation);
        }
    }

    /// <summary>A 0x3F followed by the item leaving: it was used up.</summary>
    private bool UsedUp(uint id, TimeSpan now, string where)
    {
        if (!_activations.Remove(id, out var activation) || now - activation.At > SameDeal)
        {
            return false;
        }

        Emit(activation.At, ItemKinds.Used, id, activation.Name, $"from {activation.Where ?? where}", activation.Request);
        _consumed[id] = now;
        return true;
    }

    private void SettleLeaving(TimeSpan now)
    {
        if (_leaving.Count == 0)
        {
            return;
        }

        foreach (var (id, leaving) in _leaving.Where(l => now == TimeSpan.MaxValue || now - l.Value.At > SameDeal).OrderBy(l => l.Value.At).ThenBy(l => l.Key).ToList())
        {
            _leaving.Remove(id);
            Emit(leaving.At, ItemKinds.Removed, id, leaving.Name, $"from {leaving.Where}; no request of the client's asked for it");
        }
    }

    /// <summary>
    /// The gold attribute: the stash's figure is kept for prices; a rise or fall on the person outside a vendor's answer
    /// is gold received or gold gone, settled a moment later so a withdrawal or deposit in the same instant is not counted.
    /// </summary>
    private void Gold(ReplayContext context, byte[] p, TimeSpan now)
    {
        long value = p[0] switch
        {
            0x1D => p[2],
            0x1E when p.Length >= 4 => PacketDecoder.U16(p, 2),
            0x1F when p.Length >= 6 => PacketDecoder.U32(p, 2),
            _ => -1,
        };
        if (value < 0)
        {
            return;
        }

        if (p[1] == GoldInStash)
        {
            if (value < _goldStash)
            {
                _stashFall = (_goldStash - value, now);
            }
            else if (value > _goldStash)
            {
                _stashRise = (value - _goldStash, now);
            }

            _goldStash = value;
            return;
        }

        var known = _goldKnown || _requestedInFile;
        _goldKnown = true;
        _unreported = 0;
        var gained = value - _goldPerson;
        _goldPerson = value;
        if (!known)
        {
            return;
        }

        if (gained > 0)
        {
            Rise(gained, now);
        }
        else if (gained < 0 && _price == null)
        {
            SettleFall();
            _fall = (-gained, value, now, (value == 0 && context.Life == 0) || now - _slainAt <= DeathGold);
        }
    }

    /// <summary>A gold add (0x19): what the vendor answers since the gold attribute's last report already counted is their echo, the rest is new gold.</summary>
    private void GoldAdded(long added, TimeSpan now)
    {
        var fresh = added;
        if (_unreported != 0 && now - _unreportedAt <= AnswerTimeout)
        {
            fresh = added - _unreported;
            _unreported = fresh < 0 ? -fresh : 0;
            fresh = Math.Max(0, fresh);
        }
        else
        {
            _unreported = 0;
        }

        _goldPerson += fresh;
        Rise(fresh, now);
    }

    /// <summary>A vendor answer set the gold on the person to its figure: the gold attribute reports that change later.</summary>
    private void VendorFigure(long gold, TimeSpan now)
    {
        _unreported += gold - _goldPerson;
        _unreportedAt = now;
        _goldPerson = gold;
        _goldKnown = true;
    }

    /// <summary>
    /// Gold that left the person with no vendor answer: into the stash in the same moment (not counted), a pile the client
    /// asked to drop, what the server took on a death, or else unexplained.
    /// </summary>
    private void SettleFall()
    {
        if (_fall is not { } fall)
        {
            return;
        }

        _fall = null;
        if (_stashRise is { } rise && rise.Amount == fall.Amount && (fall.At - rise.At).Duration() <= SameDeal)
        {
            _stashRise = null;
            return;
        }

        var dropped = _open.FirstOrDefault(o => o.Type == DropGold && o.Amount == fall.Amount);
        if (dropped != null)
        {
            _open.Remove(dropped);
            Emit(fall.At, ItemKinds.Dropped, 0, "Gold", $"-{fall.Amount:N0} gold to the ground, {fall.After:N0} on the person after", dropped, gold: -fall.Amount);
            return;
        }

        _events.Add(new ItemEvent(fall.At, _client, ItemKinds.GoldLost, 0, "Gold", fall.Death
            ? $"-{fall.Amount:N0} on the person on the death, {fall.After:N0} after"
            : $"-{fall.Amount:N0} on the person, {fall.After:N0} after; no request, deal or death of the client's explains it") { Unasked = true, Gold = -fall.Amount });
    }

    /// <summary>Gold that reached the person, by the gold attribute or a small amount added (0x19); a rise in the same instant adds to it.</summary>
    private void Rise(long gained, TimeSpan now)
    {
        if (gained <= 0)
        {
            return;
        }

        var after = _goldKnown ? _goldPerson : -1;
        _rise = _rise is { } rise ? (rise.Amount + gained, after, rise.At) : (gained, after, now);
    }

    /// <summary>
    /// Gold that reached the person: for the piles this client picked up since the last rise, a share of a pile another
    /// party member took, or from no pile at all. A rise matched by the stash falling as much is a withdrawal.
    /// </summary>
    private void SettleRise()
    {
        if (_rise is not { } rise)
        {
            return;
        }

        _rise = null;
        if (_stashFall is { } fall && fall.Amount == rise.Amount && (rise.At - fall.At).Duration() <= SameInstant)
        {
            _stashFall = null;
            return;
        }

        _piles.RemoveAll(p => rise.At - p.Pick.LeftGround > AnswerTimeout);
        var piles = _piles.Where(p => p.Pick.LeftGround <= rise.At).ToList();
        _piles.RemoveAll(piles.Contains);
        var text = rise.After < 0 ? $"+{rise.Amount:N0} on the person" : $"+{rise.Amount:N0} on the person ({rise.After:N0} after)";
        if (piles.Count > 0)
        {
            var held = piles.Sum(p => p.Amount);
            var first = piles[0].Pick;
            _events.Add(new ItemEvent(rise.At, _client, ItemKinds.GoldReceived, first.Item, "Gold",
                $"{text} for pile{(piles.Count == 1 ? "" : "s")} {string.Join(", ", piles.Select(p => Names.Id(p.Pick.Item)))}"
                + (held == rise.Amount ? "" : $", which held {held:N0} on the ground"))
            {
                Asked = first.At,
                Request = first.What,
                Repeats = piles.Sum(p => p.Pick.Repeats),
                Gold = rise.Amount,
            });
            return;
        }

        var shared = _gone.LastOrDefault(g => !g.Own && rise.At - g.At <= SameDeal && rise.At >= g.At);
        _gone.RemoveAll(g => rise.At - g.At > SameDeal);
        _events.Add(new ItemEvent(rise.At, _client, ItemKinds.GoldReceived, shared.Id, "Gold",
            shared.Id != 0 ? $"{text}: a share of pile {Names.Id(shared.Id)} ({shared.Amount:N0} on the ground) another player picked up" : $"{text}, from no pile this client picked up")
        {
            Unasked = true,
            Gold = rise.Amount,
        });
    }

    private void Vendor(ReplayContext context, byte[] p, TimeSpan now)
    {
        var type = p[1];
        var together = _price is { } pending && type is 4 or 5 && now - pending.At <= SameInstant && Paid(pending) == 0 && _events[pending.Indices[0]].Kind is ItemKinds.Bought or ItemKinds.Gambled
            ? pending
            : ((List<int> Indices, long TotalBefore, TimeSpan At)?)null;
        if (together == null)
        {
            SettlePrice();
        }

        var id = PacketDecoder.U32(p, 7);
        var gold = (long)(int)PacketDecoder.U32(p, 11);
        var totalBefore = _goldPerson + _goldStash;
        var personBefore = _goldPerson;
        if (type == RepairTransaction && id == NoItem && IsOpen(0, 0x35))
        {
            VendorFigure(gold, now);
            var repair = Take(0, false, 0x35);
            var items = now - _repairedAt <= SameInstant ? _repaired.Count : 0;
            _price = ([Emit(now, ItemKinds.Repaired, 0, repair.Name ?? "all items", items > 0 ? $"{items} item{(items == 1 ? "" : "s")} updated (0x3E)" : "", repair)], totalBefore, now);
            return;
        }

        if (type == RefusedTransaction || id == NoItem)
        {
            var refused = (id == NoItem ? null : Take(id, false, 0x32, 0x33)) ?? Take(0, true, 0x32, 0x33, 0x35);
            if (refused != null)
            {
                Emit(now, ItemKinds.Refused, refused.Item, refused.Name ?? "-", $"the vendor refused it (transaction {type}, result {p[2]})", refused);
            }

            if (id != NoItem)
            {
                VendorFigure(gold, now);
            }

            return;
        }

        VendorFigure(gold, now);
        switch (type)
        {
            case NpcTransactionPacket.SellTransaction:
                var sale = Take(id, false, 0x33);
                var sold = Known(context, id);
                var received = gold - personBefore;
                Emit(now, ItemKinds.Sold, id, sale?.Name ?? NameOf(context, id),
                    received > 0 ? $"+{received:N0} gold, {gold:N0} on the person after" : $"no gold gained: the vendor's figure, {gold:N0} on the person after, did not rise", sale, sold, received);
                _consumed[id] = now;
                break;
            case 4 or 5:
                var purchase = Take(0, true, 0x32);
                if (purchase == null && _lastBuy is { } last && now - last.At <= SameDeal)
                {
                    purchase = last.Request;
                }

                if (purchase != null)
                {
                    _lastBuy = (purchase, now);
                }

                var gambled = purchase?.Extra == 1;
                var target = Known(context, id);
                var kind = gambled ? ItemKinds.Gambled : ItemKinds.Bought;
                var what = type == 5 ? "a refill" : gambled ? $"from listing {purchase.Name}" : purchase?.Name == null ? "" : $"listing {purchase.Name}";
                var index = Emit(now, kind, id, type == 5 ? NameOf(context, id) : target != null ? ItemView.Short(target) : Names.Id(id), what, purchase);
                if (type != 5)
                {
                    _bought[id] = index;
                }

                _price = together is { } group ? ([.. group.Indices, index], group.TotalBefore, group.At) : ([index], totalBefore, now);
                break;
            default:
                var deal = Take(0, true, 0x35, 0x32, 0x33);
                _price = ([Emit(now, deal?.Type == 0x35 ? ItemKinds.Repaired : "vendor", id, deal?.Name ?? NameOf(context, id), $"transaction {type}", deal)], totalBefore, now);
                break;
        }
    }

    private long Paid((List<int> Indices, long TotalBefore, TimeSpan At) price) => price.TotalBefore - (_goldPerson + _goldStash);

    /// <summary>
    /// What a purchase or repair cost: the gold on the person and in the stash before its answer less what the gold
    /// attributes after it left. Purchases answered in one instant with no gold moving between them were paid by one
    /// debit, shared out by what the price table says each costs, or evenly without it.
    /// </summary>
    private void SettlePrice()
    {
        if (_price is not { } price)
        {
            return;
        }

        _price = null;
        var paid = Paid(price);
        var count = price.Indices.Count;
        var shares = Shares(price.Indices, paid);
        for (var i = 0; i < count; i++)
        {
            var e = _events[price.Indices[i]];
            var share = shares?[i] ?? paid / count + (i == 0 ? paid % count : 0);
            var text = paid <= 0 ? e.Kind == ItemKinds.Repaired ? "no gold paid" : "price unknown: the gold the client knew before it is stale"
                : count == 1 ? $"-{paid:N0} gold" : $"-{share:N0} gold, one debit of {paid:N0} for {count} answered together, split {(shares == null ? "evenly" : "by their prices")}";
            _events[price.Indices[i]] = e with { Detail = e.Detail.Length == 0 ? text : $"{e.Detail}, {text}", Gold = paid > 0 ? -share : null };
        }

        foreach (var index in price.Indices)
        {
            _boughtItems.Remove(_events[index].Item);
        }
    }

    /// <summary>One debit for several purchases split in proportion to what each item costs by the price table; null when any cost is unknown.</summary>
    private long[] Shares(List<int> indices, long paid)
    {
        if (indices.Count < 2 || paid <= 0 || Prices() is not { } table)
        {
            return null;
        }

        var costs = new long[indices.Count];
        for (var i = 0; i < costs.Length; i++)
        {
            if (!_boughtItems.TryGetValue(_events[indices[i]].Item, out var item) || ItemPrice.BuyCost(item, table) is not { } cost)
            {
                return null;
            }

            costs[i] = cost;
        }

        var total = costs.Sum();
        var shares = costs.Select(c => paid * c / total).ToArray();
        shares[0] += paid - shares.Sum();
        return shares;
    }

    private static readonly Dictionary<string, ItemPriceTable> PriceTables = [];

    private static ItemPriceTable Prices()
    {
        if (!GameDataLocation.TryResolve(GameDataLocation.ItemPricesFile, out var path))
        {
            return null;
        }

        lock (PriceTables)
        {
            if (!PriceTables.TryGetValue(path, out var table))
            {
                try
                {
                    table = ItemPriceTable.Load(path);
                }
                catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    table = null;
                }

                PriceTables[path] = table;
            }

            return table;
        }
    }

    private void FlushTransmute()
    {
        if (_transmute is not { } t)
        {
            return;
        }

        _transmute = null;
        var request = new Pending { At = t.At, What = t.What, Repeats = t.Repeats };
        if (t.Inputs.Count == 0 && t.Outputs.Count == 0)
        {
            _events.Add(new ItemEvent(t.At, _client, ItemKinds.Unanswered, 0, "cube", $"{t.What}{(t.Repeats > 0 ? $", sent {t.Repeats + 1} times" : "")}: nothing in the cube changed")
            { Request = request.What, Repeats = t.Repeats });
            return;
        }

        var index = Emit(t.Last, ItemKinds.Transmuted, 0, "cube", $"{(t.Inputs.Count == 0 ? "nothing" : string.Join(", ", t.Inputs))} -> {(t.Outputs.Count == 0 ? "nothing" : string.Join(", ", t.Outputs))}", request);
        _events[index] = _events[index] with { Unasked = false };
    }

    /// <summary>Where the client held an item before a packet changed it, or null when it held it nowhere.</summary>
    private static string Holder(ReplayContext context, uint id)
    {
        var game = context.Game;
        if (game.CursorItem?.Id == id)
        {
            return "the cursor";
        }

        foreach (var container in new[] { game.Inventory, game.Cube, game.Stash, game.Belt })
        {
            if (container.FindItemById(id) is { } held)
            {
                return ItemView.Where(held);
            }
        }

        foreach (var (slot, worn) in game.Me.Equipment)
        {
            if (worn.Id == id)
            {
                return slot.ToString();
            }
        }

        return null;
    }

    private void Removal(ReplayContext context, uint id, TimeSpan now)
    {
        if (Known(context, id) is { IsGold: true } pile && Holder(context, id) == null)
        {
            _gone.RemoveAll(g => now - g.At > SameDeal);
            _gone.Add((id, pile.Amount, now, false));
        }

        if (_open.FirstOrDefault(o => o.Type == 0x16 && o.Item == id) is { } pick)
        {
            pick.LeftGround = now;
            return;
        }

        if (_consumed.ContainsKey(id))
        {
            return;
        }

        var holder = Holder(context, id);
        if (holder == null)
        {
            return;
        }

        if (UsedUp(id, now, holder))
        {
            return;
        }

        if (_transmute != null && context.Game.Cube.FindItemById(id) != null)
        {
            _transmute.Inputs.Add(NameOf(context, id));
            _transmute.Last = now;
            return;
        }

        Emit(now, ItemKinds.Removed, id, NameOf(context, id), $"from {holder} (0x0A); no request of the client's asked for it");
    }

    private void ItemPacket(ReplayContext context, byte[] p, TimeSpan now)
    {
        var action = (Action)p[1];
        if (action is Action.AddToGround or Action.OnGround or Action.AddToShop or Action.RemoveFromShop)
        {
            return;
        }

        var id = PacketDecoder.U32(p, 4);
        var self = context.SelfId;
        var mercenary = false;
        if (p[0] == 0x9D)
        {
            if (p.Length < 13)
            {
                return;
            }

            var ownerType = (EntityType)p[8];
            var owner = PacketDecoder.U32(p, 9);
            if (ownerType == EntityType.NPC && owner != 0 && owner == context.Game.Me.MercId)
            {
                mercenary = true;
            }
            else if (ownerType != EntityType.Player || owner != self)
            {
                return;
            }
        }

        Item item;
        try
        {
            item = new ParseItemPacket(new D2gsPacket(p)).Item;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return;
        }

        var wasIdentified = _identified.TryGetValue(id, out var known) ? known : Known(context, id)?.IsIdentified;
        _identified[id] = item.IsIdentified;
        if (Loading(now, id, action, item, mercenary))
        {
            return;
        }

        var name = ItemView.Short(item);
        var where = ItemView.Where(item);
        var heldNowhere = Holder(context, id) == null;
        Pending request;
        switch (action)
        {
            case Action.PutInContainer or Action.PutInBelt:
                request = heldNowhere ? Take(id, false, 0x18, 0x2A, 0x23, 0x63, 0x16, 0x21) : Take(id, false, 0x18, 0x2A, 0x23, 0x63, 0x21);
                var leftFrom = _leftFor.Remove(id, out var left) ? left : null;
                if (_leaving.Remove(id, out var leaving))
                {
                    leftFrom = leaving.Where;
                }

                if (request?.Type == 0x16)
                {
                    Emit(now, ItemKinds.PickedUp, id, name, $"into {where}", request, item);
                }
                else if (request != null)
                {
                    Emit(now, ItemKinds.Put, id, name, $"in {where}{(leftFrom == null ? "" : $", from {leftFrom}")}", request, item);
                }
                else if (leftFrom != null)
                {
                    Emit(now, ItemKinds.Moved, id, name, $"from {leftFrom} to {where}", null, item);
                }
                else if (_bought.Remove(id, out var index))
                {
                    _boughtItems[id] = item;
                    var e = _events[index];
                    _events[index] = e with { Name = name, Detail = e.Detail.Length == 0 ? $"into {where}" : $"{e.Detail}, into {where}", Details = DetailLine(item) };
                }
                else if (_transmute != null && item.Container == ContainerType.Cube && Holder(context, id) == null)
                {
                    _transmute.Outputs.Add(name);
                    _transmute.Last = now;
                }
                else if (item.Container == ContainerType.TradeOffer)
                {
                    _lastOffer = now;
                    Emit(now, ItemKinds.Offered, id, name, $"in {where}", null, item);
                }
                else
                {
                    var holder = Holder(context, id);
                    var traded = _lastOffer is { } offer && now - offer <= TradeEnd;
                    Emit(now, holder == null ? ItemKinds.Received : ItemKinds.Moved, id, name,
                        holder != null ? $"from {holder} to {where}" : traded ? $"into {where}: what the trade brought, under a new id" : $"into {where}, an item the client did not hold", null, item);
                }

                break;
            case Action.RemoveFromContainer or Action.RemoveFromBelt:
                if (_consumed.ContainsKey(id) || IsOpen(id, 0x33))
                {
                    break;
                }

                if (!IsOpen(id, 0x19, 0x24) && UsedUp(id, now, where))
                {
                    break;
                }

                request = Take(id, false, 0x26, 0x20);
                if (request != null)
                {
                    Emit(now, ItemKinds.Used, id, name, $"from {where}", request);
                    _consumed[id] = now;
                    break;
                }

                if (IsOpen(id, 0x23, 0x63, 0x2A))
                {
                    _leftFor[id] = where;
                    break;
                }

                request = Take(id, false, 0x19, 0x24);
                if (request != null)
                {
                    Emit(now, ItemKinds.ToCursor, id, name, $"from {where}", request);
                }
                else if (item.Container == ContainerType.ForTrade)
                {
                    Emit(now, ItemKinds.TradedAway, id, name, "off the own trade page as the trade went through", _tradeAccepted is { } accepted && now - accepted <= TradeEnd ? new Pending { At = accepted, What = "accept trade (0x4F)" } : null);
                }
                else if (item.Container == ContainerType.TradeOffer)
                {
                    _lastOffer = now;
                }
                else if (_transmute != null && item.Container == ContainerType.Cube)
                {
                    _transmute.Inputs.Add(name);
                    _transmute.Last = now;
                }
                else
                {
                    _leaving[id] = new Leaving(now, name, where);
                }

                break;
            case Action.ToCursor or Action.GroundToCursor:
                request = heldNowhere ? Take(id, false, 0x19, 0x24, 0x16) : Take(id, false, 0x19, 0x24);
                Emit(now, request?.Type == 0x16 || action == Action.GroundToCursor ? ItemKinds.PickedUp : ItemKinds.ToCursor, id, name, "to the cursor", request, item);
                break;
            case Action.DropToGround:
                request = Take(id, false, 0x17);
                if (request != null || context.Game.CursorItem?.Id == id)
                {
                    Emit(now, ItemKinds.Dropped, id, name, $"at {Names.Point(item.Location)}", request);
                }

                break;
            case Action.Equip or Action.SwapBodyItem or Action.IndirectlySwapBodyItem when mercenary:
                Emit(now, ItemKinds.Mercenary, id, name, $"equipped on the mercenary in {item.Directory}", Take(0, true, 0x61), item);
                break;
            case Action.Equip or Action.SwapBodyItem or Action.IndirectlySwapBodyItem when _bought.Remove(id, out var boughtIndex):
                _boughtItems[id] = item;
                var purchase = _events[boughtIndex];
                _events[boughtIndex] = purchase with { Name = name, Detail = purchase.Detail.Length == 0 ? $"worn at once in {item.Directory}" : $"{purchase.Detail}, worn at once in {item.Directory}", Details = DetailLine(item) };
                break;
            case Action.Equip or Action.SwapBodyItem or Action.IndirectlySwapBodyItem:
                request = heldNowhere ? Take(id, false, 0x1A, 0x1D, 0x16) : Take(id, false, 0x1A, 0x1D);
                Emit(now, request?.Type == 0x16 ? ItemKinds.PickedUp : ItemKinds.Equipped, id, name,
                    $"{(request?.Type == 0x16 ? "worn at once in" : "in")} {item.Directory}{(action == Action.SwapBodyItem ? ", what it replaced to the cursor" : "")}", request, item);
                break;
            case Action.Unequip when mercenary:
                Emit(now, ItemKinds.Mercenary, id, name, $"taken off the mercenary's {item.Directory}", Take(0, true, 0x61), item);
                break;
            case Action.Unequip:
                request = Take(id, false, 0x1C) ?? TakeSlot(item.Directory);
                var unequipped = Emit(now, ItemKinds.Unequipped, id, name, request == null ? $"from {item.Directory}, taken off by the server (on a death, to the corpse)" : $"from {item.Directory} to the cursor", request);
                _unequipped = request == null ? (unequipped, item.Directory, now) : null;
                break;
            case Action.AutoUnequip:
                Emit(now, ItemKinds.Unequipped, id, name, $"from {item.Directory}, by the server");
                break;
            case Action.RefillFromInventory:
                if (_unequipped is { } emptied && emptied.Slot == item.Directory && now - emptied.At <= SameInstant)
                {
                    var e = _events[emptied.Index];
                    _events[emptied.Index] = e with { Kind = ItemKinds.Used, Detail = $"from {item.Directory}: used up, the server took it off to refill the slot" };
                    _consumed[e.Item] = now;
                }

                _unequipped = null;
                var refill = _leaving.Remove(id, out var refilled) ? refilled.Where : Holder(context, id);
                Emit(now, ItemKinds.Moved, id, name, $"{(refill == null ? "" : $"from {refill} ")}to {item.Directory}: the server refilled the slot", null, item);
                break;
            case Action.SwapInContainer:
                request = Take(id, false, 0x1F);
                var displaced = Displaced(context, item);
                Emit(now, ItemKinds.Swapped, id, name, $"into {where}{(displaced == null ? "" : $"; {displaced} to the cursor")}", request, item);
                break;
            case Action.SwapInBelt:
                Emit(now, ItemKinds.Moved, id, name, $"swapped into {where}", Take(id, false, 0x25), item);
                break;
            case Action.ItemInSocket:
                Emit(now, ItemKinds.Socketed, id, name, $"into {NameOf(context, item.PlayerId)}", Take(id, false, 0x28), item);
                break;
            case Action.AddQuantity:
                Emit(now, ItemKinds.Stacked, id, name, item.Amount > 0 ? $"now {item.Amount}" : "", Take(id, false, 0x21));
                break;
            case Action.WeaponSwitch:
                if (Take(0, true, 0x60) is { } switched)
                {
                    Emit(now, ItemKinds.WeaponSwitch, id, name, $"{item.Directory}", switched);
                }

                break;
            case Action.UpdateStats:
                request = Take(id, false, 0x27, 0x37);
                if (request == null && Take(0, true, 0x34) is { } identifyAll)
                {
                    _identifyAll = (identifyAll, now);
                }

                if (request == null && _identifyAll is { } all && now - all.At <= SameInstant)
                {
                    request = all.Request;
                }

                if (wasIdentified == false && item.IsIdentified)
                {
                    Emit(now, ItemKinds.Identified, id, name, Stats(item), request, item);
                }
                else if (request != null)
                {
                    Emit(now, ItemKinds.Identified, id, name, "no change: it was identified already", request);
                }

                break;
        }
    }

    private Pending TakeSlot(DirectoryType slot)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            if (_open[i].Type == 0x1C && _open[i].Extra == (int)slot)
            {
                var found = _open[i];
                _open.RemoveAt(i);
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The items a character joins with arrive unasked in the first moments of a file: counted, not listed. The window
    /// closes two seconds after the first own item packet, or at the client's first item request.
    /// </summary>
    private bool Loading(TimeSpan now, uint id, Action action, Item item, bool mercenary)
    {
        if (_loadStart == null)
        {
            _loadStart = now;
            _loadUntil = now + LoadWindow;
        }

        if (_requested || now > _loadUntil || action is not (Action.PutInContainer or Action.PutInBelt or Action.Equip or Action.ItemInSocket))
        {
            return false;
        }

        var where = mercenary ? "mercenary" : action switch
        {
            Action.Equip => "equipped",
            Action.ItemInSocket => "socketed",
            _ => item.Container switch
            {
                ContainerType.Stash or ContainerType.Stash2 => "stash",
                ContainerType.Belt => "belt",
                ContainerType.Cube => "cube",
                ContainerType.Inventory => "inventory",
                var other => other.ToString(),
            },
        };
        _loadedCounts[where] = _loadedCounts.GetValueOrDefault(where) + 1;
        return true;
    }

    private static string Displaced(ReplayContext context, Item placed)
    {
        Core.D2GS.Items.Containers.Container container = placed.Container switch
        {
            ContainerType.Inventory => context.Game.Inventory,
            ContainerType.Cube => context.Game.Cube,
            ContainerType.Stash or ContainerType.Stash2 => context.Game.Stash,
            _ => null,
        };
        var cell = ItemView.Cell(placed);
        var other = container?.Items.FirstOrDefault(i => i.Id != placed.Id && ItemView.Cell(i) is { } at && cell != null
            && at.X < cell.X + Math.Max(1, (int)placed.Width) && cell.X < at.X + Math.Max(1, (int)i.Width)
            && at.Y < cell.Y + Math.Max(1, (int)placed.Height) && cell.Y < at.Y + Math.Max(1, (int)i.Height));
        return other == null ? null : ItemView.Short(other);
    }

    private static string Stats(Item item)
    {
        var stats = ItemView.Stats(item);
        return stats.Count == 0 ? $"{item.Quality}" : $"{item.Quality}: {string.Join("; ", stats)}";
    }

    /// <summary>One line of what the packet decoded beyond name and quality; null when it carried nothing more.</summary>
    public static string DetailLine(Item item)
    {
        var parts = new List<string>();
        if (item.Level > 0)
        {
            parts.Add($"level {item.Level}");
        }

        if (item.Ethereal)
        {
            parts.Add("ethereal");
        }

        if (!item.IsIdentified && item.Quality > QualityType.Superior)
        {
            parts.Add("unidentified");
        }

        if (item.Sockets > 0)
        {
            parts.Add(item.SocketedItems.IsEmpty ? $"{item.Sockets} sockets" : $"{item.Sockets} sockets ({string.Join(", ", item.SocketedItems.Values.OrderBy(s => s.Id).Select(s => s.Name))})");
        }

        if (item.MaximumDurability > 0)
        {
            parts.Add($"durability {item.Durability}/{item.MaximumDurability}");
        }

        if (item.Amount > 0 && !item.IsGold)
        {
            parts.Add($"amount {item.Amount}");
        }

        var stats = ItemView.Stats(item);
        if (stats.Count > 0)
        {
            parts.Add(string.Join("; ", stats));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public void Save(BinaryWriter w) => w.WriteList(_events, (x, e) =>
    {
        x.Write(e.At);
        x.WriteText(e.Client);
        x.WriteText(e.Kind);
        x.Write(e.Item);
        x.WriteText(e.Name);
        x.WriteText(e.Detail);
        x.Write(e.Asked);
        x.WriteText(e.Request);
        x.Write(e.Repeats);
        x.Write(e.Unasked);
        x.WriteText(e.Details);
        x.Write(e.Gold.HasValue);
        x.Write(e.Gold ?? 0);
        x.Write(e.Pile.HasValue);
        x.Write(e.Pile ?? 0);
    });

    public void Load(BinaryReader r) => r.ReadList(_events, x =>
    {
        var (at, client, kind, item, name, detail) = (x.ReadTime(), x.ReadText(), x.ReadText(), x.ReadUInt32(), x.ReadText(), x.ReadText());
        var (asked, request, repeats, unasked, details) = (x.ReadNullableTime(), x.ReadText(), x.ReadInt32(), x.ReadBoolean(), x.ReadText());
        var hasGold = x.ReadBoolean();
        var gold = x.ReadInt64();
        var hasPile = x.ReadBoolean();
        var pile = x.ReadInt64();
        return new ItemEvent(at, client, kind, item, name, detail) { Asked = asked, Request = request, Repeats = repeats, Unasked = unasked, Details = details, Gold = hasGold ? gold : null, Pile = hasPile ? pile : null };
    });
}

/// <summary>The item moves of a game's clients: one replay per client, then filters, a summary and the lines.</summary>
public static class ItemMoves
{
    /// <summary>
    /// What moved for one client. <see cref="GoldPicked"/> and <see cref="GoldUnasked"/> are what the gold attribute says
    /// reached the person outside vendor answers, for its own piles and otherwise (a party member's pile, a corpse);
    /// <see cref="PileGold"/> is what its piles held on the ground. <see cref="GoldDropped"/> is what the client dropped
    /// to the ground and <see cref="GoldLost"/> what left the person otherwise, on a death or unexplained.
    /// </summary>
    public sealed record Summary(string Client, int Picked, long GoldPicked, int GoldPiles, long PileGold, long GoldUnasked, int Sold, long GoldIn, int Bought, int Gambled, int Repaired, long GoldOut,
        int Stashed, int Cubed, int Transmuted, int Used, int Activated, int Dropped, int Equipped, int Identified, int TradedAway, int Received, int Removed, int Unanswered,
        long GoldDropped = 0, long GoldLost = 0);

    /// <summary>One replay per client feeds its ledger and the item state check, both cached together.</summary>
    public static List<(string Client, ItemLedger Ledger, ItemStateCheck Check)> Replay(GameQuery query, IEnumerable<string> clients)
        => [.. query.ReplayAll(clients, TimeSpan.MaxValue, () => (Ledger: new ItemLedger(), Check: new ItemStateCheck()), o => [o.Ledger, o.Check])
            .Select(r => (r.Client, r.Observers.Ledger, r.Observers.Check))];

    /// <summary>The check's findings as events, so they read in place among the moves.</summary>
    public static IEnumerable<ItemEvent> Findings(string client, ItemStateCheck check)
        => check.Found.Select(i => new ItemEvent(i.Offset, client, i.Kind.ToUpperInvariant(), 0, "-", i.Detail) { Unasked = true });

    /// <summary>Whether an event is about the item asked for: an id (0x1C9 or 1C9) or part of the name, case ignored.</summary>
    public static bool Matches(ItemEvent e, string item)
    {
        if (item == null)
        {
            return true;
        }

        var hex = item.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? item[2..] : null;
        if (hex != null && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            return e.Item == id || System.Text.RegularExpressions.Regex.IsMatch(e.Detail, $@"{Names.Id(id)}(?![0-9A-Fa-f])");
        }

        return (e.Name ?? "").Contains(item, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(item, StringComparison.OrdinalIgnoreCase);
    }

    public static Summary Summarize(string client, IEnumerable<ItemEvent> events)
    {
        var list = events.ToList();
        int Count(string kind) => list.Count(e => e.Kind == kind);
        var piles = list.Where(e => e.Kind == ItemKinds.PickedUp && e.Name == "Gold").ToList();
        var received = list.Where(e => e.Kind == ItemKinds.GoldReceived).ToList();
        var puts = list.Where(e => e.Kind == ItemKinds.Put || (e.Kind == ItemKinds.PickedUp && e.Name != "Gold")).ToList();
        return new Summary(
            client,
            list.Count(e => e.Kind == ItemKinds.PickedUp && e.Name != "Gold"),
            received.Where(e => !e.Unasked).Sum(e => e.Gold ?? 0),
            piles.Count,
            piles.Sum(e => e.Pile ?? 0),
            received.Where(e => e.Unasked).Sum(e => e.Gold ?? 0),
            Count(ItemKinds.Sold),
            list.Where(e => e.Kind == ItemKinds.Sold).Sum(e => e.Gold ?? 0),
            Count(ItemKinds.Bought),
            Count(ItemKinds.Gambled),
            Count(ItemKinds.Repaired),
            -list.Where(e => e.Kind is ItemKinds.Bought or ItemKinds.Gambled or ItemKinds.Repaired).Sum(e => e.Gold ?? 0),
            puts.Count(e => e.Detail.StartsWith("in stash", StringComparison.Ordinal) || e.Detail.StartsWith("into stash", StringComparison.Ordinal)),
            puts.Count(e => e.Detail.StartsWith("in cube", StringComparison.Ordinal) || e.Detail.StartsWith("into cube", StringComparison.Ordinal)),
            Count(ItemKinds.Transmuted),
            Count(ItemKinds.Used),
            Count(ItemKinds.Activated),
            Count(ItemKinds.Dropped),
            Count(ItemKinds.Equipped),
            Count(ItemKinds.Identified),
            Count(ItemKinds.TradedAway),
            Count(ItemKinds.Received),
            Count(ItemKinds.Removed),
            Count(ItemKinds.Unanswered),
            -list.Where(e => e.Kind == ItemKinds.Dropped).Sum(e => e.Gold ?? 0),
            -list.Where(e => e.Kind == ItemKinds.GoldLost).Sum(e => e.Gold ?? 0));
    }

    public static string Format(Summary s)
        => $"{s.Client}: picked {s.Picked} items and {s.GoldPiles} gold piles (+{s.GoldPicked:N0} gold{(s.PileGold == s.GoldPicked ? "" : $", though the piles held {s.PileGold:N0}")})"
            + $"{(s.GoldUnasked == 0 ? "" : $", got +{s.GoldUnasked:N0} gold from piles it did not pick up")}, sold {s.Sold} (+{s.GoldIn:N0} gold), "
            + $"bought {s.Bought}, gambled {s.Gambled} and repaired {s.Repaired} times (-{s.GoldOut:N0} gold), "
            + $"stashed {s.Stashed}, cubed {s.Cubed}, transmuted {s.Transmuted}, used {s.Used}, activated {s.Activated}, dropped {s.Dropped}, equipped {s.Equipped}, identified {s.Identified}, "
            + $"traded away {s.TradedAway}, received {s.Received}, removed by the server {s.Removed}, unanswered requests {s.Unanswered}"
            + (s.GoldDropped == 0 ? "" : $", dropped {s.GoldDropped:N0} gold") + (s.GoldLost == 0 ? "" : $", lost {s.GoldLost:N0} gold (deaths or unexplained)");

    public static Summary Total(IReadOnlyList<Summary> all) => new(
        "game", all.Sum(s => s.Picked), all.Sum(s => s.GoldPicked), all.Sum(s => s.GoldPiles), all.Sum(s => s.PileGold), all.Sum(s => s.GoldUnasked), all.Sum(s => s.Sold), all.Sum(s => s.GoldIn),
        all.Sum(s => s.Bought), all.Sum(s => s.Gambled), all.Sum(s => s.Repaired), all.Sum(s => s.GoldOut), all.Sum(s => s.Stashed), all.Sum(s => s.Cubed), all.Sum(s => s.Transmuted),
        all.Sum(s => s.Used), all.Sum(s => s.Activated), all.Sum(s => s.Dropped), all.Sum(s => s.Equipped), all.Sum(s => s.Identified), all.Sum(s => s.TradedAway), all.Sum(s => s.Received),
        all.Sum(s => s.Removed), all.Sum(s => s.Unanswered), all.Sum(s => s.GoldDropped), all.Sum(s => s.GoldLost));

    /// <summary>"asked 00:50.946 (to cube (0x2A)), answered in 116 ms", or what the server did unasked.</summary>
    public static string Answer(ItemEvent e)
    {
        if (e.Kind is ItemKinds.Unanswered or ItemKinds.Repeat)
        {
            return "";
        }

        if (e.Asked is not { } asked)
        {
            return e.Kind is ItemKinds.Loaded ? "" : "unasked";
        }

        var repeats = e.Repeats > 0 ? $", sent {e.Repeats + 1} times" : "";
        return $"asked {TraceTime.Format(asked)} ({e.Request}{repeats}), answered in {(e.At - asked).TotalMilliseconds:0} ms";
    }

    public static string Format(ItemEvent e, bool details)
    {
        var answer = Answer(e);
        var line = $"{TraceTime.Format(e.At)} {e.Client,-12} {e.Kind,-16} {e.Name}{(e.Detail.Length == 0 ? "" : " " + e.Detail)}{(answer.Length == 0 ? "" : $" - {answer}")}";
        return details && e.Details != null ? $"{line}\n    {e.Details}" : line;
    }
}
