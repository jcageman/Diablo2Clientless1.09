using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Text;

namespace D2NG.Trace;

/// <summary>The header fields after the server game id: rules version, build and build time, each absent in older files.</summary>
public static class TraceHeaderIdentity
{
    /// <param name="span">The header after its fixed 8 bytes.</param>
    /// <param name="serverOffset">Where the server game id's length sits in <paramref name="span"/>.</param>
    /// <returns>Null rules when the header has no rules field: the recorder did not say, which is not rules 0.</returns>
    public static (int? Rules, string Build, DateTime? BuildUtc) Read(ReadOnlySpan<byte> span, int serverOffset)
    {
        if (serverOffset + 2 > span.Length)
        {
            return (null, null, null);
        }

        var offset = serverOffset + 2 + BinaryPrimitives.ReadUInt16LittleEndian(span[serverOffset..]);
        if (offset + 2 > span.Length)
        {
            return (null, null, null);
        }

        int rules = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]);
        offset += 2;
        if (offset + 2 > span.Length)
        {
            return (rules, null, null);
        }

        var buildLength = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]);
        if (offset + 2 + buildLength > span.Length)
        {
            return (rules, null, null);
        }

        var build = Encoding.UTF8.GetString(span.Slice(offset + 2, buildLength));
        offset += 2 + buildLength;
        DateTime? buildUtc = null;
        if (offset + 8 <= span.Length)
        {
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
            if (ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
            {
                buildUtc = new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        return (rules, build, buildUtc);
    }

    /// <summary>"rules in force: MonsterLifeFromStopsAndAssigns, ..." for the header's version, "none" for 0, what a newer one is replayed under, and for an unstamped trace the estimate.</summary>
    public static string InForce(TraceHeader header, string path = null)
    {
        if (header.Source == TraceSource.Sniffer)
        {
            return "rules in force: none of ours, it is a real client (replayed under the latest)";
        }

        if (header.RulesVersion is not { } version)
        {
            var estimate = ReplayRules.Resolve(header, path);
            return $"replayed under rules {estimate.Rules.Version} ({Names(estimate.Rules)}), an estimate - {estimate.Basis}";
        }

        var rules = Core.ClientRules.ForVersion(version);
        return version > Core.ClientRules.LatestVersion
            ? $"rules in force: unknown to this build, replayed under {rules.Version} ({Names(rules)})"
            : $"rules in force: {Names(rules)}";
    }

    private static string Names(Core.ClientRules rules) => rules.Version == 0 ? "none" : string.Join(", ", rules.InForce);

    /// <summary>"build 5ada60b (2026-11-14 05:40 UTC), client rules 2", or what the file does not say.</summary>
    public static string Describe(TraceHeader header)
    {
        var build = header.Build == null
            ? "an unstamped build"
            : $"build {BuildIdentity.Short(header.Build)}{(BuildIdentity.IsDirty(header.Build) ? " (uncommitted changes)" : "")}{(header.BuildUtc is { } utc ? $" ({utc:yyyy-MM-dd HH:mm} UTC)" : "")}";
        var rules = header.Source == TraceSource.Sniffer || header.RulesVersion == TraceFormat.RulesNotApplicable
            ? "client rules not applicable (a real client)"
            : header.RulesVersion is { } version ? $"client rules {version}" : "rules not stamped";
        return $"{build}, {rules}";
    }
}
