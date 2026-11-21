using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TraceTool;

/// <summary>The self section of Trace accuracy: each client's own area and position against the server's, merged over the games.</summary>
internal static class SelfReport
{
    private sealed record Row(string Client, string Class, int Replays, double Seconds, double AreaUnknown, double Residual, double AreaDifferent,
        double TownWhileUnknown, double TownWhileOutside, double NotTownWhileInTown, double ServerAreaDifferent, double ServerDisagreesOnTown, int Moving, int[] MovingUnconfirmed,
        List<(D2NG.Core.D2GS.Packet.InComingPacket Packet, double Distance, bool Warp)> Corrections,
        Stretches InTownUnconfirmed, Stretches NotTownUnconfirmed, Stretches[] Unknown);

    private static List<Row> Rows(IEnumerable<SelfAccuracy> selves)
    {
        var rows = selves.Where(s => s.Client != null).GroupBy(s => s.Client).Select(g => Merge(g.Key, g.Select(s => s.Class).FirstOrDefault(c => c != null) ?? "?", [.. g])).OrderBy(r => r.Class).ThenBy(r => r.Client).ToList();
        if (rows.Count > 1)
        {
            rows.Add(Merge("all", "all", [.. selves.Where(s => s.Client != null)]));
        }

        return rows;
    }

    private static Row Merge(string client, string type, List<SelfAccuracy> selves)
        => new(client, type, selves.Count,
            selves.Sum(s => s.Seconds), selves.Sum(s => s.AreaUnknown), selves.Sum(s => s.Residual), selves.Sum(s => s.AreaDifferent),
            selves.Sum(s => s.TownWhileUnknown), selves.Sum(s => s.TownWhileOutside), selves.Sum(s => s.NotTownWhileInTown),
            selves.Sum(s => s.ServerAreaDifferent), selves.Sum(s => s.ServerDisagreesOnTown),
            selves.Sum(s => s.MovingSeconds),
            [.. Enumerable.Range(0, SelfAccuracy.UnconfirmedThresholds.Length).Select(i => selves.Sum(s => s.MovingUnconfirmedSeconds[i]))],
            [.. selves.SelectMany(s => s.Corrections)],
            Stretches.Merge(selves.Select(s => s.InTownUnconfirmed)), Stretches.Merge(selves.Select(s => s.NotTownUnconfirmed)),
            [.. Enum.GetValues<TownUnknownCause>().Select(c => Stretches.Merge(selves.Select(s => s.TownUnknown[(int)c])))]);

    public static void Print(TextWriter output, IReadOnlyList<SelfAccuracy> selves)
    {
        var rows = Rows(selves);
        output.WriteLine();
        output.WriteLine("Self: the client's own area and position against the server's (seconds in the game and placed).");
        output.WriteLine("  unknown = Game.ResolvedArea (the believed position's) unknown, Game.Area still names an area; residual = the server-confirmed");
        output.WriteLine("  position does not resolve either (Game.KnownArea unknown); different = Game.Area and ResolvedArea name different areas;");
        output.WriteLine("  town?: Game.IsInTown() true while the believed area is unknown; town!: IsInTown() true while IsOutOfTownConfirmed(); field!: false while IsInTownConfirmed();");
        output.WriteLine("  srv area = believed and server-confirmed positions resolve to different areas; srv town = they disagree about town (ServerDisagreesOnTown).");
        output.WriteLine($"  {"client",-14} {"class",-11} {"games",5} {"placed s",9} {"unknown s",10} {"residual",9} {"different",10} {"town? s",8} {"town! s",8} {"field! s",9} {"srv area",9} {"srv town",9}");
        foreach (var r in rows)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {r.Client,-14} {r.Class,-11} {r.Replays,5} {r.Seconds,9:0} {r.AreaUnknown,10:0.0} {r.Residual,9:0.0} {r.AreaDifferent,10:0.0} {r.TownWhileUnknown,8:0.0} {r.TownWhileOutside,8:0.0} {r.NotTownWhileInTown,9:0.0} {r.ServerAreaDifferent,9:0.0} {r.ServerDisagreesOnTown,9:0.0}"));
        }

        output.WriteLine();
        output.WriteLine("Town helpers against Game.IsInTown(): seconds / stretches / longest stretch (s). Confirmed means the server's position says so");
        output.WriteLine("and the believed position's tile does not contradict it; neither helper true is unknown, by cause.");
        var causes = Enum.GetValues<TownUnknownCause>();
        output.WriteLine($"  {"client",-14} {"class",-11} {"IsInTown && !InTownConf",24} {"!IsInTown && !OutConf",24} {"town! s",8} {"field! s",9}");
        foreach (var r in rows)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {r.Client,-14} {r.Class,-11} {Stretch(r.InTownUnconfirmed),24} {Stretch(r.NotTownUnconfirmed),24} {r.TownWhileOutside,8:0.0} {r.NotTownWhileInTown,9:0.0}"));
        }

        output.WriteLine($"  unknown (neither helper): {string.Join(", ", causes.Select(c => $"{c} = {CauseText(c)}"))}");
        output.WriteLine($"  {"client",-14} {"class",-11} {string.Join(" ", causes.Select(c => $"{c,22}"))}");
        foreach (var r in rows)
        {
            output.WriteLine($"  {r.Client,-14} {r.Class,-11} {string.Join(" ", r.Unknown.Select(u => $"{Stretch(u),22}"))}");
        }

        var firsts = selves.SelectMany(s => s.FirstStatementAfter).ToList();
        var times = firsts.Where(f => f != null).Select(f => f.Value.TotalSeconds).OrderBy(t => t).ToList();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  first own position from the server after a file's first packet: {times.Count} of {firsts.Count} files, median {Percentile(times, 0.5):0.000} / p90 {Percentile(times, 0.9):0.000} / max {(times.Count == 0 ? 0 : times[^1]):0.000} s"));
        output.WriteLine();
        output.WriteLine("  residual stretches of 0.5 s or more:");
        foreach (var self in selves.Where(s => s.ResidualEpisodes.Count > 0))
        {
            foreach (var (start, end, where) in self.ResidualEpisodes)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {self.Game} {self.Client,-14} {self.Class,-11} {TraceTime.Format(start)}-{TraceTime.Format(end)} ({(end - start).TotalSeconds:0.0}s) {where}"));
            }
        }

        output.WriteLine();
        output.WriteLine("Walking or running on the client's own word: seconds with a walk or run packet sent, and of those the seconds in which the");
        output.WriteLine("server's last statement of the position (0x15, 0x96, 0x95, 0x18, 0x0D, 0x0F) was older than 3, 5 and 10 s.");
        output.WriteLine($"  {"client",-14} {"class",-11} {"moving s",9} {string.Join(" ", SelfAccuracy.UnconfirmedThresholds.Select(t => $"{$">{t:0}s",8}"))}");
        foreach (var r in rows)
        {
            output.WriteLine($"  {r.Client,-14} {r.Class,-11} {r.Moving,9} {string.Join(" ", r.MovingUnconfirmed.Select(m => $"{m,8}"))}");
        }

        output.WriteLine();
        output.WriteLine("Where the client held itself against where the server then said it was, at each statement (subtiles, median / p90 / max).");
        output.WriteLine($"Statements within {SelfAccuracy.WarpExplains.TotalSeconds:0} s of a waypoint, a portal or stairs taken or an act load, or {SelfAccuracy.TeleportExplains.TotalSeconds:0} s of a Teleport cast, are where the warp put the character, and only counted (warps).");
        output.WriteLine($"  {"client",-14} {"class",-11} {"n",7} {"error",20} {"> 5",6} {"> 15",6} {"warps",6}");
        foreach (var r in rows)
        {
            CorrectionRow(output, $"{r.Client,-14} {r.Class,-11}", r.Corrections);
        }

        output.WriteLine("  by packet, every client:");
        foreach (var g in selves.SelectMany(s => s.Corrections).GroupBy(c => c.Packet).OrderByDescending(g => g.Count()))
        {
            CorrectionRow(output, $"{Names.Incoming((byte)g.Key),-26}", [.. g]);
        }
    }

    private static void CorrectionRow(TextWriter output, string key, List<(D2NG.Core.D2GS.Packet.InComingPacket Packet, double Distance, bool Warp)> corrections)
    {
        var drift = corrections.Where(c => !c.Warp).Select(c => c.Distance).ToList();
        output.WriteLine($"  {key} {drift.Count,7:N0} {Stats(drift),20} {drift.Count(d => d > 5),6} {drift.Count(d => d > 15),6} {corrections.Count - drift.Count,6}");
    }

    public static object Json(IReadOnlyList<SelfAccuracy> selves) => Rows(selves).Select(r =>
    {
        var errors = r.Corrections.Where(c => !c.Warp).Select(c => c.Distance).OrderBy(d => d).ToList();
        return new
        {
            client = r.Client,
            @class = r.Class,
            replays = r.Replays,
            placedSeconds = r.Seconds,
            areaUnknownSeconds = r.AreaUnknown,
            residualUnknownSeconds = r.Residual,
            serverAreaDifferentSeconds = r.ServerAreaDifferent,
            serverDisagreesOnTownSeconds = r.ServerDisagreesOnTown,
            areaDifferentSeconds = r.AreaDifferent,
            inTownWhileUnknownSeconds = r.TownWhileUnknown,
            inTownWhileOutsideSeconds = r.TownWhileOutside,
            notTownWhileInTownSeconds = r.NotTownWhileInTown,
            inTownUnconfirmed = new { seconds = r.InTownUnconfirmed.Seconds, stretches = r.InTownUnconfirmed.Count, longest = r.InTownUnconfirmed.Longest },
            notTownNotOutConfirmed = new { seconds = r.NotTownUnconfirmed.Seconds, stretches = r.NotTownUnconfirmed.Count, longest = r.NotTownUnconfirmed.Longest },
            townUnknown = Enum.GetValues<TownUnknownCause>().Select(c => new { cause = c.ToString(), seconds = r.Unknown[(int)c].Seconds, stretches = r.Unknown[(int)c].Count, longest = r.Unknown[(int)c].Longest }),
            movingSeconds = r.Moving,
            movingUnconfirmedSeconds = SelfAccuracy.UnconfirmedThresholds.Select((t, i) => new { olderThan = t, seconds = r.MovingUnconfirmed[i] }),
            corrections = new { n = errors.Count, warps = r.Corrections.Count - errors.Count, median = Percentile(errors, 0.5), p90 = Percentile(errors, 0.9), max = errors.Count == 0 ? 0 : errors[^1] },
        };
    }).ToList();

    private static string Stretch(Stretches s)
        => string.Create(CultureInfo.InvariantCulture, $"{s.Seconds:0.0} / {s.Count} / {s.Longest:0.000}");

    private static string CauseText(TownUnknownCause cause) => cause switch
    {
        TownUnknownCause.Join => "no server position yet since joining",
        TownUnknownCause.ActLoadHold => "act load's town held",
        TownUnknownCause.ActLoadNoStatement => "after an act load, believed on a tile, no server position yet",
        TownUnknownCause.Dispute => "server and believed positions disagree on town",
        TownUnknownCause.ServerOffMap => "server position on no revealed tile",
        _ => "other",
    };

    private static string Stats(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{Percentile(sorted, 0.5),5:F1} /{Percentile(sorted, 0.9),6:F1} /{sorted[^1],6:F0}");
    }

    private static double Percentile(List<double> sorted, double share)
        => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * share))];
}
