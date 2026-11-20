using System;
using System.IO;
using System.Reflection;

namespace D2NG.Core.Tracing;

/// <summary>
/// Which build of D2NG.Core is running: the informational version the SDK stamps, "1.0.0+&lt;commit sha&gt;" when the
/// build saw the repository and "1.0.0+&lt;commit sha&gt;-dirty" when the checkout had uncommitted changes (the
/// AddDirtyMarker target in D2NG.Core.csproj), and when the program was built - the entry assembly's write time, else
/// this assembly's.
/// </summary>
public static class BuildIdentity
{
    public const string DirtySuffix = "-dirty";

    public static string Version { get; } = typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    public static DateTime? Utc { get; } = WrittenUtc();

    /// <summary>True when the build that ran had uncommitted changes: its sha does not name the code.</summary>
    public static bool Dirty => IsDirty(Version);

    public static bool IsDirty(string version) => version != null && version.EndsWith(DirtySuffix, StringComparison.Ordinal);

    /// <summary>The first 7 characters of the commit sha in a version, the whole version without one, "unknown" for none.</summary>
    public static string Short(string version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return "unknown";
        }

        if (IsDirty(version))
        {
            version = version[..^DirtySuffix.Length];
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version.Substring(plus + 1, Math.Min(7, version.Length - plus - 1));
    }

    private static DateTime? WrittenUtc()
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(BuildIdentity).Assembly })
        {
            try
            {
                var location = assembly?.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    return File.GetLastWriteTimeUtc(location);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        return null;
    }
}
