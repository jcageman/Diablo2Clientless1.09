using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>A request the client sent that the server answers, and when the answer came (null: none within <see cref="RequestObserver.AnswerTimeout"/>).</summary>
public sealed record ClientRequest(string Kind, TimeSpan At, TimeSpan? Answered)
{
    public TimeSpan? Latency => Answered - At;

    /// <summary>Until when the client waited on it: the answer, or the timeout for one that never came.</summary>
    public TimeSpan WaitedUntil => Answered ?? At + RequestObserver.AnswerTimeout;
}

/// <summary>A packet the client sent that is an action of the bot (not a ping or a location update), described.</summary>
public sealed record SentAction(TimeSpan At, byte Type, string Text);

/// <summary>
/// Pairs what a client asked of the server with the server's answer, by the packets seen to answer each in recorded
/// games: an NPC interact (0x13) with its NPC info (0x27), an object interact with the object's state (0x0E), the
/// client's stop or reassign (0x0D, 0x15), an act load (0x03) or the waypoint menu (0x63); a waypoint (0x49) with the
/// act load or reassign; a trade or gamble window (0x38) with the first shop item (0x9C); a buy, sell or repair with
/// the vendor's answer (0x2A); a pick up with the item leaving the ground (0x0A, as gold does); a pick up, lift, put down, drink or identify with an item packet of the same item; a
/// Teleport cast and an entity update request (0x4B) with the client's reassign; a town portal cast with the client's
/// own new portal (0x82 naming the client as owner of a portal object first seen after the cast); a skill select (0x3C)
/// with 0x23. A waypoint packet to level 0 only closes the menu and asks nothing. An answer settles the oldest open
/// request of its kind. A request sent while one of its kind is open for the same unit - or for any, for Teleport, town
/// portal casts and entity updates, which the bot sends again and again until the answer - is a repeat of that one, not a request of its
/// own, so its latency runs from the first of the repeats.
/// Also keeps every action the client sent, its attack and loot casts, and its arrivals (act loads, answered warps).
/// </summary>
public sealed class RequestObserver : ICachedObserver
{
    public string CacheKey => "RequestObserver";

    /// <summary>What a whole replay leaves: the requests, actions, attack and loot casts and arrivals; what is open at a file's end is cleared then.</summary>
    public void Save(System.IO.BinaryWriter w)
    {
        w.WriteList(_requests, (x, r) => { x.WriteText(r.Kind); x.Write(r.At); x.Write(r.Answered); });
        w.WriteList(_actions, (x, a) => { x.Write(a.At); x.Write(a.Type); x.WriteText(a.Text); });
        w.WriteTimes(_attacks);
        w.WriteTimes(_loots);
        w.WriteList(_arrivals, (x, a) => { x.Write(a.At); x.WriteText(a.What); });
    }

    public void Load(System.IO.BinaryReader r)
    {
        r.ReadList(_requests, x => new ClientRequest(x.ReadText(), x.ReadTime(), x.ReadNullableTime()));
        r.ReadList(_actions, x => new SentAction(x.ReadTime(), x.ReadByte(), x.ReadText()));
        r.ReadTimes(_attacks);
        r.ReadTimes(_loots);
        r.ReadList(_arrivals, x => (x.ReadTime(), x.ReadText()));
    }

    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    public const string NpcInteract = "npc interact -> npc info (0x27)";
    public const string ObjectInteract = "object interact -> object state, stop, reassign or act load";
    public const string Waypoint = "waypoint (0x49) -> act load or reassign";
    public const string TradeWindow = "trade or gamble window (0x38) -> shop list (0x9C)";
    public const string VendorDeal = "buy, sell or repair -> vendor answer (0x2A)";
    public const string PickUp = "pick up (0x16) -> item packet or removal (0x0A)";
    public const string Lift = "lift to cursor (0x19) -> item packet";
    public const string PutDown = "put in container, belt or cube -> item packet";
    public const string Drink = "use belt item (0x26) -> item packet";
    public const string UseItem = "use inventory item (0x20) -> item packet";
    public const string Identify = "identify (0x27) -> item packet";
    public const string Teleport = "Teleport cast -> reassign (0x15)";
    public const string TownPortal = "town portal cast -> own new portal (0x51, 0x82)";
    public const string EntityUpdate = "entity update (0x4B) -> reassign (0x15)";
    public const string SelectSkill = "select skill (0x3C) -> set skill (0x23)";

    private readonly List<(string Kind, uint Key, int Index)> _open = [];
    private readonly List<ClientRequest> _requests = [];
    private readonly List<SentAction> _actions = [];
    private readonly List<TimeSpan> _attacks = [];
    private readonly List<TimeSpan> _loots = [];
    private readonly List<(TimeSpan At, string What)> _arrivals = [];
    private readonly HashSet<uint> _portalsSeen = [];
    private readonly HashSet<uint> _portalsNew = [];

    public IReadOnlyList<ClientRequest> Requests => _requests;

    public IReadOnlyList<SentAction> Actions => _actions;

    /// <summary>Attack casts: what <see cref="ActivityObserver.IsAttack"/> calls fighting, less the loot skills.</summary>
    public IReadOnlyList<TimeSpan> Attacks => _attacks;

    /// <summary>Pick ups sent and loot skill casts (Find Item).</summary>
    public IReadOnlyList<TimeSpan> Loots => _loots;

    /// <summary>Act loads, and answered waypoints and object interacts that moved the client.</summary>
    public IReadOnlyList<(TimeSpan At, string What)> Arrivals => _arrivals;

    public static bool IsLootSkill(Skill skill) => skill is Skill.FindItem or Skill.FindPotion;

    /// <summary>Request kinds the bot sends periodically while it waits for something else: an answer to one is not what it waits for.</summary>
    public static bool IsPoll(string kind) => kind == EntityUpdate;

    private static bool RepeatsUntilAnswered(string kind) => kind is Teleport or EntityUpdate or TownPortal;

    public void After(ReplayContext context, TraceRecord record)
    {
        var now = context.Now;
        Expire(now);
        var p = record.Payload;
        if (p.Length == 0)
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

    public void Finished(ReplayContext context)
    {
        _open.Clear();
        _portalsSeen.Clear();
        _portalsNew.Clear();
    }

    private void Expire(TimeSpan now) => _open.RemoveAll(o => now - _requests[o.Index].At > AnswerTimeout);

    private void Open(string kind, uint key, TimeSpan at)
    {
        foreach (var open in _open)
        {
            if (open.Kind == kind && (open.Key == key && (key != 0 || RepeatsUntilAnswered(kind))))
            {
                return;
            }
        }

        _open.Add((kind, key, _requests.Count));
        _requests.Add(new ClientRequest(kind, at, null));
    }

    private void Answer(string kind, uint key, TimeSpan now)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            if (_open[i].Kind == kind && (_open[i].Key == 0 || key == 0 || _open[i].Key == key))
            {
                var index = _open[i].Index;
                _requests[index] = _requests[index] with { Answered = now };
                _open.RemoveAt(i);
                if (kind is Waypoint or ObjectInteract)
                {
                    _arrivals.Add((now, kind == Waypoint ? "waypoint" : "object interact"));
                }

                return;
            }
        }
    }

    private bool Waiting(string kind)
    {
        foreach (var open in _open)
        {
            if (open.Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    private void Sent(ReplayContext context, byte[] p, TimeSpan now)
    {
        var type = p[0];
        if (type is 0x6A or 0x5F)
        {
            return;
        }

        var text = Names.Outgoing(type);
        switch (type)
        {
            case 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0A or 0x0C or 0x0D or 0x0E or 0x0F or 0x10 or 0x11:
                var hand = type <= 0x0A ? Hand.Left : Hand.Right;
                var skill = Skill.Attack;
                var known = context.Game.Me != null && context.Game.Me.ActiveSkills.TryGetValue(hand, out skill);
                text = known ? $"cast {skill}" : "cast (skill unknown)";
                if (known && skill == Skill.Teleport)
                {
                    Open(Teleport, 0, now);
                }
                else if (known && skill is Skill.BookOfTownportal or Skill.ScrollOfTownportal)
                {
                    Open(TownPortal, 0, now);
                }
                else if (known && IsLootSkill(skill))
                {
                    _loots.Add(now);
                }
                else if (!known || ActivityObserver.IsAttack(skill))
                {
                    _attacks.Add(now);
                }

                break;
            case 0x13 when p.Length >= 9:
                var entity = PacketDecoder.U32(p, 1);
                if (entity == (uint)EntityType.NPC)
                {
                    Open(NpcInteract, 0, now);
                    text = "interact npc";
                }
                else if (entity is (uint)EntityType.Object or (uint)EntityType.Doorway)
                {
                    Open(ObjectInteract, PacketDecoder.U32(p, 5), now);
                    text = "interact object";
                }

                break;
            case 0x49 when p.Length >= 9 && PacketDecoder.U32(p, 5) == 0:
                text = "close waypoint menu";
                break;
            case 0x49:
                Open(Waypoint, 0, now);
                break;
            case 0x38 when p.Length >= 5 && PacketDecoder.U32(p, 1) is 1 or 2:
                Open(TradeWindow, 0, now);
                break;
            case 0x32 or 0x33 or 0x35:
                Open(VendorDeal, 0, now);
                break;
            case 0x16 when p.Length >= 9:
                Open(PickUp, PacketDecoder.U32(p, 5), now);
                _loots.Add(now);
                break;
            case 0x19 when p.Length >= 5:
                Open(Lift, PacketDecoder.U32(p, 1), now);
                break;
            case 0x18 or 0x23 or 0x2A when p.Length >= 5:
                Open(PutDown, PacketDecoder.U32(p, 1), now);
                break;
            case 0x26 when p.Length >= 5:
                Open(Drink, PacketDecoder.U32(p, 1), now);
                break;
            case 0x20 when p.Length >= 5:
                Open(UseItem, PacketDecoder.U32(p, 1), now);
                break;
            case 0x27 when p.Length >= 5:
                Open(Identify, PacketDecoder.U32(p, 1), now);
                break;
            case 0x4B:
                Open(EntityUpdate, 0, now);
                break;
            case 0x3C:
                Open(SelectSkill, 0, now);
                break;
        }

        _actions.Add(new SentAction(now, type, text));
    }

    private void Received(ReplayContext context, byte[] p, TimeSpan now)
    {
        if (p[0] == 0x51 && p.Length >= 8 && PacketDecoder.U16(p, 6) == (ushort)EntityCode.TownPortal && _portalsSeen.Add(PacketDecoder.U32(p, 2)) && Waiting(TownPortal))
        {
            _portalsNew.Add(PacketDecoder.U32(p, 2));
        }

        if (_open.Count == 0 && p[0] != 0x03)
        {
            return;
        }

        switch (p[0])
        {
            case 0x27:
                Answer(NpcInteract, 0, now);
                break;
            case 0x03:
                _arrivals.Add((now, "act load"));
                Answer(Waypoint, 0, now);
                Answer(ObjectInteract, 0, now);
                break;
            case 0x15 or 0x0D when p.Length >= 6 && p[1] == (byte)EntityType.Player && context.SelfId is { } self && PacketDecoder.U32(p, 2) == self:
                Answer(ObjectInteract, 0, now);
                if (p[0] == 0x15)
                {
                    Answer(Waypoint, 0, now);
                    Answer(Teleport, 0, now);
                    Answer(EntityUpdate, 0, now);
                }

                break;
            case 0x63:
                Answer(ObjectInteract, 0, now);
                break;
            case 0x0E when p.Length >= 6:
                Answer(ObjectInteract, PacketDecoder.U32(p, 2), now);
                break;
            case 0x9C or 0x9D when p.Length >= 8:
                var id = PacketDecoder.U32(p, 4);
                if (p[0] == 0x9C && Waiting(TradeWindow))
                {
                    Answer(TradeWindow, 0, now);
                }

                foreach (var kind in ItemKinds)
                {
                    Answer(kind, id, now);
                }

                break;
            case 0x0A when p.Length >= 6 && p[1] == (byte)EntityType.Item:
                Answer(PickUp, PacketDecoder.U32(p, 2), now);
                break;
            case 0x3F when p.Length >= 6:
                Answer(UseItem, PacketDecoder.U32(p, 2), now);
                break;
            case 0x2A:
                Answer(VendorDeal, 0, now);
                break;
            case 0x82 when p.Length >= 25 && context.SelfId is { } owner && PacketDecoder.U32(p, 1) == owner && _portalsNew.Remove(PacketDecoder.U32(p, 21)):
                Answer(TownPortal, 0, now);
                break;
            case 0x23:
                Answer(SelectSkill, 0, now);
                break;
        }
    }

    private static readonly string[] ItemKinds = [PickUp, Lift, PutDown, Drink, UseItem, Identify];
}

/// <summary>A stretch of standing still, what came around it, and where its time went.</summary>
public sealed record StandStill(TimeSpan Start, TimeSpan End, string State, Point At, string Area)
{
    public TimeSpan Duration => End - Start;

    /// <summary>The last action the client sent before it, and how long before.</summary>
    public SentAction Before { get; init; }

    /// <summary>The first action the client sent after it began.</summary>
    public SentAction After { get; init; }

    /// <summary>Time a request of the client was open: sent and not answered yet (or never, up to the timeout).</summary>
    public TimeSpan ServerWait { get; init; }

    /// <summary>Time inside a typed wait the bot recorded, with no request open.</summary>
    public TimeSpan TypedWait { get; init; }

    /// <summary>The rest: no request open, no typed wait - the bot itself did nothing.</summary>
    public TimeSpan BotWait { get; init; }

    /// <summary>The request kinds and typed wait reasons that took its time, largest first.</summary>
    public IReadOnlyList<(string What, TimeSpan Time)> Parts { get; init; } = [];

    public string Cause { get; init; }
}

/// <summary>
/// Where standing still goes. A stand-still is a standing-idle or standing-waiting stretch of the activity split, or a
/// part of a town stretch where the client did not move (town idle). Each 100 ms of it is server wait when a request of
/// the client was open (sent and unanswered, see <see cref="RequestObserver"/>), else typed wait when the bot recorded a
/// typed wait, else bot wait. The cause is the largest of the three; for bot wait it says what came just before: an
/// arrival (act load or answered warp), the bot polling (two or more entity updates sent inside it: it is waiting on
/// something), a server answer the bot had not acted on yet (never a poll's), the end of the game, or the last action sent.
/// </summary>
public static class IdleAttribution
{
    private static readonly TimeSpan ArrivalWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(0.5);

    public static List<StandStill> Of(ActivityObserver activity, IReadOnlyList<Stretch> stretches, RequestObserver requests,
        IReadOnlyList<(TimeSpan Start, TimeSpan End, string Reason)> waits, TimeSpan minimum)
    {
        var stills = new List<StandStill>();
        foreach (var stretch in stretches)
        {
            switch (stretch.State)
            {
                case ActivityState.StandingIdle or ActivityState.StandingWaiting when stretch.Duration >= minimum:
                    stills.Add(Attribute(new StandStill(stretch.Start, stretch.End, ActivityObserver.Name(stretch.State), stretch.At, stretch.Area), requests, waits));
                    break;
                case ActivityState.Town:
                    foreach (var (start, stop, at) in Timeline.StillWithin(activity, stretch.Start, stretch.End).Where(s => s.Stop - s.Start >= minimum))
                    {
                        stills.Add(Attribute(new StandStill(start, stop, "town idle", at, activity.AreaName(start)), requests, waits));
                    }

                    break;
            }
        }

        return stills;
    }

    private static StandStill Attribute(StandStill still, RequestObserver requests, IReadOnlyList<(TimeSpan Start, TimeSpan End, string Reason)> waits)
    {
        var parts = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        TimeSpan server = TimeSpan.Zero, typed = TimeSpan.Zero, bot = TimeSpan.Zero;
        var first = FirstFrom(requests.Requests, still.Start - RequestObserver.AnswerTimeout);
        for (var t = still.Start; t < still.End; t += ActivityObserver.Slice)
        {
            var slice = still.End - t < ActivityObserver.Slice ? still.End - t : ActivityObserver.Slice;
            string request = null;
            for (var i = first; i < requests.Requests.Count && requests.Requests[i].At <= t; i++)
            {
                if (requests.Requests[i].WaitedUntil > t)
                {
                    request = requests.Requests[i].Kind + (requests.Requests[i].Answered == null ? " (unanswered)" : "");
                    break;
                }
            }

            if (request != null)
            {
                server += slice;
                parts[request] = parts.GetValueOrDefault(request) + slice;
                continue;
            }

            var reason = waits.Where(w => w.Start <= t && t <= w.End).OrderBy(w => w.Start).Select(w => w.Reason).FirstOrDefault();
            if (reason != null)
            {
                typed += slice;
                var key = $"typed wait: {(reason.Length == 0 ? "no reason" : reason)}";
                parts[key] = parts.GetValueOrDefault(key) + slice;
                continue;
            }

            bot += slice;
        }

        var before = requests.Actions.LastOrDefault(a => a.At <= still.Start);
        var after = requests.Actions.FirstOrDefault(a => a.At > still.Start);
        var ordered = parts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList();
        string cause;
        if (server >= typed && server >= bot && server > TimeSpan.Zero)
        {
            cause = "server: " + ordered.First(p => !p.Key.StartsWith("typed wait", StringComparison.Ordinal)).Key;
        }
        else if (typed >= bot && typed > TimeSpan.Zero)
        {
            cause = ordered.First(p => p.Key.StartsWith("typed wait", StringComparison.Ordinal)).Key;
        }
        else
        {
            cause = "bot: " + BotCause(still, requests, before, after);
        }

        return still with { Before = before, After = after, ServerWait = server, TypedWait = typed, BotWait = bot, Parts = ordered, Cause = cause };
    }

    private static string BotCause(StandStill still, RequestObserver requests, SentAction before, SentAction after)
    {
        if (requests.Arrivals.LastOrDefault(a => a.At <= still.Start + ActivityObserver.Slice) is { What: { } what } arrival && still.Start - arrival.At <= ArrivalWindow)
        {
            return $"settling after arrival ({what})";
        }

        var polls = requests.Requests.Count(r => RequestObserver.IsPoll(r.Kind) && r.At >= still.Start && r.At < still.End);
        if (polls >= 2)
        {
            return $"waiting, polling {RequestObserver.EntityUpdate.Split(" -> ")[0]}";
        }

        if (requests.Requests.LastOrDefault(r => !RequestObserver.IsPoll(r.Kind) && r.Answered is { } answered && answered <= still.End && answered >= still.Start - AnswerWindow) is { } answered)
        {
            return $"acting on an answer late ({answered.Kind.Split(" -> ")[0]})";
        }

        if (after == null || after.Type == (byte)Core.D2GS.Packet.OutGoingPacket.LeaveGame)
        {
            return "before leaving the game";
        }

        return before == null ? "before its first action" : $"after {before.Text}";
    }

    private static int FirstFrom(IReadOnlyList<ClientRequest> requests, TimeSpan from)
    {
        int low = 0, high = requests.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (requests[middle].At < from)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>"12 stand-stills of 1.0s or more, 34.5s: server 10.1s, typed 0.0s, bot 24.4s; by cause: ..." with the causes largest first.</summary>
    public static string Summary(IReadOnlyCollection<StandStill> stills, TimeSpan minimum)
    {
        var total = stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration);
        var server = stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.ServerWait);
        var typed = stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.TypedWait);
        var bot = stills.Aggregate(TimeSpan.Zero, (a, s) => a + s.BotWait);
        var causes = stills.GroupBy(s => s.Cause).Select(g => (Cause: g.Key, Count: g.Count(), Time: g.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration)))
            .OrderByDescending(c => c.Time).ThenBy(c => c.Cause, StringComparer.Ordinal).ToList();
        var shown = causes.Take(8).Select(c => $"{c.Cause} {c.Time.TotalSeconds:0.0}s ({c.Count})");
        return $"{stills.Count} stand-still{(stills.Count == 1 ? "" : "s")} of {minimum.TotalSeconds:0.0}s or more, {total.TotalSeconds:0.0}s: server wait {server.TotalSeconds:0.0}s, typed wait {typed.TotalSeconds:0.0}s, bot wait {bot.TotalSeconds:0.0}s"
            + (causes.Count == 0 ? "" : $"; by cause: {string.Join(", ", shown)}{(causes.Count > 8 ? $", {causes.Count - 8} more" : "")}");
    }

    /// <summary>"1.4s town idle at (5043, 5018) in X: bot 1.1s, server 0.3s (...); before: Run 0.2s earlier; next: interact npc 1.3s in".</summary>
    public static string Render(StandStill s)
    {
        var split = string.Join(", ", new[] { ("server", s.ServerWait), ("typed", s.TypedWait), ("bot", s.BotWait) }.Where(p => p.Item2 > TimeSpan.Zero).Select(p => $"{p.Item1} {p.Item2.TotalSeconds:0.0}s"));
        var before = s.Before == null ? "nothing sent before" : $"before: {s.Before.Text} {(s.Start - s.Before.At).TotalSeconds:0.0}s earlier";
        var after = s.After == null ? "nothing sent after" : $"next: {s.After.Text} {(s.After.At - s.Start).TotalSeconds:0.0}s in";
        return $"{s.Duration.TotalSeconds,5:0.0}s {s.State,-16} {s.Cause}; {split}; {before}; {after}; at {Names.Point(s.At)} in {s.Area ?? "?"}";
    }
}

/// <summary>Request-to-answer latency per request kind, over any number of clients and games.</summary>
public sealed class LatencyTable
{
    private readonly Dictionary<string, (List<double> Ms, int Unanswered)> _kinds = new(StringComparer.Ordinal);

    public void Add(RequestObserver requests)
    {
        foreach (var request in requests.Requests)
        {
            var have = _kinds.TryGetValue(request.Kind, out var known) ? known : ([], 0);
            if (request.Latency is { } latency)
            {
                have.Ms.Add(latency.TotalMilliseconds);
            }
            else
            {
                have.Unanswered++;
            }

            _kinds[request.Kind] = have;
        }
    }

    public IEnumerable<(string Kind, int Answered, double Median, double P90, double Max, int Unanswered)> Rows()
        => _kinds.Select(k =>
        {
            var sorted = k.Value.Ms.Order().ToList();
            double At(double q) => sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(q * sorted.Count))];
            return (k.Key, sorted.Count, At(0.5), At(0.9), sorted.Count == 0 ? double.NaN : sorted[^1], k.Value.Unanswered);
        }).OrderByDescending(r => r.Item2 + r.Unanswered).ThenBy(r => r.Key, StringComparer.Ordinal);

    public IEnumerable<string> Lines()
    {
        yield return $"{"request -> answer",-62} {"answered",8} {"median",8} {"p90",8} {"max",8} {"unanswered",10}";
        foreach (var r in Rows())
        {
            yield return $"{r.Kind,-62} {r.Answered,8} {Ms(r.Median),8} {Ms(r.P90),8} {Ms(r.Max),8} {r.Unanswered,10}";
        }
    }

    private static string Ms(double value) => double.IsNaN(value) ? "-" : value.ToString("0", CultureInfo.InvariantCulture) + "ms";
}

/// <summary>
/// The phases of a farming client's game, decided every 100 ms: out (no file open, or not placed yet), town (a town
/// area), and outside town, per outing (a stay outside town): travel until its first attack cast, kill until its last
/// attack cast plus 1.5 s, loot until its last pick up or loot skill cast after that plus 1.5 s, and return for the rest.
/// An outing without attacks is travel throughout.
/// </summary>
public static class FarmPhases
{
    public static readonly string[] Order = ["out", "town", "travel", "kill", "loot", "return"];

    public static Dictionary<string, TimeSpan> Of(ActivityObserver activity, IReadOnlyList<Stretch> stretches, RequestObserver requests, TimeSpan from, TimeSpan to)
    {
        var totals = Order.ToDictionary(p => p, _ => TimeSpan.Zero, StringComparer.Ordinal);
        var slices = new List<(TimeSpan At, TimeSpan Length, string Base)>();
        var index = 0;
        for (var t = from; t < to; t += ActivityObserver.Slice)
        {
            var length = to - t < ActivityObserver.Slice ? to - t : ActivityObserver.Slice;
            while (index + 1 < stretches.Count && stretches[index].End <= t)
            {
                index++;
            }

            var state = index < stretches.Count ? stretches[index].State : ActivityState.Out;
            var basis = state is ActivityState.Out or ActivityState.Loading ? "out" : activity.AreaAt(t) is { } area && ActivityObserver.IsTown(area) ? "town" : "field";
            slices.Add((t, length, basis));
        }

        for (var i = 0; i < slices.Count;)
        {
            if (slices[i].Base != "field")
            {
                totals[slices[i].Base] += slices[i].Length;
                i++;
                continue;
            }

            var end = i;
            while (end < slices.Count && slices[end].Base == "field")
            {
                end++;
            }

            var outingStart = slices[i].At;
            var outingEnd = slices[end - 1].At + slices[end - 1].Length;
            var attacks = requests.Attacks.Where(a => a >= outingStart && a < outingEnd).ToList();
            TimeSpan? firstAttack = attacks.Count > 0 ? attacks[0] : null;
            TimeSpan? lastAttack = attacks.Count > 0 ? attacks[^1] : null;
            TimeSpan? lastLoot = lastAttack is { } last ? requests.Loots.Where(l => l > last && l < outingEnd).Select(l => (TimeSpan?)l).LastOrDefault() : null;
            for (var j = i; j < end; j++)
            {
                var t = slices[j].At;
                var phase = firstAttack is not { } fa || t < fa ? "travel"
                    : t <= lastAttack.Value + ActivityObserver.FightMemory ? "kill"
                    : lastLoot is { } ll && t <= ll + ActivityObserver.PickMemory ? "loot"
                    : "return";
                totals[phase] += slices[j].Length;
            }

            i = end;
        }

        return totals;
    }

    /// <summary>"town 25s, travel 8s, kill 22s, ..." in phase order, phases with time only.</summary>
    public static string Render(IReadOnlyDictionary<string, TimeSpan> phases)
        => string.Join(", ", Order.Where(p => phases.GetValueOrDefault(p) >= TimeSpan.FromSeconds(0.05)).Select(p => $"{p} {phases[p].TotalSeconds:0.0}s"));
}
