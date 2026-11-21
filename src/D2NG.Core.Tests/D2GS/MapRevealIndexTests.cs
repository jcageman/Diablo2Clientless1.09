using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class MapRevealIndexTests
{
    /// <summary>ActData's reveal handling as it was: a new tile per packet, found among the others by a linear search.</summary>
    private sealed class LegacyReveal
    {
        public Area Area { get; private set; }

        public List<Tile> Tiles { get; } = [];

        public void Handle(MapRevealPacket p, Point position)
        {
            var tile = new Tile(p.X, p.Y, p.Area);
            if (position == null || tile.Contains(position))
            {
                Area = p.Area;
            }

            if (!Tiles.Any(item => item.Equals(tile)))
            {
                Tiles.Add(tile);
                foreach (var t in Tiles)
                {
                    if (t.IsNorthOf(tile))
                    {
                        tile.North = t;
                        t.South = tile;
                    }
                    if (t.IsEastOf(tile))
                    {
                        tile.East = t;
                        t.West = tile;
                    }
                    if (t.IsSouthOf(tile))
                    {
                        tile.South = t;
                        t.North = tile;
                    }
                    if (t.IsWestOf(tile))
                    {
                        tile.West = t;
                        t.East = tile;
                    }
                }
            }
        }
    }

    [Fact]
    public void RevealsLeaveTheSameTilesNeighboursAndAreaAsBefore()
    {
        var random = new Random(11);
        Area[] areas = [Area.BloodMoor, Area.ColdPlains, Area.DenOfEvil, Area.CaveLevel1, Area.TowerCellarLevel1];
        for (var round = 0; round < 20; round++)
        {
            var legacy = new LegacyReveal();
            var act = new ActData();
            for (var step = 0; step < 400; step++)
            {
                var x = (ushort)(1000 + random.Next(12) * 8);
                var y = (ushort)(400 + random.Next(12) * 8);
                var area = areas[random.Next(areas.Length)];
                var packet = new MapRevealPacket(new D2gsPacket([0x07, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8), (byte)area]));
                var position = random.Next(5) == 0 ? null : new Point((ushort)((x + random.Next(-8, 16)) * 5), (ushort)((y + random.Next(-8, 16)) * 5));
                legacy.Handle(packet, position);
                act.HandleMapRevealPacket(packet, position);
                Assert.Equal(legacy.Area, act.Area);
                if (step % 40 != 39)
                {
                    continue;
                }

                Assert.Equal(legacy.Tiles.Count, act.Tiles.Count);
                for (var i = 0; i < legacy.Tiles.Count; i++)
                {
                    var (expected, actual) = (legacy.Tiles[i], act.Tiles[i]);
                    Assert.Equal((expected.X, expected.Y, expected.Area), (actual.X, actual.Y, actual.Area));
                    foreach (var direction in Enum.GetValues<CardinalDirection>())
                    {
                        var expectedIndex = expected.Adjacent.TryGetValue(direction, out var e) && e != null ? legacy.Tiles.IndexOf(e) : -1;
                        var actualIndex = actual.Adjacent.TryGetValue(direction, out var a) && a != null ? act.Tiles.IndexOf(a) : -1;
                        Assert.Equal(expectedIndex, actualIndex);
                    }
                }
            }
        }
    }
}
