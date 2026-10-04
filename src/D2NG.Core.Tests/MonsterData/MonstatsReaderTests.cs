using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.MonsterData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace D2NG.Core.Tests.MonsterData;

/// <summary>
/// The decode is what a realm patch can invalidate, so it has to fail loudly rather than hand back
/// plausible-looking nonsense. The record shape here is 1.09's: 864 bytes, the Class string at the
/// front, and 18 signed resistance bytes at +729 grouped per stat with three difficulties each.
/// </summary>
public class MonstatsReaderTests
{
    private const int RecordSize = 864;
    private const int ResistOffset = 729;

    private static byte[] Table(int recordSize, params (string Class, int[] Resists)[] records)
    {
        var raw = new byte[4 + recordSize * records.Length];
        BitConverter.GetBytes(records.Length).CopyTo(raw, 0);

        for (var i = 0; i < records.Length; i++)
        {
            var start = 4 + i * recordSize;
            Encoding.ASCII.GetBytes(records[i].Class).CopyTo(raw, start);
            for (var k = 0; k < records[i].Resists.Length; k++)
            {
                raw[start + ResistOffset + k] = unchecked((byte)(sbyte)records[i].Resists[k]);
            }
        }

        return raw;
    }

    private static MonsterResistTable Decode(byte[] raw)
        => MonstatsReader.Decode(raw, "test.mpq", "test", []);

    [Fact]
    public void RecordIndexIsTheClassIdAndResistsAreGroupedPerStat()
    {
        // Two rows padded out to Gloam's real class id, whose lightning resistance is the reason
        // burning souls and gloams cannot be hurt by static field in Hell.
        var records = new (string, int[])[(int)NPCCode.Gloam + 1];
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = ("unused", new int[18]);
        }

        records[(int)NPCCode.Gloam] = ("Gloam", [
            40, 40, 10,     // physical
            0, 0, 0,        // magic
            0, 0, 0,        // fire
            50, 70, 100,    // lightning
            0, 0, 0,        // cold
            0, 0, 0         // poison
        ]);

        var table = Decode(Table(RecordSize, records));

        Assert.True(table.TryGet(NPCCode.Gloam, out var gloam));
        Assert.Equal("Gloam", gloam.Class);
        Assert.Equal(10, gloam.Base(ResistType.Physical, Difficulty.Hell));
        Assert.Equal(70, gloam.Base(ResistType.Lightning, Difficulty.Nightmare));
        Assert.Equal(100, gloam.Base(ResistType.Lightning, Difficulty.Hell));
        Assert.Equal(0, gloam.Base(ResistType.Cold, Difficulty.Hell));
    }

    [Fact]
    public void NegativeResistancesSurviveTheDecode()
    {
        var table = Decode(Table(RecordSize, ("Andariel", [0, 0, 0, 0, 0, 0, -50, -50, -50, 0, 0, 0, 0, 0, 0, 0, 0, 0])));

        Assert.True(table.TryGet((NPCCode)0, out var andariel));
        Assert.Equal(-50, andariel.Base(ResistType.Fire, Difficulty.Hell));
    }

    /// <summary>Blank placeholder rows are real; 1.09 has a handful and they must not abort the read.</summary>
    [Fact]
    public void BlankRecordsAreKept()
    {
        var table = Decode(Table(RecordSize, ("Gloam", new int[18]), (string.Empty, new int[18])));

        Assert.Equal(2, table.Count);
        Assert.True(table.TryGet((NPCCode)1, out var blank));
        Assert.Equal(string.Empty, blank.Class);
    }

    [Fact]
    public void AnUnknownRecordSizeIsRefusedRatherThanGuessed()
    {
        var raw = Table(RecordSize + 8, ("Gloam", new int[18]));

        var error = Assert.Throws<InvalidDataException>(() => Decode(raw));
        Assert.Contains("unrecognised record size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResistOutsideThePlausibleRangeIsRefused()
    {
        var raw = Table(RecordSize, ("Gloam", new int[18]));
        raw[4 + ResistOffset] = unchecked((byte)(sbyte)-120);

        var error = Assert.Throws<InvalidDataException>(() => Decode(raw));
        Assert.Contains("outside the plausible", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameFieldThatIsNotTextIsRefused()
    {
        var raw = Table(RecordSize, ("Gloam", new int[18]));
        raw[4] = 0x01;

        var error = Assert.Throws<InvalidDataException>(() => Decode(raw));
        Assert.Contains("non-printable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordCountThatDoesNotDivideTheFileIsRefused()
    {
        var raw = Table(RecordSize, ("Gloam", new int[18]));
        BitConverter.GetBytes(7).CopyTo(raw, 0);

        var error = Assert.Throws<InvalidDataException>(() => Decode(raw));
        Assert.Contains("does not divide evenly", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceRecordsWhereTheValuesCameFrom()
    {
        var raw = Table(RecordSize, ("Gloam", new int[18]));

        var notes = new List<string>();
        var table = MonstatsReader.Decode(raw, "patch_d2.mpq", @"C:\Diablo II 1.09", notes);

        Assert.Equal("patch_d2.mpq", table.Source.Archive);
        Assert.Equal(RecordSize, table.Source.RecordSize);
        Assert.Equal(ResistOffset, table.Source.ResistOffset);
        Assert.Equal(64, table.Source.Sha256.Length);
        Assert.NotEmpty(notes);
    }
}
