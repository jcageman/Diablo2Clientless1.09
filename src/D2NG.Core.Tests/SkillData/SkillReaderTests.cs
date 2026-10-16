using D2NG.Core.D2GS.Enums;
using D2NG.Core.SkillData;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace D2NG.Core.Tests.SkillData;

/// <summary>
/// Pins the 1.09 skills.bin offsets, which were found by matching every column of the d2exp.mpq txt
/// against the patch_d2.mpq bin.
/// </summary>
public class SkillReaderTests
{
    private static byte[] Record(Action<byte[]> fill)
    {
        var raw = new byte[4 + SkillReader.SkillSize];
        BitConverter.GetBytes(1).CopyTo(raw, 0);
        fill(raw);
        return raw;
    }

    private static void Put(byte[] raw, int at, int value) => BitConverter.GetBytes(value).CopyTo(raw, at);

    [Fact]
    public void ASkillDecodesAtThe109Offsets()
    {
        const int s = 4;
        var raw = Record(raw =>
        {
            Encoding.ASCII.GetBytes("Natural Resistan").CopyTo(raw, s + 4);
            raw[s + 20] = 4;
            Put(raw, s + 152, 12);
            raw[s + 212] = 2;
            raw[s + 213] = 3;
            raw[s + 214] = 1;
            Put(raw, s + 236, 1);
            Put(raw, s + 240, 0);
            Put(raw, s + 244, 80);
            Put(raw, s + 288, 20);
            Put(raw, s + 292, 5);
            raw[s + 296] = 8;
            raw[s + 297] = 8;
            raw[s + 320] = 4;
            Put(raw, s + 324, 40);
            Put(raw, s + 328, 45);
            Put(raw, s + 332, 10);
            Put(raw, s + 336, 12);
            Put(raw, s + 340, 14);
            Put(raw, s + 344, 25);
        });

        var skill = Assert.Single(SkillReader.DecodeSkills(raw, "skills.bin"));

        Assert.Equal(0, skill.Id);
        Assert.Equal("Natural Resistan", skill.Name);
        Assert.Equal(CharacterClass.Barbarian, skill.Class);
        Assert.Equal(12, skill.RequiredLevel);
        Assert.Equal((2, 3, 1), (skill.Page, skill.Row, skill.Column));
        Assert.True(skill.Passive);
        Assert.Equal([0, 80, 0, 0, 0, 0], skill.Params);
        Assert.Equal((20, 5), (skill.ToHit, skill.LevelToHit));
        Assert.Equal((8, 8), (skill.HitShift, skill.SourceDamage));
        Assert.Equal(SkillElement.Cold, skill.Element);
        Assert.Equal((40, 45), (skill.ElementMin, skill.ElementMax));
        Assert.Equal([10, 12, 14], skill.ElementLevel);
        Assert.Equal(25, skill.ElementLength);
    }

    [Fact]
    public void AClasslessSkillHasNoClass()
    {
        var skill = Assert.Single(SkillReader.DecodeSkills(Record(raw => raw[4 + 20] = 0xFF), "skills.bin"));

        Assert.Null(skill.Class);
    }

    [Fact]
    public void ARecordOfTheWrongSizeFails()
    {
        var raw = new byte[4 + SkillReader.SkillSize + 1];
        BitConverter.GetBytes(1).CopyTo(raw, 0);

        Assert.Throws<InvalidDataException>(() => SkillReader.DecodeSkills(raw, "skills.bin"));
    }
}
