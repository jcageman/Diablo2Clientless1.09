using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.MonsterData;
using D2NG.Core;
using D2NG.Core.ItemData;
using D2NG.Core.ObjectData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Regenerates the game-data files the bot reads, straight out of a Diablo 2 install. Run it after
// the realm patches; nothing calls it at runtime.
//
//   MpqData --gamedir "C:\Diablo II 1.09" --out "D:\projects\diablo2bot\data"

var options = ParseArgs(args);
if (options == null && ProbeArgs(args) == null)
{
    Console.Error.WriteLine("""
        Usage: MpqData --gamedir <Diablo 2 install> --out <data directory> [--report <directory>]

          --gamedir  Folder holding patch_d2.mpq / d2exp.mpq / d2data.mpq.
          --out      Folder to write monster-resists.json, shrines.json and item-affixes.json into.
                     Created if missing.
          --report   Folder to write affixes-classic.md and affixes-expansion.md into: what can
                     spawn on each item type, for reviewing pickit rules.
        """);
    return 1;
}

var probeArgs = ProbeArgs(args);
if (probeArgs != null)
{
    return MpqData.ProbeBin.Run(probeArgs.Value.GameDirectory, probeArgs.Value.Bin, probeArgs.Value.Filter);
}

var (gameDirectory, outDirectory, reportDirectory) = options.Value;

if (!Directory.Exists(gameDirectory))
{
    Console.Error.WriteLine($"Game directory '{gameDirectory}' does not exist.");
    return 1;
}

Console.WriteLine($"Reading monster data from {gameDirectory}");

MonsterResistTable table;
try
{
    var (read, notes) = MonstatsReader.Read(gameDirectory);
    table = read;
    foreach (var note in notes)
    {
        Console.WriteLine($"  {note}");
    }
}
catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
{
    Console.Error.WriteLine($"Extraction failed: {ex.Message}");
    return 1;
}

Directory.CreateDirectory(outDirectory);
var target = Path.Combine(outDirectory, "monster-resists.json");
table.Save(target);

Console.WriteLine($"  source: {table.Source}");
Console.WriteLine($"Wrote {table.Count} monsters to {target}");
Console.WriteLine();

// Read back what was written rather than reporting the in-memory copy, so the spot check also
// proves the bot can load the file.
table = MonsterResistTable.Load(target);

// Printed so a human can eyeball the table after a patch instead of trusting it silently. These
// three are the ones the bots actually turn decisions on.
Console.WriteLine("Spot check (reloaded from disk):");
foreach (var code in new[] { NPCCode.BurningSoul, NPCCode.OblivionKnight, NPCCode.DoomKnight })
{
    if (!table.TryGet(code, out var entry))
    {
        Console.WriteLine($"  {code}: MISSING from the table");
        continue;
    }

    Console.WriteLine($"  {code} (id {entry.ClassId}, monstats '{entry.Class}')");
    foreach (var type in Enum.GetValues<ResistType>())
    {
        var perDifficulty = Enum.GetValues<Difficulty>()
            .Select(difficulty => $"{difficulty}={entry.Base(type, difficulty)}");
        Console.WriteLine($"    {type,-9} {string.Join("  ", perDifficulty)}");
    }
}

Console.WriteLine();
Console.WriteLine($"Reading object data from {gameDirectory}");

ShrineTable shrineTable;
try
{
    var (read, notes) = ObjectsReader.Read(gameDirectory);
    shrineTable = read;
    foreach (var note in notes)
    {
        Console.WriteLine($"  {note}");
    }
}
catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
{
    Console.Error.WriteLine($"Extraction failed: {ex.Message}");
    return 1;
}

var shrineTarget = Path.Combine(outDirectory, GameDataLocation.ShrinesFile);
shrineTable.Save(shrineTarget);
Console.WriteLine($"  source: {shrineTable.Source}");
Console.WriteLine($"Wrote {shrineTable.Count} shrines to {shrineTarget}");

// The three shrine classes the Act 1 wilderness places, reloaded from disk like the monsters above.
// 84 must come out as a fixed health shrine: its row is called plain "Shrine" and only Parm0 says so.
shrineTable = ShrineTable.Load(shrineTarget);
Console.WriteLine("Spot check (reloaded from disk):");
foreach (var classId in new[] { 2, 81, 84 })
{
    Console.WriteLine(shrineTable.TryGet((EntityCode)classId, out var shrine)
        ? $"  object {classId} '{shrine.Name}': {shrine.Kind}"
        : $"  object {classId}: MISSING from the table");
}

Console.WriteLine();
Console.WriteLine($"Reading item affix data from {gameDirectory}");

ItemAffixTable affixTable;
try
{
    var (read, notes) = ItemAffixReader.Read(gameDirectory);
    affixTable = read;
    foreach (var note in notes)
    {
        Console.WriteLine($"  {note}");
    }
}
catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
{
    Console.Error.WriteLine($"Extraction failed: {ex.Message}");
    return 1;
}

var affixTarget = Path.Combine(outDirectory, GameDataLocation.ItemAffixesFile);
affixTable.Save(affixTarget);
Console.WriteLine($"Wrote {affixTable.Affixes.Count} affixes, {affixTable.BaseItems.Count} bases and {affixTable.CubeRecipes.Count} cube recipes to {affixTarget}");

// Rings are the plainest check that the type tree and the rare flag decode: classic rare rings
// must lose every expansion-only affix and every magic-only one.
affixTable = ItemAffixTable.Load(affixTarget);
var ring = affixTable.BaseItems.First(item => item.Code == "rin");
Console.WriteLine("Spot check (reloaded from disk), affixes that can roll on a ring:");
foreach (var (expansion, quality) in new[] { (false, AffixQuality.Magic), (false, AffixQuality.Rare), (true, AffixQuality.Magic), (true, AffixQuality.Rare) })
{
    var count = affixTable.EligibleAffixes(ring, expansion, quality).Count();
    Console.WriteLine($"  {(expansion ? "expansion" : "classic"),-9} {quality,-5} {count}");
}

if (reportDirectory != null)
{
    MpqData.AffixReport.Write(affixTable, reportDirectory);
}

return 0;

static (string GameDirectory, string Bin, string Filter)? ProbeArgs(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        values[args[i].TrimStart('-')] = args[i + 1];
    }

    if (!values.TryGetValue("gamedir", out var gameDirectory) || !values.TryGetValue("probe", out var bin))
    {
        return null;
    }

    values.TryGetValue("find", out var filter);
    return (gameDirectory, bin, filter);
}

static (string GameDirectory, string OutDirectory, string ReportDirectory)? ParseArgs(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        values[args[i].TrimStart('-')] = args[i + 1];
    }

    values.TryGetValue("report", out var reportDirectory);
    return values.TryGetValue("gamedir", out var gameDirectory) && values.TryGetValue("out", out var outDirectory)
        ? (gameDirectory, outDirectory, reportDirectory)
        : null;
}
