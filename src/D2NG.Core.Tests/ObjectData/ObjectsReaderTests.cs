using D2NG.Core.D2GS.Objects;
using D2NG.Core.ObjectData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace D2NG.Core.Tests.ObjectData;

/// <summary>
/// The record shape here is 1.09's objects.bin: 464 bytes, the Name string at the front and the
/// Parm0 dword at +392. The three shrine classes below are real rows: 84 is the fixed health shrine
/// of the Act 1 wilderness, 164 a fixed mana shrine in the Act 2 tombs, 2 the rolled one.
/// </summary>
public class ObjectsReaderTests
{
    private const int RecordSize = 464;
    private const int ParmOffset = 392;

    private static byte[] Table(int recordSize, params (string Name, int Parm0)[] records)
    {
        var raw = new byte[4 + recordSize * records.Length];
        BitConverter.GetBytes(records.Length).CopyTo(raw, 0);

        for (var i = 0; i < records.Length; i++)
        {
            var start = 4 + i * recordSize;
            Encoding.ASCII.GetBytes(records[i].Name).CopyTo(raw, start);
            BitConverter.GetBytes(records[i].Parm0).CopyTo(raw, start + ParmOffset);
        }

        return raw;
    }

    private static (string, int)[] Padded(int count)
    {
        var records = new (string, int)[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = ("Casket", 0);
        }

        return records;
    }

    private static ShrineTable Decode(byte[] raw)
        => ObjectsReader.Decode(raw, "test.mpq", "test", []);

    [Fact]
    public void OnlyShrineRowsAreKeptAndTheRecordIndexIsTheClassId()
    {
        var records = Padded(165);
        records[2] = ("Shrine", 3);
        records[84] = ("Shrine", 1);
        records[164] = ("Shrine", 2);
        records[100] = ("healthshrine", 1);

        var table = Decode(Table(RecordSize, records));

        Assert.Equal(4, table.Count);
        Assert.False(table.IsShrine(EntityCode.WaypointAct1));
        Assert.True(table.TryGetKind((EntityCode)2, out var rolled));
        Assert.Equal(ShrineKind.Random, rolled);
        Assert.True(table.TryGetKind((EntityCode)84, out var health));
        Assert.Equal(ShrineKind.Health, health);
        Assert.True(table.TryGetKind((EntityCode)164, out var mana));
        Assert.Equal(ShrineKind.Mana, mana);
        Assert.True(table.TryGet((EntityCode)100, out var typed));
        Assert.Equal("healthshrine", typed.Name);
    }

    [Fact]
    public void ShrineNamesMatchRegardlessOfCase()
    {
        var table = Decode(Table(RecordSize, ("shrine", 3), ("Magic Shrine", 3), ("Shrine2wilderness", 3)));

        Assert.Equal(3, table.Count);
    }

    [Fact]
    public void AParm0OutsideTheShrineKindsIsRefused()
    {
        var raw = Table(RecordSize, ("Shrine", 7));

        var error = Assert.Throws<InvalidDataException>(() => Decode(raw));
        Assert.Contains("Parm0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownRecordSizeIsRefusedRatherThanGuessed()
    {
        var raw = Table(RecordSize + 8, ("Shrine", 3));

        var error = Assert.Throws<InvalidDataException>(() => Decode(raw));
        Assert.Contains("unrecognised record size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameFieldThatIsNotTextIsRefused()
    {
        var raw = Table(RecordSize, ("Shrine", 3));
        raw[4] = 0x01;

        var error = Assert.Throws<InvalidDataException>(() => Decode(raw));
        Assert.Contains("non-printable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceRecordsWhereTheValuesCameFrom()
    {
        var raw = Table(RecordSize, ("Shrine", 3));

        var notes = new List<string>();
        var table = ObjectsReader.Decode(raw, "patch_d2.mpq", @"C:\Diablo II 1.09", notes);

        Assert.Equal("patch_d2.mpq", table.Source.Archive);
        Assert.Equal(RecordSize, table.Source.RecordSize);
        Assert.Equal(ParmOffset, table.Source.ParmOffset);
        Assert.Equal(64, table.Source.Sha256.Length);
        Assert.NotEmpty(notes);
    }
}
