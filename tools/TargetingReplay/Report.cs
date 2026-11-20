using ConsoleBot.Attack;
using D2NG.Trace.Query;

namespace TargetingReplay;

internal static class Report
{
    public static void Day(string folder, GameResult[] results, TimeSpan elapsed, int worst)
    {
        var skipped = results.Where(r => r.Skipped != null).GroupBy(r => r.Skipped).Select(g => $"{g.Count()} {g.Key}").ToList();
        Console.WriteLine($"== {folder}: {results.Count(r => r.Skipped == null)} games{(skipped.Count > 0 ? $" ({string.Join(", ", skipped)} skipped)" : "")}, read in {elapsed.TotalSeconds:0.0}s");
        Console.WriteLine("  server's own figure per arm, no lane test (skill moves that moved under 3 with no hit of his own, and the 'skill move went nowhere' incidents):");
        foreach (var arm in results.Where(r => r.Skipped == null && r.Whirls > 0).GroupBy(r => r.Arm ?? "not recorded").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var whirls = arm.Sum(r => r.Whirls);
            Console.WriteLine($"    wallAwareWhirl={arm.Key}: {arm.Count()} games, {whirls} whirls; went nowhere {arm.Sum(r => r.NowhereWhirls)} ({Per1000(arm.Sum(r => r.NowhereWhirls), whirls)} per 1000 whirls); "
                + $"{arm.Sum(r => r.NowhereIncidents)} incidents spanning {arm.Sum(r => r.NowhereSeconds):0}s ({Per1000(arm.Sum(r => r.NowhereSeconds), whirls)}s per 1000 whirls)");
        }

        var rows = results.SelectMany(r => r.Rows).ToList();
        foreach (var group in rows.GroupBy(r => (r.Model, r.Arm)).OrderBy(g => g.Key.Model == "server").ThenBy(g => g.Key.Model, StringComparer.Ordinal).ThenBy(g => g.Key.Arm, StringComparer.Ordinal))
        {
            var mine = group.ToList();
            Console.WriteLine($"  monster positions {group.Key.Model}{(group.Key.Arm == null ? "" : $", games with wallAwareWhirl={group.Key.Arm}")}: {mine.Count} whirls in {mine.Select(r => r.Game).Distinct().Count()} games");
            Console.WriteLine($"    server outcome: {string.Join(", ", Enum.GetValues<Outcome>().Select(o => $"{Name(o)} {mine.Count(r => r.Outcome == o)}"))}");
            var nowhere = mine.Where(r => r.Outcome == Outcome.WentNowhere).ToList();
            var others = mine.Where(r => r.Outcome != Outcome.WentNowhere).ToList();
            Console.WriteLine($"    lane test against the server: lane blocked & went nowhere {nowhere.Count(r => r.LaneBlocked)}, lane blocked & did not {others.Count(r => r.LaneBlocked)}, "
                + $"lane open & went nowhere {nowhere.Count(r => !r.LaneBlocked)}, lane open & did not {others.Count(r => !r.LaneBlocked)}");
            Console.WriteLine($"    planner's choice: {string.Join(", ", mine.GroupBy(r => r.Decision.Choice).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
            var avoided = nowhere.Where(r => r.Avoided).ToList();
            Console.WriteLine($"    (a) whirls that went nowhere (server), avoided: {avoided.Count} of {nowhere.Count} ({Share(avoided.Count, nowhere.Count)}) - {string.Join(", ", avoided.GroupBy(r => r.Decision.Choice).Select(g => $"{g.Key} {g.Count()}"))}");
            var blind = mine.Where(r => r.Outcome is Outcome.WentNowhere or Outcome.MovedHitNothing && !r.LaneBlocked).ToList();
            Console.WriteLine($"        blind spot: {blind.Count} whirls went nowhere or hit nothing with the lane reading open; the planner still whirls at {blind.Count(r => r.Casts)} of them "
                + $"({blind.Count(r => r.Outcome == Outcome.WentNowhere && r.Casts)} that went nowhere)");
            var hits = mine.Count(r => r.Outcome == Outcome.Hit);
            var rejected = mine.Where(r => r.Rejected).ToList();
            Console.WriteLine($"    (b) whirls that really hit (moved 3 or more, hits his own), rejected for a wall: {rejected.Count} of {hits} ({Share(rejected.Count, hits)}) - {string.Join(", ", rejected.GroupBy(r => r.Decision.Choice).Select(g => $"{g.Key} {g.Count()}"))}; "
                + $"put off to walk closer first: {mine.Count(r => r.Deferred)}; aimed elsewhere: {mine.Count(r => r.Outcome == Outcome.Hit && r.Decision.Choice == WhirlChoice.DifferentWhirl)}; party, hits not attributable: {mine.Count(r => r.Outcome == Outcome.PartyUnattributed)}");
            Console.WriteLine($"    (c) wall stall time saved, estimated: {avoided.Sum(r => r.StallSeconds):0}s over {avoided.Select(r => r.Game).Distinct().Count()} games "
                + $"(each avoided whirl that went nowhere: its last ask to the next whirl's last ask, at most {Counterfactual.StallCap.TotalSeconds:0}s after it ends, less {Counterfactual.AlternativeCost.TotalSeconds:0.0}s for the walk instead)");
            foreach (var game in mine.GroupBy(r => r.Game).Where(g => g.Any(r => r.Outcome == Outcome.WentNowhere || r.Rejected)).OrderByDescending(g => g.Count(r => r.Outcome == Outcome.WentNowhere)).Take(8))
            {
                Console.WriteLine($"      {game.Key}: {game.Count()} whirls, {game.Count(r => r.Outcome == Outcome.WentNowhere)} went nowhere ({game.Count(r => r.Avoided)} avoided, {game.Where(r => r.Avoided).Sum(r => r.StallSeconds):0}s), {game.Count(r => r.Rejected)} false blocks");
            }

            if (rejected.Count > 0)
            {
                Console.WriteLine($"    worst false blocks (most monsters hit), each exportable with Trace export <game> --client <client> --at <t> --out <file>:");
                foreach (var r in rejected.OrderByDescending(r => r.Hits).ThenBy(r => r.Game, StringComparer.Ordinal).ThenBy(r => r.At).Take(worst))
                {
                    Console.WriteLine($"      {r.Game} --client {r.Client} --at {TraceTime.Format(r.At)}: hit {r.Hits}, moved {r.Moved:0.0} from {Names.Point(r.Me)} toward {Names.Point(r.Asked)}; planner: {r.Decision.Choice}{(r.Decision.WalkTo is { } walk ? $" via {Names.Point(walk)}" : "")}{(r.Decision.Target is { } t ? $" to {Names.Point(t)}" : "")}");
                }
            }
        }
    }

    private static string Name(Outcome outcome) => outcome switch
    {
        Outcome.Hit => "hit",
        Outcome.WentNowhere => "went nowhere",
        Outcome.MovedHitNothing => "moved but hit nothing",
        Outcome.HitInPlace => "hit without moving",
        _ => "party, hits not attributable",
    };

    private static string Share(int part, int whole) => whole == 0 ? "-" : $"{(double)part / whole:P0}";

    private static string Per1000(double part, int whole) => whole == 0 ? "-" : $"{part * 1000 / whole:0.0}";
}
