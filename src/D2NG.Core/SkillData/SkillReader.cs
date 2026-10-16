using D2NG.Core.ItemData;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace D2NG.Core.SkillData;

/// <summary>
/// Reads skills.bin out of a Diablo 2 install. The patch_d2.mpq bin overrides the d2exp.mpq txt
/// (Might and Concentration radius, Maul, Feral Rage, Double Swing differ), so only the bin is read.
/// The offsets are 1.09's 360 byte record, worked out by matching every txt column against it.
/// </summary>
public static class SkillReader
{
    internal const int SkillSize = 360;

    private const string File = @"data\global\excel\skills.bin";

    public static (SkillTable Table, IReadOnlyList<string> Notes) Read(string gameDirectory)
    {
        var (archive, raw) = ItemAffixReader.ReadRawTable(gameDirectory, File);
        var table = new SkillTable { Skills = DecodeSkills(raw, File) };
        table.Sources.Add(new ItemAffixSourceFile
        {
            File = File,
            Archive = archive,
            Sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            RecordCount = table.Skills.Count,
            RecordSize = SkillSize,
            ExtractedUtc = DateTime.UtcNow
        });
        return (table, [$"{File}: {table.Skills.Count} records from {archive}"]);
    }

    internal static List<SkillEntry> DecodeSkills(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, SkillSize);
        var skills = new List<SkillEntry>();
        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * SkillSize;
            int Int(int offset) => BitConverter.ToInt32(raw, start + offset);
            skills.Add(new SkillEntry
            {
                Id = i,
                Name = ItemAffixReader.ReadString(raw, start + 4, 16, file, i),
                Class = ItemAffixReader.ClassReference(raw[start + 20]),
                RequiredLevel = Int(152),
                Page = raw[start + 212],
                Row = raw[start + 213],
                Column = raw[start + 214],
                Passive = Int(236) != 0,
                Params = [Int(240), Int(244), Int(248), Int(252), Int(256), Int(260)],
                ToHit = Int(288),
                LevelToHit = Int(292),
                HitShift = raw[start + 296],
                SourceDamage = raw[start + 297],
                MinDamage = Int(300),
                MaxDamage = Int(304),
                LevelDamage = [Int(308), Int(312), Int(316)],
                Element = (SkillElement)raw[start + 320],
                ElementMin = Int(324),
                ElementMax = Int(328),
                ElementLevel = [Int(332), Int(336), Int(340)],
                ElementLength = Int(344)
            });
        }

        return skills;
    }
}
