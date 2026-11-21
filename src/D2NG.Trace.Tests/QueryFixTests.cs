using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace D2NG.Trace.Tests;

public class GameWindowTests
{
    /// <summary>Two games of one process: the first ends at 60 s, the next starts 5 s later; a TIMING line logged just before the first ended.</summary>
    private static (string First, string Second) TwoGamesOfOneProcess(TempRoot root)
    {
        var first = Game1.Folder(root, "partyA1");
        var second = Game1.Folder(root, "partyB1");
        new HandTrace("client1", "partyA1", Game1.Start).Join(1).Ticks(1, 60).WriteTo(first);
        new HandTrace("client1", "partyB1", Game1.Start.AddSeconds(65), serverGame: null).Join(1, token: 0x0203).Ticks(1, 60).WriteTo(second);
        new SidecarFile(Game1.Start)
            .Typed(2, "move", "client1", "partyA1", ("kind", "walk"), ("outcome", "arrived"))
            .Log(59.5, LogEventLevel.Information, "TIMING step {Step} {Outcome} in {Seconds:0}s", ("Step", "Mephisto"), ("Outcome", "FAILED"), ("Seconds", 50.0))
            .Typed(67, "move", "client1", "partyB1", ("kind", "walk"), ("outcome", "arrived"))
            .WriteTo(Game1.Date(root), "115900");
        return (first, second);
    }

    [Fact]
    public void ALineOfAProcessBelongsToOneGameOnly()
    {
        using var root = new TempRoot();
        var (first, second) = TwoGamesOfOneProcess(root);

        var firstSteps = Steps.Of(GameQuery.Open(first));
        var secondEntries = GameQuery.Open(second).GameEntries();

        Assert.Equal("failed", Assert.Single(firstSteps, s => s.Name == "Mephisto").Outcome);
        Assert.DoesNotContain(secondEntries, e => e.IsLog && e.Message.Contains("TIMING", StringComparison.Ordinal));
        Assert.Empty(Steps.Of(GameQuery.Open(second)));
    }

    [Fact]
    public void AStepThatEndedBeforeTheGameStartedIsNotOneOfItsSteps()
    {
        var before = new StepSpan(null, "Mephisto", 1, TimeSpan.FromSeconds(-430), TimeSpan.FromSeconds(-4.9), "failed", "log");
        var within = new StepSpan(null, "Mephisto", 1, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(3), "ok", "log");
        var instant = new StepSpan("c", "cross", 1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), "ok", "typed");

        Assert.False(Steps.Overlaps(before, TimeSpan.FromSeconds(60)));
        Assert.True(Steps.Overlaps(within, TimeSpan.FromSeconds(60)));
        Assert.True(Steps.Overlaps(instant, TimeSpan.FromSeconds(60)));
    }
}

public class HistoryTests
{
    [Fact]
    public void TheAnswerDoesNotDependOnWhatRanBeforeOrOnTheCache()
    {
        using var root = new TempRoot();
        var folder = BotCheckTests.GamesOfOneBot(root, 10, 10, "{Character} cannot find {Npc} in town", (s, at) => s
            .Log(at + 4, LogEventLevel.Warning, "{Character} lost the party", ("Character", "client1")));
        var cache = Path.Combine(root.Path, "cache", TraceHistory.FileName);
        List<string> Run(TraceHistory history)
        {
            var found = Game1.Incidents(folder, history, checks: "bot").Found.Where(i => i.Kind == "new warning").Select(i => i.Detail).ToList();
            history.Save();
            return found;
        }

        var fresh = Run(TraceHistory.Load(cache));
        var again = Run(TraceHistory.Load(cache));
        foreach (var other in Directory.GetDirectories(Game1.Date(root)))
        {
            Game1.Incidents(other, TraceHistory.Load(cache), checks: "bot");
        }

        var afterTheDay = Run(TraceHistory.Load(cache));
        File.Delete(cache);
        var deleted = Run(TraceHistory.Load(cache));

        Assert.Single(fresh);
        Assert.Equal(fresh, again);
        Assert.Equal(fresh, afterTheDay);
        Assert.Equal(fresh, deleted);
    }

    [Fact]
    public void ACorruptCacheIsIgnoredAndLeftAlone()
    {
        using var root = new TempRoot();
        var folder = BotCheckTests.GamesOfOneBot(root, 10, 10, "{Character} cannot find {Npc} in town", (s, at) => s
            .Log(at + 4, LogEventLevel.Warning, "{Character} lost the party", ("Character", "client1")));
        var cache = Path.Combine(root.Path, TraceHistory.FileName);
        File.WriteAllText(cache, "{\"Version\":2,\"Games\":{\"party1@");

        var history = TraceHistory.Load(cache);
        var found = Game1.Incidents(folder, history, checks: "bot").Found;
        history.Save();

        Assert.NotNull(history.Problem);
        Assert.Single(found, i => i.Kind == "new warning");
        Assert.Equal("{\"Version\":2,\"Games\":{\"party1@", File.ReadAllText(cache));
    }

    [Fact]
    public void FewerThanTenEarlierGamesSkipTheComparingChecksWithANote()
    {
        using var root = new TempRoot();
        var folder = BotCheckTests.GamesOfOneBot(root, 9, 10, "{Character} cannot find {Npc} in town", (s, at) => s
            .Log(at + 4, LogEventLevel.Warning, "{Character} lost the party", ("Character", "client1")));

        var (found, notes) = Game1.Incidents(folder, checks: "bot");

        Assert.DoesNotContain(found, i => i.Kind is "new warning" or "slow step");
        Assert.Contains(notes, n => n.Contains("skipped for lack of history", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultCacheIsPerUserAndNotBesideTheTraces()
    {
        var path = TraceHistory.DefaultPath();

        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), path, StringComparison.OrdinalIgnoreCase);
    }
}

public class SeenTypeTests
{
    [Fact]
    public void NpcAndObjectSightingsOfOneNameAreCountedApartAndAPrefixPicksOne()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(2, SyntheticGame.AssignObject(0x77, EntityCode.CompellingOrb, 5062, 1906))
            .Received(3, SyntheticGame.AssignNpc(0x78, NPCCode.CompellingOrb, 5062, 1906))
            .WriteTo(folder);
        List<string> Types(string code)
        {
            var seen = new SeenObserver(code, null);
            ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [seen])?.Replayer.Dispose();
            return [.. seen.Sightings.Select(s => s.Type)];
        }

        Assert.Equal(["object", "npc"], Types("CompellingOrb"));
        Assert.Equal(["npc"], Types("npc:CompellingOrb"));
        Assert.Equal(["object"], Types("object:compellingorb"));
    }
}

public class CursorAndUnitTests
{
    [Fact]
    public void AttacksWhileTheCursorIsHeldAreNotCountedAsUnansweredCursorPackets()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(0.5, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5070, 1910))
            .Received(1, SyntheticGame.Item(Core.D2GS.Items.Action.ToCursor, 0x40));
        for (var i = 0; i < 4; i++)
        {
            file.Sent(2 + i * 3.4, Packets.Insert(0x40));
            for (var j = 0; j < 5; j++)
            {
                file.Sent(2.2 + i * 3.4 + j * 0.3, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30));
            }
        }

        file.WriteTo(folder);

        var stuck = Assert.Single(Game1.Replayed(folder, "client1", new StuckCursorCheck()));
        Assert.Contains("4 cursor packets went unanswered", stuck.Detail);
        Assert.Contains("(InsertItemToBuffer x4)", stuck.Detail);
        Assert.Contains("kept attacking", stuck.Detail);
        Assert.DoesNotContain("RightSkillOnUnit", stuck.Detail);
    }

    private static HandTrace Watcher(string label, int attacks)
    {
        var file = new HandTrace(label, "party1", Game1.Start).Join(label == "client1" ? 1u : 2u)
            .Received(1, SyntheticGame.AssignNpc(0x30, NPCCode.WaterWatcherLimb, 5070, 1910))
            .Received(2, SyntheticGame.SkillOnPoint(0x30, Skill.Submerge, 5070, 1910));
        for (var i = 0; i < attacks; i++)
        {
            file.Sent(3 + i, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30));
        }

        return file.Ticks(6, 45, 5057, 1902);
    }

    [Fact]
    public void AttackingASubmergedUnitIsItsOwnKindAndTwoClientsMakeOneLine()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Watcher("client1", 3).WriteTo(folder);
        Watcher("client2", 4).WriteTo(folder);

        var found = Game1.Incidents(folder, checks: "state").Found.Where(i => i.Kind is StaleUnitCheck.SubmergedKind or "stale unit").ToList();

        var line = Assert.Single(found);
        Assert.Equal(StaleUnitCheck.SubmergedKind, line.Kind);
        Assert.Equal("client1+client2", line.Client);
        Assert.Contains("went under (Submerge)", line.Detail);
        Assert.Contains("7 packets sent at it by client1 3, client2 4", line.Detail);
    }

    [Fact]
    public void FewerThanThreePacketsAtASilentUnitAreNotReported()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Watcher("client1", 2).WriteTo(folder);

        Assert.Empty(Game1.Replayed(folder, "client1", new StaleUnitCheck()));
    }
}

public class HotLoopAndGoldTests
{
    private static string Lines(TempRoot root, string template, int count)
    {
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).Ticks(1, 30, 5057, 1902).WriteTo(folder);
        var sidecar = new SidecarFile(Game1.Start).Typed(0.5, "move", "client1", "party1", ("kind", "walk"), ("outcome", "arrived"));
        for (var i = 0; i < count; i++)
        {
            sidecar.Log(5 + i * 0.015, LogEventLevel.Information, template, ("Sale", "{}"), ("A", i), ("B", i));
        }

        sidecar.WriteTo(Game1.Date(root), "115900");
        return folder;
    }

    [Fact]
    public void SellingAnInventoryIsNotAHotLoopAndBackingUpIs()
    {
        using var selling = new TempRoot();
        Assert.DoesNotContain(Game1.Incidents(Lines(selling, "Sold item {Sale:l}", 30), checks: "bot").Found, i => i.Kind == "hot loop");
        using var backing = new TempRoot();
        Assert.Single(Game1.Incidents(Lines(backing, "Backing up to point {A} from {B}", 30), checks: "bot").Found, i => i.Kind == "hot loop");
    }

    private static byte[] Transaction(uint item, int goldAfter)
        => [0x2A, 0x00, 0x01, 0, 0, 0, 0, .. BitConverter.GetBytes(item), .. BitConverter.GetBytes(goldAfter)];

    private static byte[] Buy(uint npc, uint item) => [0x32, .. BitConverter.GetBytes(npc), .. BitConverter.GetBytes(item), 0, 0, 0, 0, .. BitConverter.GetBytes(7000)];

    [Fact]
    public void GoldPaidToAVendorExplainsAGoldDrop()
    {
        using var root = new TempRoot();
        string Write(string name, bool paid)
        {
            var folder = Game1.Folder(root, name);
            var file = new HandTrace("client1", "party1", Game1.Start).Join(1)
                .Received(0.5, [0x1E, 0x0E, .. BitConverter.GetBytes((ushort)10000)])
                .Ticks(1, 4, 5057, 1902);
            if (paid)
            {
                file.Sent(4.5, Buy(0x10, 0x99)).Received(4.6, Transaction(0x99, 3000));
            }

            file.Received(5, [0x1E, 0x0E, .. BitConverter.GetBytes((ushort)3000)])
                .Ticks(6, 8, 5057, 1902)
                .OutOfTown(9)
                .Ticks(10, 12)
                .WriteTo(folder);
            return folder;
        }

        Assert.Empty(Game1.Replayed(Write("bought", true), "client1", new GoldDropCheck()));
        var unexplained = Assert.Single(Game1.Replayed(Write("lost", false), "client1", new GoldDropCheck()));
        Assert.Contains("no vendor was paid", unexplained.Detail);
    }
}

public class MapSourceTests
{
    private static string Map(int width, int height, int value = 0)
    {
        var rows = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat(value, width).ToArray()).ToArray();
        return JsonSerializer.Serialize(new { levelOrigin = new { x = 100, y = 200 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new { } });
    }

    [Fact]
    public void OnlyAFewAreaGridsStayInMemoryAndAGridIsThreeBitsASubtile()
    {
        var source = new MapSource("http://map.invalid", null, _ => Map(400, 400));
        var areas = Enum.GetValues<Area>().Where(a => a != Area.None).Take(MapSource.KeptGrids + 4).ToList();
        foreach (var area in areas)
        {
            source.Grid(1, Difficulty.Normal, area);
        }

        var parsed = source.Parsed;
        source.Grid(1, Difficulty.Normal, areas[^1]);
        Assert.Equal(parsed, source.Parsed);
        source.Grid(1, Difficulty.Normal, areas[0]);
        Assert.Equal(parsed + 1, source.Parsed);
        Assert.True(source.Grid(1, Difficulty.Normal, areas[0]).Bytes <= (400 * 400 * 3 / 8) + 24);
    }

    [Fact]
    public void ACorruptCachedMapIsFetchedAgainAndReplaced()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var file = Path.Combine(root.Path, $"map-{7:X8}-{Difficulty.Normal}-{(int)Area.BloodMoor}.json");
        File.WriteAllText(file, "{\"levelOrigin\":");
        var fetches = 0;
        var source = new MapSource("http://map.invalid", root.Path, _ =>
        {
            fetches++;
            return Map(10, 10);
        });

        Assert.Equal('.', source.Grid(7, Difficulty.Normal, Area.BloodMoor).Ground(101, 201));
        Assert.Equal(1, fetches);
        Assert.Equal(Map(10, 10), File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public void AnErrorAnswerForOneAreaLeavesTheOtherAreasAvailable()
    {
        var source = new MapSource("http://map.invalid", null, url => url.Contains($"area={(int)Area.ColdPlains}", StringComparison.Ordinal)
            ? throw new System.Net.Http.HttpRequestException("HTTP 500", null, System.Net.HttpStatusCode.InternalServerError)
            : Map(10, 10));

        var refused = Assert.Throws<MapUnavailableException>(() => source.Grid(7, Difficulty.Normal, Area.ColdPlains));
        Assert.False(refused.WholeService);
        Assert.Equal('.', source.Grid(7, Difficulty.Normal, Area.BloodMoor).Ground(101, 201));
    }
}

public class GameFolderClaimTests
{
    [Fact]
    public void TheSameIdJoinsItsFolderAndALaterReuseOfTheIdGetsANewOne()
    {
        using var root = new TempRoot();
        var game = Path.Combine(root.Path, "party1");
        var now = new DateTime(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);

        var first = TraceChannel.GameDirectory(game, "10.0.0.1#5", now);
        var rejoin = TraceChannel.GameDirectory(game, "10.0.0.1#5", now.AddMinutes(30));
        var reused = TraceChannel.GameDirectory(game, "10.0.0.1#5", now.AddHours(4));

        Assert.Equal(game, first);
        Assert.Equal(game, rejoin);
        Assert.Equal(game + "~2", reused);
    }

    [Fact]
    public void AMarkStillBeingWrittenByAnotherProcessIsReadOnceItIsThere()
    {
        using var root = new TempRoot();
        var game = Path.Combine(root.Path, "party1");
        Directory.CreateDirectory(game);
        var mark = Path.Combine(game, TraceChannel.ServerGameFile);
        var claim = new FileStream(mark, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        var writer = new Thread(() =>
        {
            Thread.Sleep(150);
            claim.Write(System.Text.Encoding.UTF8.GetBytes("10.0.0.1#5\n" + DateTime.UtcNow.ToString("O") + "\n"));
            claim.Dispose();
        });
        writer.Start();

        var folder = TraceChannel.GameDirectory(game, "10.0.0.1#5");
        writer.Join();

        Assert.Equal(game, folder);
    }

    [Fact]
    public void AnEarlierGameWithoutAMarkAndAFolderThatCannotBeMadeDoNotCostTheTrace()
    {
        using var root = new TempRoot();
        var earlier = Path.Combine(root.Path, "party1");
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(earlier, "client1" + TraceFormat.Extension), "x");
        var blocked = Path.Combine(root.Path, "party2");
        File.WriteAllText(blocked, "a file where the folder should be");

        Assert.Equal(earlier + "~2", TraceChannel.GameDirectory(earlier, "10.0.0.1#5"));
        var own = TraceChannel.GameDirectory(blocked, "10.0.0.1#6");
        Assert.StartsWith(blocked + "~", own);
        Assert.True(Directory.Exists(own));
    }
}

public class EventClientTests
{
    [Fact]
    public void PartyAndCombinedEventsAreShownForEachOfTheirClients()
    {
        string[] labels = ["a", "b", "c"];

        Assert.Equal(["a", "b", "c"], EventFilter.ClientsOf(null, labels).Clients);
        Assert.Equal(["a", "c"], EventFilter.ClientsOf("a+c", labels).Clients);
        var (clients, notInGame) = EventFilter.ClientsOf("a+x", labels);
        Assert.Equal(["a"], clients);
        Assert.Equal(["x"], notInGame);
    }
}

public class StuckWithSparseStepsTests
{
    [Fact]
    public void AnInferredZeroLengthStepDoesNotHideAnIdleStretch()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1).OutOfTown(1).Ticks(2, 50).WriteTo(folder);
        new SidecarFile(Game1.Start)
            .Typed(1.5, "step", "client1", "party1", ("name", "cross A->B"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"))
            .WriteTo(Game1.Date(root), "115900");

        var stuck = Assert.Single(Game1.Incidents(folder, checks: "bot").Found, i => i.Kind == "stuck");

        Assert.DoesNotContain("during step", stuck.Detail);
    }
}

public class MidnightTests
{
    [Fact]
    public void ARejoinAfterMidnightIsPartOfTheGameThatStartedBeforeIt()
    {
        using var root = new TempRoot();
        var start = new DateTime(2026, 11, 13, 23, 50, 0, DateTimeKind.Local).ToUniversalTime();
        var rejoin = new DateTime(2026, 11, 14, 0, 2, 0, DateTimeKind.Local).ToUniversalTime();
        var before = Path.Combine(HandTrace.DateFolder(root.Path, start), "party1");
        var after = Path.Combine(HandTrace.DateFolder(root.Path, rejoin), "party1");
        new HandTrace("client1", "party1", start, "10.0.0.1#5").Join(1).Ticks(1, 690).WriteTo(before);
        new HandTrace("client1", "party1", rejoin, "10.0.0.1#5").Join(1).Ticks(1, 60).WriteTo(after);

        var game = Assert.Single(GameTrace.OpenAll(before));
        Assert.Equal(2, game.Files.Count);
        Assert.Equal(2, game.FilesOf("client1").Last().Part);
        Assert.Empty(GameTrace.GamesUnder(HandTrace.DateFolder(root.Path, rejoin)));
        Assert.Single(GameTrace.GamesUnder(root.Path));
    }
}

public class PartyTests
{
    [Fact]
    public void GamesAreGroupedByTheirSetOfClients()
    {
        using var root = new TempRoot();
        new HandTrace("client1", "lvl1", Game1.Start).Join(1).Ticks(1, 5).WriteTo(Game1.Folder(root, "lvl1"));
        new HandTrace("client2", "lvl1", Game1.Start).Join(2).Ticks(1, 5).WriteTo(Game1.Folder(root, "lvl1"));
        new HandTrace("runner", "ta1", Game1.Start.AddSeconds(30)).Join(1).Ticks(1, 5).WriteTo(Game1.Folder(root, "ta1"));
        new HandTrace("client2", "lvl2", Game1.Start.AddSeconds(60)).Join(2).Ticks(1, 5).WriteTo(Game1.Folder(root, "lvl2"));
        new HandTrace("client1", "lvl2", Game1.Start.AddSeconds(60)).Join(1).Ticks(1, 5).WriteTo(Game1.Folder(root, "lvl2"));

        var parties = Parties.Of(GameTrace.GamesUnder(Game1.Date(root)));

        Assert.Equal(["client1, client2", "runner"], parties.Select(p => p.Clients));
        Assert.Equal(["lvl1", "lvl2"], parties[0].Games.Select(g => g.GameName));
    }
}

public class TownAndSkillTests
{
    private static byte[] Chat(byte type, uint npc) => [type, .. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(npc)];

    [Fact]
    public void TownStaysSayWhyTheyBeganAndWhereTheirTimeWent()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(0.2, SyntheticGame.AssignNpc(0x10, NPCCode.Akara, 5060, 1905))
            .Received(0.2, SyntheticGame.AssignNpc(0x11, NPCCode.Charsi, 5070, 1905))
            .Sent(2, Chat(0x2F, 0x10)).Sent(4, Chat(0x30, 0x10))
            .Sent(6, Chat(0x2F, 0x11)).Sent(7, [0x32, .. BitConverter.GetBytes(0x11u), .. BitConverter.GetBytes(0x99u), 0, 0, 0, 0, 0, 0, 0, 0]).Sent(9, Chat(0x30, 0x11))
            .Ticks(10, 14, 5057, 1902)
            .OutOfTown(15)
            .Ticks(16, 30)
            .Received(30.5, SyntheticGame.AssignObject(0x50, EntityCode.TownPortal, 5162, 2011))
            .Sent(31, SyntheticGame.Unit(0x13, EntityType.Object, 0x50))
            .Received(32, Packets.MapReveal(5057, 1902, Area.RogueEncampment)).Received(32.01, SyntheticGame.Reassign(1, 5057, 1902))
            .Ticks(33, 45, 5057, 1902)
            .WriteTo(folder);
        new HandTrace("client1", "party1", Game1.Start.AddSeconds(50)).Join(1).Ticks(1, 10, 5057, 1902).WriteTo(folder, "client1-2");
        new SidecarFile(Game1.Start)
            .Typed(1, "move", "client1", "party1", ("kind", "walk"), ("outcome", "arrived"))
            .Typed(44, "decision", "client1", "party1", ("name", "chicken"))
            .WriteTo(Game1.Date(root), "115900");
        var query = GameQuery.Open(folder);
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Town: new TownObserver()), x => [x.Activity, x.Town])[0];

        var stays = TownStays.Of("client1", o.Activity, o.Town, [], [], TownStays.Chickens(query, "client1"), end);

        Assert.Equal(["game start", "portal back", "rejoin after chicken"], stays.Select(s => s.Cause));
        var start = stays[0];
        Assert.True(start.Split.GetValueOrDefault("healing") >= TimeSpan.FromSeconds(1.5));
        Assert.True(start.Split.GetValueOrDefault("vendor") >= TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, start.Trades);
        var summary = new TownSummary();
        summary.Add(stays);
        Assert.Contains("portal back 1x", summary.Causes());
    }

    [Fact]
    public void FightingTimeIsSplitBySkillWithHitsAndLowMana()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var file = new HandTrace("client1", "party1", Game1.Start).Join(1, characterClass: CharacterClass.Paladin)
            .Received(0.1, [0x1F, 0x09, .. BitConverter.GetBytes(100 << 8)])
            .Received(0.2, SyntheticGame.AssignNpc(0x30, NPCCode.Zombie, 5070, 1910))
            .OutOfTown(0.5, 5060, 1905)
            .Received(1, SyntheticGame.SetSkill(1, Hand.Right, Skill.BlessedHammer))
            .Received(1.1, SyntheticGame.LifeMana(1000, 100, 5060, 1905));
        for (var at = 2.0; at < 6; at += 0.5)
        {
            file.Sent(at, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30)).Received(at + 0.2, SyntheticGame.NpcHit(0x30, 80));
        }

        file.Received(6, SyntheticGame.LifeMana(1000, 5, 5060, 1905)).Received(6.1, SyntheticGame.SetSkill(1, Hand.Right, Skill.Attack));
        for (var at = 6.5; at < 12; at += 0.5)
        {
            file.Sent(at, SyntheticGame.Unit(0x0D, EntityType.NPC, 0x30));
        }

        file.Ticks(12, 14, 5060, 1905).WriteTo(folder);
        var query = GameQuery.Open(folder);
        var (_, o) = query.ReplayAll(["client1"], TimeSpan.MaxValue, () => (Activity: new ActivityObserver(), Fights: new FightObserver()), x => [x.Activity, x.Fights])[0];
        var end = query.Game.EndUtc - query.Game.StartUtc;

        var skills = new SkillSplit();
        skills.Add(o.Fights, o.Activity.Stretches(TimeSpan.Zero, end, []), TimeSpan.Zero, end);

        Assert.Equal(8, skills.Skills["BlessedHammer"].Casts);
        Assert.Equal(8, skills.Skills["BlessedHammer"].Hits);
        Assert.Equal(11, skills.Skills["Attack"].Casts);
        Assert.Equal(0, skills.Skills["Attack"].Hits);
        Assert.True(skills.Skills["Attack"].Time > skills.Skills["BlessedHammer"].Time);
        Assert.True(skills.Skills["Attack"].LowMana >= TimeSpan.FromSeconds(5));
        Assert.True(skills.Skills["BlessedHammer"].LowMana <= TimeSpan.FromSeconds(0.5));
        Assert.StartsWith("Attack", skills.Short());
    }
}
