using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace D2NG.Core;

/// <summary>
/// Where the extracted game data files live.
///
/// A host points this at the folder holding its data files - normally a <c>data</c> folder beside
/// its run configs - so a table regenerated after a realm patch is picked up without rebuilding.
/// Anything not found there falls back to a <c>data</c> folder beside the assembly, which is how the
/// test projects supply their fixtures.
/// </summary>
public static class GameDataLocation
{
    public const string ItemDataFile = "item_data.txt";
    public const string ItemPropertiesFile = "item_properties.txt";
    public const string MonsterResistsFile = "monster-resists.json";
    public const string ShrinesFile = "shrines.json";
    public const string ItemAffixesFile = "item-affixes.json";

    /// <summary>Files without which no item can be parsed, so a host should check them up front.</summary>
    public static IReadOnlyList<string> RequiredFiles { get; } = [ItemDataFile, ItemPropertiesFile];

    private static string _configuredDirectory;

    /// <summary>The folder a host asked for, or null when nothing has.</summary>
    public static string ConfiguredDirectory => _configuredDirectory;

    public static string DefaultDirectory { get; } = DirectoryNextToAssembly();

    public static void Use(string directory)
    {
        _configuredDirectory = directory;
        DataManager.DataManager.Reset();
    }

    public static bool TryResolve(string fileName, out string path)
    {
        foreach (var directory in Directories())
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = null;
        return false;
    }

    public static string Resolve(string fileName)
        => TryResolve(fileName, out var path)
            ? path
            : throw new FileNotFoundException(
                $"Game data file '{fileName}' is in none of: {string.Join(", ", Directories())}");

    public static List<string> MissingRequiredFiles()
    {
        var missing = new List<string>();
        foreach (var file in RequiredFiles)
        {
            if (!TryResolve(file, out _))
            {
                missing.Add(file);
            }
        }

        return missing;
    }

    public static IEnumerable<string> Directories()
    {
        if (!string.IsNullOrEmpty(_configuredDirectory))
        {
            yield return _configuredDirectory;
        }

        yield return DefaultDirectory;
    }

    private static string DirectoryNextToAssembly()
    {
        var assemblyFile = new Uri(Assembly.GetExecutingAssembly().Location).AbsolutePath.Replace("%20", " ");
        return Path.Combine(Path.GetDirectoryName(assemblyFile), "data");
    }
}
