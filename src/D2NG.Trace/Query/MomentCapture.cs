using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>A monster as a client held it at a moment, with its position under each motion model and the server's last word.</summary>
public sealed record MomentUnit(uint Id, NPCCode Code, EntityState State, double Life, Point Legacy, Point Anchored, Point Simulated, Point Server,
    bool Boss, bool Champion, bool Minion, IReadOnlyList<MonsterEnchantment> Enchantments, IReadOnlyList<EntityEffect> Effects, bool Ally)
{
    public bool Alive => State is not (EntityState.Dieing or EntityState.Dead) && Life > 0;

    /// <summary>legacy, anchored, simulated or server (the last position a packet stated, else legacy).</summary>
    public Point At(string model) => model.ToLowerInvariant() switch
    {
        "anchored" => Anchored,
        "simulated" => Simulated,
        "server" => Server ?? Legacy,
        _ => Legacy,
    };
}

/// <summary>What a client held at one moment, in memory: the same content <see cref="MomentExport"/> writes, without the grid (take it from the map).</summary>
/// <param name="OtherPlayers">Players besides the client in its game; with them, or with a mercenary of its own, a monster's hit may be theirs.</param>
public sealed record Moment(TimeSpan At, Area? Area, uint MapId, Difficulty Difficulty, Point Self, Point SelfServer, int Life, int Mana, IReadOnlyList<MomentUnit> Units,
    int OtherPlayers = 0, bool Mercenary = false);

/// <summary>
/// Takes a <see cref="Moment"/> at each of the given offsets during one replay: just before the first record at or after
/// the offset is applied, so the state is the client's at that instant. Offsets past the replay's end get none.
/// </summary>
public sealed class MomentCapture : IReplayObserver
{
    private readonly List<TimeSpan> _times;
    private readonly AreaTracker _areas = new();
    private readonly StatedPositions _stated = new();
    private readonly ServerPositions _server = new();
    private int _next;

    public MomentCapture(IEnumerable<TimeSpan> times) => _times = [.. times.Order()];

    public Dictionary<TimeSpan, Moment> Moments { get; } = [];

    public void Before(ReplayContext context, TraceRecord record)
    {
        while (_next < _times.Count && _times[_next] <= context.Now)
        {
            if (context.Game.Me?.Location is not null)
            {
                Moments[_times[_next]] = Take(context);
            }

            _next++;
        }
    }

    public void After(ReplayContext context, TraceRecord record)
    {
        _areas.Observe(context, record);
        _stated.After(context, record);
        _server.Observe(context, record);
    }

    private Moment Take(ReplayContext context)
    {
        var game = context.Game;
        var me = game.Me;
        var mercs = new HashSet<uint>(game.Players.Where(p => p.MercId != null).Select(p => p.MercId.Value));
        if (me.MercId is { } own)
        {
            mercs.Add(own);
        }

        var units = game.WorldObjects.Values
            .Where(o => o.Type == EntityType.NPC && o.Location is not null)
            .OrderBy(o => o.Id)
            .Select(o => new MomentUnit(o.Id, o.NPCCode, o.State, o.LifePercentage, o.LegacyLocation, o.LastKnownLocation,
                o.LocationIn(Core.D2GS.Motion.MonsterMotion.Simulated), _stated.Last.TryGetValue(o.Id, out var last) ? last.Position : null,
                o.IsBoss, o.IsChampion, o.IsBossMinion, [.. o.MonsterEnchantments], [.. o.Effects], mercs.Contains(o.Id) || CombatObserver.IsAlly(o.NPCCode)))
            .ToList();
        return new Moment(context.Now, _areas.Current(context) ?? _areas.LastKnown, game.MapId, game.Difficulty, me.Location, _server.At(context.Now)?.Position,
            me.Life, me.Mana, units, game.Players.Count(p => p.Id != me.Id), me.MercId != null);
    }
}
