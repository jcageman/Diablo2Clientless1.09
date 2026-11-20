using D2NG.Core.D2GS.Items;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// Potions drunk for nothing: a healing potion within 1.5 s of the previous one (healing potions do not stack - the
/// later one replaces what is left of the earlier, so drinking faster heals no faster), a healing potion at full life,
/// a mana potion at full mana or within <see cref="ManaObserver.Refill"/> of the previous mana potion (that one still
/// refilling; measured, a mana potion drunk then queues behind it, so the mana comes later rather than faster), a
/// rejuvenation at full life and mana. Rejuvenations heal at once and are never too soon.
/// A drink is a belt use (0x26, not one given to the mercenary) or an inventory use (0x20) of a potion that the server
/// consumed (0x3F for it, or its 0x9C/0x9D removal from the belt, within 2 s; a resend before then is the same drink);
/// life and mana are what the client held when it sent it, and the time is when it sent it. Wasted drinks less than 10 s
/// apart are one incident.
/// </summary>
public sealed class PotionWasteCheck : IncidentCheck, IFamily
{
    public const string Kind = "potions wasted";

    public static readonly TimeSpan HealPeriod = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SameEpisode = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ConsumeWithin = TimeSpan.FromSeconds(2);

    private TimeSpan? _lastHealing;
    private (TimeSpan At, string Mana)? _lastMana;
    private readonly List<(TimeSpan At, string Why, string Potion)> _episode = [];
    private readonly Dictionary<uint, (TimeSpan At, ClassificationType Kind, string Potion, bool FullLife, bool FullMana, string Life, string Mana)> _sent = [];

    public string Family => "bot";

    public override void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            Consumed(context, p);
            return;
        }

        if (record.Kind != TraceRecordKind.D2gsSent || p.Length < 5 || p[0] is not (0x26 or 0x20) || context.Game.Me is not { } me)
        {
            return;
        }

        if (p[0] == 0x26 && p.Length >= 9 && PacketDecoder.U32(p, 5) != 0)
        {
            return;
        }

        var id = PacketDecoder.U32(p, 1);
        var item = Find(context, id, p[0] == 0x26);
        if (item == null || item.Classification is not (ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion))
        {
            return;
        }

        var now = context.Now;
        if (_sent.TryGetValue(id, out var earlier) && now - earlier.At <= ConsumeWithin)
        {
            return;
        }

        var maxLife = context.MaxLife;
        var maxMana = SafeMaxMana(me);
        _sent[id] = (now, item.Classification, item.Name.ToString(), maxLife > 0 && me.Life >= maxLife, maxMana > 0 && me.Mana >= maxMana, $"{me.Life}/{maxLife}", $"{me.Mana}/{maxMana}");
    }

    private void Consumed(ReplayContext context, byte[] p)
    {
        if (_sent.Count == 0 || p.Length < 6)
        {
            return;
        }

        uint id;
        if (p[0] == 0x3F)
        {
            id = PacketDecoder.U32(p, 2);
        }
        else if (p[0] is 0x9C or 0x9D && p.Length >= 8 && p[1] == (byte)Core.D2GS.Items.Action.RemoveFromBelt)
        {
            id = PacketDecoder.U32(p, 4);
        }
        else
        {
            return;
        }

        if (!_sent.Remove(id, out var drink) || context.Now - drink.At > ConsumeWithin)
        {
            return;
        }

        var now = drink.At;
        string why = null;
        switch (drink.Kind)
        {
            case ClassificationType.HealthPotion:
                if (_lastHealing is { } previous && now - previous < HealPeriod)
                {
                    why = $"{(now - previous).TotalMilliseconds:0} ms after the previous healing potion";
                }
                else if (drink.FullLife)
                {
                    why = $"at full life {drink.Life}";
                }

                _lastHealing = now;
                break;
            case ClassificationType.ManaPotion:
                if (drink.FullMana)
                {
                    why = $"at full mana {drink.Mana}";
                }
                else if (_lastMana is { } previousMana && now - previousMana.At < ManaObserver.Refill)
                {
                    why = $"{(now - previousMana.At).TotalMilliseconds:0} ms after the previous mana potion (mana {previousMana.Mana} then, {drink.Mana} now), still refilling";
                }

                _lastMana = (now, drink.Mana);
                break;
            case ClassificationType.RejuvenationPotion when drink.FullLife && drink.FullMana:
                why = "at full life and mana";
                break;
        }

        if (why == null)
        {
            return;
        }

        if (_episode.Count > 0 && now - _episode[^1].At > SameEpisode)
        {
            Flush(context);
        }

        _episode.Add((now, why, drink.Potion));
    }

    public override void Finished(ReplayContext context)
    {
        Flush(context);
        _lastHealing = null;
        _lastMana = null;
        _sent.Clear();
    }

    private void Flush(ReplayContext context)
    {
        if (_episode.Count == 0)
        {
            return;
        }

        var potions = string.Join(", ", _episode.GroupBy(e => e.Potion).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
        var whys = string.Join("; ", _episode.Select(e => $"{TraceTime.Format(e.At)} {e.Why}"));
        AddFound(new Incident(_episode[0].At, context.Client, Kind, $"{_episode.Count} potion{(_episode.Count == 1 ? "" : "s")} drunk for nothing ({potions}): {whys}")
        {
            End = _episode.Count > 1 ? _episode[^1].At : null,
        });
        _episode.Clear();
    }

    internal static Item Find(ReplayContext context, uint id, bool belt)
    {
        try
        {
            var container = belt ? (Core.D2GS.Items.Containers.Container)context.Game.Belt : context.Game.Inventory;
            return container?.Items.FirstOrDefault(i => i.Id == id);
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static int SafeMaxMana(Core.D2GS.Players.Self me)
    {
        try
        {
            return me.MaxMana;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or KeyNotFoundException)
        {
            return 0;
        }
    }
}
