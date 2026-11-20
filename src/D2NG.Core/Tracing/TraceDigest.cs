using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace D2NG.Core.Tracing;

/// <summary>
/// A summary of what one client believes about its game, written live and recomputed by replay at the
/// same point in the packet stream: any field that differs means the replayed state is not the state
/// the bot acted on.
/// </summary>
public sealed record TraceDigest
{
    private const byte Version = 1;

    public const int Size = 1 + 2 + 2 + 4 + 4 + 4 + 2 + 1 + 1 + 4 + 4 + 4 + 4 + 4 + 4 + 4;

    public ushort X { get; init; }
    public ushort Y { get; init; }
    public int Life { get; init; }
    public int Mana { get; init; }
    public uint Experience { get; init; }
    public Area Area { get; init; }
    public Act Act { get; init; }
    public Difficulty Difficulty { get; init; }
    public uint MapId { get; init; }
    public uint CursorItemId { get; init; }
    public int Npcs { get; init; }
    public int Objects { get; init; }
    public int OtherWorldObjects { get; init; }
    public int Players { get; init; }
    public int Items { get; init; }

    public static TraceDigest Capture(Game game)
    {
        var me = game.Me;
        if (me == null)
        {
            return null;
        }

        int npcs = 0, objects = 0, other = 0;
        foreach (var worldObject in game.WorldObjects.Values)
        {
            switch (worldObject.Type)
            {
                case EntityType.NPC:
                    npcs++;
                    break;
                case EntityType.Object:
                    objects++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        var location = me.Location;
        return new TraceDigest
        {
            X = location?.X ?? 0,
            Y = location?.Y ?? 0,
            Life = me.Life,
            Mana = me.Mana,
            Experience = me.Experience,
            Area = game.Area,
            Act = game.Act,
            Difficulty = game.Difficulty,
            MapId = game.MapId,
            CursorItemId = game.CursorItem?.Id ?? 0,
            Npcs = npcs,
            Objects = objects,
            OtherWorldObjects = other,
            Players = game.Players.Count,
            Items = game.Items.Count,
        };
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        var span = bytes.AsSpan();
        span[0] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(span[1..], X);
        BinaryPrimitives.WriteUInt16LittleEndian(span[3..], Y);
        BinaryPrimitives.WriteInt32LittleEndian(span[5..], Life);
        BinaryPrimitives.WriteInt32LittleEndian(span[9..], Mana);
        BinaryPrimitives.WriteUInt32LittleEndian(span[13..], Experience);
        BinaryPrimitives.WriteUInt16LittleEndian(span[17..], (ushort)Area);
        span[19] = (byte)Act;
        span[20] = (byte)Difficulty;
        BinaryPrimitives.WriteUInt32LittleEndian(span[21..], MapId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[25..], CursorItemId);
        BinaryPrimitives.WriteInt32LittleEndian(span[29..], Npcs);
        BinaryPrimitives.WriteInt32LittleEndian(span[33..], Objects);
        BinaryPrimitives.WriteInt32LittleEndian(span[37..], OtherWorldObjects);
        BinaryPrimitives.WriteInt32LittleEndian(span[41..], Players);
        BinaryPrimitives.WriteInt32LittleEndian(span[45..], Items);
        return bytes;
    }

    public static TraceDigest FromBytes(ReadOnlySpan<byte> span)
    {
        if (span.Length < Size || span[0] < Version)
        {
            return null;
        }

        return new TraceDigest
        {
            X = BinaryPrimitives.ReadUInt16LittleEndian(span[1..]),
            Y = BinaryPrimitives.ReadUInt16LittleEndian(span[3..]),
            Life = BinaryPrimitives.ReadInt32LittleEndian(span[5..]),
            Mana = BinaryPrimitives.ReadInt32LittleEndian(span[9..]),
            Experience = BinaryPrimitives.ReadUInt32LittleEndian(span[13..]),
            Area = (Area)BinaryPrimitives.ReadUInt16LittleEndian(span[17..]),
            Act = (Act)span[19],
            Difficulty = (Difficulty)span[20],
            MapId = BinaryPrimitives.ReadUInt32LittleEndian(span[21..]),
            CursorItemId = BinaryPrimitives.ReadUInt32LittleEndian(span[25..]),
            Npcs = BinaryPrimitives.ReadInt32LittleEndian(span[29..]),
            Objects = BinaryPrimitives.ReadInt32LittleEndian(span[33..]),
            OtherWorldObjects = BinaryPrimitives.ReadInt32LittleEndian(span[37..]),
            Players = BinaryPrimitives.ReadInt32LittleEndian(span[41..]),
            Items = BinaryPrimitives.ReadInt32LittleEndian(span[45..]),
        };
    }

    public IEnumerable<(string Field, string Expected, string Actual)> Differences(TraceDigest actual)
    {
        if (actual == null)
        {
            yield return ("digest", "present", "no player yet");
            yield break;
        }

        foreach (var (field, expected, other) in new (string, object, object)[]
        {
            (nameof(X), X, actual.X),
            (nameof(Y), Y, actual.Y),
            (nameof(Life), Life, actual.Life),
            (nameof(Mana), Mana, actual.Mana),
            (nameof(Experience), Experience, actual.Experience),
            (nameof(Area), Area, actual.Area),
            (nameof(Act), Act, actual.Act),
            (nameof(Difficulty), Difficulty, actual.Difficulty),
            (nameof(MapId), MapId, actual.MapId),
            (nameof(CursorItemId), CursorItemId, actual.CursorItemId),
            (nameof(Npcs), Npcs, actual.Npcs),
            (nameof(Objects), Objects, actual.Objects),
            (nameof(OtherWorldObjects), OtherWorldObjects, actual.OtherWorldObjects),
            (nameof(Players), Players, actual.Players),
            (nameof(Items), Items, actual.Items),
        })
        {
            if (!Equals(expected, other))
            {
                yield return (field, expected.ToString(), other.ToString());
            }
        }
    }
}
