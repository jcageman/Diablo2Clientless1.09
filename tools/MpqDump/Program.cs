using MpqLib;
using System;
using System.IO;
using System.Linq;

// Prints any file out of a Diablo 2 install, resolving the archives the way the game does. For
// answering questions the game itself already answers - a skill's cast delay, a column nobody
// documented - instead of trusting a wiki that may describe a different patch.
//
//   MpqDump --gamedir "C:\Diablo II 1.09" --file "data\global\excel\skills.txt"
//   MpqDump --gamedir "C:\Diablo II 1.09" --file "data\global\excel\skills.txt" --columns "skill,delay"

var gameDirectory = ValueOf("--gamedir");
var file = ValueOf("--file");
var columns = ValueOf("--columns");
var rowFilter = ValueOf("--rows");
var outPath = ValueOf("--out");
var onlyArchive = ValueOf("--archive");

if (gameDirectory == null || file == null)
{
    Console.Error.WriteLine("""
        Usage: MpqDump --gamedir <Diablo 2 install> --file <path inside the archive>
                       [--columns <comma separated>] [--rows <substring>] [--out <file>]
                       [--archive <name.mpq>]

        Without --columns the raw file is printed. With it, the file is read as a tab
        separated table and only those columns are shown. --archive reads that one archive
        instead of the first that has the file, to see what a patch overrode.
        """);
    return 1;
}

// The order the game resolves them in: first archive holding the file wins.
string[] archives = onlyArchive != null ? [onlyArchive] : ["patch_d2.mpq", "d2exp.mpq", "d2data.mpq"];
byte[] raw = null;
var from = "";
foreach (var archive in archives)
{
    var path = Path.Combine(gameDirectory, archive);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"  {archive} not present, skipped");
        continue;
    }

    using var mpq = new MpqArchive(path);
    if (!mpq.FileExists(file))
    {
        Console.Error.WriteLine($"  {archive} has no {file}, skipped");
        continue;
    }

    using var stream = mpq.OpenFile(file);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    raw = buffer.ToArray();
    from = archive;
    break;
}

if (raw == null)
{
    Console.Error.WriteLine($"None of {string.Join(", ", archives)} in '{gameDirectory}' contains {file}.");
    return 1;
}

Console.Error.WriteLine($"  {file} read from {from}, {raw.Length} bytes");

// Binary tables must go to a file untouched: the patch archive ships .bin overrides of the
// readable .txt files and they disagree, so the bin is what the game actually reads.
if (outPath != null)
{
    File.WriteAllBytes(outPath, raw);
    Console.Error.WriteLine($"  wrote {raw.Length} bytes to {outPath}");
    return 0;
}

var text = System.Text.Encoding.ASCII.GetString(raw);
if (columns == null)
{
    Console.WriteLine(text);
    return 0;
}

var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
var header = lines[0].Split('\t');
var wanted = columns.Split(',').Select(c => c.Trim()).ToList();
var indexes = wanted
    .Select(w => (Name: w, Index: Array.FindIndex(header, h => h.Equals(w, StringComparison.OrdinalIgnoreCase))))
    .ToList();

var missing = indexes.Where(i => i.Index < 0).Select(i => i.Name).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"No such column(s): {string.Join(", ", missing)}");
    Console.Error.WriteLine($"Available: {string.Join(", ", header)}");
    return 1;
}

Console.WriteLine(string.Join("\t", indexes.Select(i => i.Name)));
foreach (var line in lines.Skip(1))
{
    if (rowFilter != null && !line.Contains(rowFilter, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    var cells = line.Split('\t');
    Console.WriteLine(string.Join("\t", indexes.Select(i => i.Index < cells.Length ? cells[i.Index] : "")));
}

return 0;

string ValueOf(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
