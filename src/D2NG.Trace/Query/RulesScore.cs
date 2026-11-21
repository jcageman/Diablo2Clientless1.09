using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// What a client's game held against what it was told, for <c>accuracy --compare-rules</c>. A stop (0x6D) and an assign of a
/// unit already known (0x56/0x57) state a monster's life without changing it, so the life held just before them is
/// scored: the error of the model. A hit (0x0C) and a dying or dead state (0x69) state life after the damage, so the
/// life held after the packet is applied is scored against it: whether Game read it. Other 0x69 states are left out: their
/// byte is life after a hit as well, and Game reads it whatever the rules. Also the live digests' cursor and whole digest.
/// </summary>
public sealed class RulesScore : IReplayObserver
{
    private const double LifeTolerance = 1;

    public const string StopRow = "0x6D stop, held before (changes no life)";
    public const string AssignRow = "0x56/0x57 assign of a known unit, held before";
    public const string HitRow = "0x0C hit, held after applying it";
    public const string DyingRow = "0x69 dying or dead, life 0 after applying it";

    public Dictionary<string, (long N, long Agreeing, double AbsoluteSum)> Life { get; } = [];

    public long Digests { get; private set; }

    public long DigestsAgreeing { get; private set; }

    public long CursorAgreeing { get; private set; }

    private (string Row, uint Id, double Stated)? _pending;

    public static RulesScore Merge(IEnumerable<RulesScore> parts)
    {
        var merged = new RulesScore();
        foreach (var part in parts)
        {
            merged.Digests += part.Digests;
            merged.DigestsAgreeing += part.DigestsAgreeing;
            merged.CursorAgreeing += part.CursorAgreeing;
            foreach (var (kind, (n, agreeing, sum)) in part.Life)
            {
                var have = merged.Life.GetValueOrDefault(kind);
                merged.Life[kind] = (have.N + n, have.Agreeing + agreeing, have.AbsoluteSum + sum);
            }
        }

        return merged;
    }

    public void Before(ReplayContext context, TraceRecord record)
    {
        _pending = null;
        if (record.Kind != TraceRecordKind.D2gsReceived || record.Payload.Length == 0)
        {
            return;
        }

        try
        {
            switch ((InComingPacket)record.Payload[0])
            {
                case InComingPacket.NPCStop:
                    var stop = new NPCStopPacket(new D2gsPacket(record.Payload));
                    if (stop.LifePercentage > 0)
                    {
                        Held(context, StopRow, stop.EntityId, stop.LifePercentage);
                    }

                    break;
                case InComingPacket.AssignNPC1:
                case InComingPacket.AssignNPC2:
                    var assign = new AssignNpcPacket(new D2gsPacket(record.Payload));
                    if (assign.LifePercentage > 0)
                    {
                        Held(context, AssignRow, assign.EntityId, assign.LifePercentage);
                    }

                    break;
                case InComingPacket.NPCHit:
                    var hit = new NpcHitPacket(new D2gsPacket(record.Payload));
                    if (hit.EntityType == EntityType.NPC)
                    {
                        _pending = (HitRow, hit.EntityId, hit.LifePercentage);
                    }

                    break;
                case InComingPacket.NPCState:
                    var state = new NpcStatePacket(new D2gsPacket(record.Payload));
                    if (state.EntityState is EntityState.Dieing or EntityState.Dead)
                    {
                        _pending = (DyingRow, state.EntityId, 0);
                    }

                    break;
            }
        }
        catch (Exception e) when (e is D2GSPacketException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
        }
    }

    private void Held(ReplayContext context, string kind, uint id, double stated)
    {
        if (!context.Replayer.Game.WorldObjects.TryGetValue((id, EntityType.NPC), out var unit))
        {
            return;
        }

        var difference = Math.Abs(unit.LifePercentage - stated);
        var have = Life.GetValueOrDefault(kind);
        Life[kind] = (have.N + 1, have.Agreeing + (difference <= LifeTolerance ? 1 : 0), have.AbsoluteSum + difference);
    }

    public void After(ReplayContext context, TraceRecord record)
    {
        if (_pending is { } pending)
        {
            _pending = null;
            Held(context, pending.Row, pending.Id, pending.Stated);
        }

        if (record.Kind != TraceRecordKind.Digest || TraceDigest.FromBytes(record.Payload) is not { } live)
        {
            return;
        }

        TraceDigest held;
        try
        {
            held = TraceDigest.Capture(context.Replayer.Game);
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            held = null;
        }

        Digests++;
        if (held != null && held.CursorItemId == live.CursorItemId)
        {
            CursorAgreeing++;
        }

        if (!live.Differences(held).Any())
        {
            DigestsAgreeing++;
        }
    }
}
