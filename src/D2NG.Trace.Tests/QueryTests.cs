using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public sealed class ScenarioFixture : IDisposable
{
    private readonly TempRoot _root = new();

    public ScenarioFixture()
    {
        Folder = SyntheticGame.WriteScenario(_root.Path);
        Query = GameQuery.Open(Folder);
    }

    public string Root => _root.Path;

    public string Folder { get; }

    public GameQuery Query { get; }

    public void Dispose() => _root.Dispose();
}

public class QueryTests(ScenarioFixture scenario) : IClassFixture<ScenarioFixture>
{
    private const string Sorceress = SyntheticGame.Sorceress;
    private const string Amazon = SyntheticGame.Amazon;

    [Fact]
    public void TheGameFolderOpensWithItsClientsSidecarAndRealm()
    {
        var game = scenario.Query.Game;
        Assert.Equal(SyntheticGame.GameName, game.GameName);
        Assert.Equal([Sorceress, Amazon], game.Labels);
        Assert.Equal(Sorceress, game.ResolveClient("CLIENT1"));
        Assert.Throws<ArgumentException>(() => game.ResolveClient("client"));
        Assert.NotEmpty(scenario.Query.Sidecar);
        Assert.Equal(Sorceress, scenario.Query.Realm.Characters.Values.Single());
        Assert.Equal([scenario.Folder], GameTrace.GameFoldersUnder(scenario.Root));
    }

    [Fact]
    public void IncidentsFindWhatWentWrongAndNothingThatRecovered()
    {
        var incidents = Incidents.Find(scenario.Query);
        string Kinds(string client) => string.Join(",", incidents.Where(i => i.Client == client).Select(i => i.Kind));

        Assert.Equal("life drop,digest mismatch,server silent,chicken,step failed", Kinds(Sorceress));
        Assert.Equal("step failed,death,error,trace gap", Kinds(Amazon));

        var drop = incidents.First(i => i.Kind == "life drop" && i.Client == Sorceress);
        Assert.Contains("1000 -> 600 of 1000", drop.Detail);
        Assert.Contains("Zombie 0xE9 (attack 1 x3)", drop.Detail);
        var death = incidents.Single(i => i.Kind == "death");
        Assert.Contains("FoulCrow 0xE5", death.Detail);
        Assert.Contains("ColdPlains->StonyField", incidents.Single(i => i.Kind == "step failed" && i.Client == Amazon).Detail);
        Assert.DoesNotContain(incidents, i => i.Detail.Contains("BloodMoor->ColdPlains", StringComparison.Ordinal));
        Assert.Contains("Life live 999 replay 600", incidents.Single(i => i.Kind == "digest mismatch").Detail);
        Assert.Single(incidents, i => i.Kind == "server silent");

        var next = Incidents.NextCommand(scenario.Folder, drop);
        Assert.EndsWith($"--client {Sorceress} --from 00:00.000 --to 00:09.850", next);
    }

    [Fact]
    public void ARefusedCreateIsAnIncidentOfTheGameItNamed()
    {
        var answers = scenario.Query.Realm.Answers;
        var refused = answers.Single(a => a.Refused);
        Assert.Equal(SyntheticGame.RefusedGameName, refused.Game);
        Assert.Equal(0x1Fu, refused.Result);
        Assert.Equal(Sorceress, refused.Character);
        Assert.Contains("NAME_EXIST", Incidents.FromRealm([refused], scenario.Query.Game.StartUtc, scenario.Query.Game.Labels).Single().Detail);
        Assert.DoesNotContain(scenario.Query.Realm.AnswersFor(SyntheticGame.GameName), a => a.Refused);
    }

    [Fact]
    public void ACorpseAssignedWithNoDamageBeforeItIsNotADeath()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "20261113", "game-1");
        Directory.CreateDirectory(folder);
        var records = new List<TraceRecord> { new(TraceRecordKind.D2gsSent, TimeSpan.Zero, SyntheticTrace.Logon()) };
        records.AddRange(SyntheticTrace.Joining().Select(p => new TraceRecord(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(10), p)));
        records.Add(new TraceRecord(TraceRecordKind.D2gsReceived, TimeSpan.FromSeconds(1), SyntheticGame.CorpseAssign(1, 0x99)));
        TraceFile.Write(Path.Combine(folder, "client 1.d2trace"), new TraceHeader { StartUtc = DateTime.UtcNow, ClientLabel = "client 1", GameName = "game-1" }, records);

        var incidents = Incidents.Find(GameQuery.Open(folder));

        Assert.DoesNotContain(incidents, i => i.Kind == "death");
    }

    [Fact]
    public void TheTimelineMergesReplayEventsTypedEventsAndLogLines()
    {
        var entries = Timeline.Build(scenario.Query, new TimelineOptions());
        var lines = entries.Select(Timeline.Format).ToList();
        void Has(string client, string text) => Assert.Contains(entries, e => e.Client == client && e.Text.Contains(text, StringComparison.Ordinal));

        Has(Sorceress, "x3 Teleport");
        Has(Sorceress, "x3 reassigned by the server (5057, 1902) -> (5160, 2010)");
        Has(Sorceress, "area RogueEncampment -> BloodMoor");
        Has(Sorceress, "seen Zombie 0xE9");
        Has(Sorceress, "x14 FrozenOrb at (5166, 2012), Zombie 0xE9 over 3.3s");
        Has(Sorceress, "life 70% (700/1000), below 75%");
        Has(Sorceress, "Zombie 0xE9 died");
        Has(Sorceress, "target Zombie 0xE9 (nearest); passed over 0xF1 FoulCrow (farther than 40)");
        Has(Sorceress, "decision chicken");
        Has(Sorceress, "left the game");
        Has(Amazon, "cursor holds HoradricCube 0x40");
        Has(Amazon, "cursor empty (was HoradricCube 0x40)");
        Has(Amazon, "town portal 0x77 appeared");
        Has(Amazon, "town portal 0x77 belongs to client1");
        Has(Amazon, "quest DenOfEvil: 0x0000 -> 0x2012");
        Has(Amazon, "player client1 left");
        Has(Amazon, "DIED: corpse 0x99");
        Has(Amazon, "step cross ColdPlains->StonyField attempt 3 FAILED");
        Has(Amazon, "wait entered: taxi portal");
        Has("?", "INF Run pickups: nothing worth picking up");
        Has(Sorceress, "WRN Instance 1 game server was silent");
        Assert.DoesNotContain(entries, e => e.Text.StartsWith("DBG", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Text.Contains("seen FoulCrow 0xF1", StringComparison.Ordinal));
        Assert.Equal(entries.OrderBy(e => e.Offset).Select(e => e.Offset), entries.Select(e => e.Offset));
        Assert.All(lines, l => Assert.Matches(@"^\d\d:\d\d\.\d{3} ", l));
    }

    [Fact]
    public void TheTimelineNarrowsToAClientAndAWindow()
    {
        var entries = Timeline.Build(scenario.Query, new TimelineOptions { Client = Amazon, From = TimeSpan.FromSeconds(5), To = TimeSpan.FromSeconds(10), Debug = true });

        Assert.All(entries, e => Assert.InRange(e.Offset, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        Assert.All(entries, e => Assert.Contains(e.Client, new[] { Amazon, "?" }));
        Assert.Contains(entries, e => e.Client == "?");
        Assert.DoesNotContain(entries, e => e.Text.Contains("FrozenOrb", StringComparison.Ordinal));
    }

    [Fact]
    public void StateIsTheReplayedClientAtThatInstant()
    {
        var json = StateQuery.Render(scenario.Query, Sorceress, TimeSpan.FromSeconds(7.5), null);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(700, root.GetProperty("self").GetProperty("life").GetInt32());
        Assert.Equal(1000, root.GetProperty("self").GetProperty("maxLife").GetInt32());
        Assert.Equal("BloodMoor", root.GetProperty("self").GetProperty("area").GetString());
        Assert.Equal("FrozenOrb", root.GetProperty("self").GetProperty("rightSkill").GetString());
        var unit = root.GetProperty("units").EnumerateArray().Single();
        Assert.Equal("0xE9", unit.GetProperty("id").GetString());
        Assert.Equal("Zombie", unit.GetProperty("code").GetString());
        Assert.Equal(Amazon, root.GetProperty("players")[0].GetProperty("name").GetString());

        var narrow = System.Text.Json.JsonDocument.Parse(StateQuery.Render(scenario.Query, Sorceress, TimeSpan.FromSeconds(7.5), "units:5,quests"));
        Assert.Empty(narrow.RootElement.GetProperty("units").EnumerateArray());
        Assert.False(narrow.RootElement.TryGetProperty("self", out _));

        var cursor = System.Text.Json.JsonDocument.Parse(StateQuery.Render(scenario.Query, Amazon, TimeSpan.FromSeconds(3.2), "self"));
        Assert.Equal("HoradricCube 0x40", cursor.RootElement.GetProperty("self").GetProperty("cursor").GetString());
        Assert.Throws<ArgumentException>(() => StateQuery.ParseSelect("self,monsters"));
    }

    [Fact]
    public void PacketsAreDecodedAndFiltered()
    {
        var attacks = PacketQueries.Packets(scenario.Query, Sorceress, new PacketOptions { Types = [0x6C] }).ToList();
        Assert.Equal(3, attacks.Count);
        Assert.All(attacks, a => Assert.Contains("unit=0xE9 attack=1 target=player 0x1", a));

        var sent = PacketQueries.Packets(scenario.Query, Sorceress, new PacketOptions { Sent = true, From = TimeSpan.FromSeconds(6), To = TimeSpan.FromSeconds(6.6) }).ToList();
        Assert.Equal(["00:06.000 > RightSkillOnLocation   at=(5166, 2012)", "00:06.250 > RightSkillOnLocation   at=(5166, 2012)", "00:06.500 > RightSkillOnLocation   at=(5166, 2012)"], sent);

        var life = PacketQueries.Packets(scenario.Query, Sorceress, new PacketOptions { Types = [0x95], From = TimeSpan.FromSeconds(7.4), To = TimeSpan.FromSeconds(7.5) }).Single();
        Assert.Contains("life=700", life);

        var realm = PacketQueries.Packets(scenario.Query, Sorceress, new PacketOptions { Realm = true }).ToList();
        Assert.Contains(realm, l => l.Contains("MCP CREATEGAME", StringComparison.Ordinal) && l.Contains("result=0x1F", StringComparison.Ordinal));
    }

    [Fact]
    public void AUnitsHistoryShowsEachPacketAndTheStateGameMadeOfIt()
    {
        var found = PacketQueries.FindUnits(scenario.Query, Sorceress, "zomb");
        var zombie = Assert.Single(found);
        Assert.Equal(SyntheticGame.Zombie, zombie.Id);
        Assert.Equal(TimeSpan.FromSeconds(5), zombie.First);

        var history = new UnitHistory(EntityType.NPC, SyntheticGame.Zombie, TimeSpan.Zero, TimeSpan.MaxValue);
        ClientReplay.Run(scenario.Query.Game, Sorceress, TimeSpan.MaxValue, [history])?.Replayer.Dispose();
        var lines = history.Entries.SelectMany(e => new[] { e.Packet, e.State }).ToList();

        Assert.StartsWith("00:05.000 < AssignNPC2", lines[0]);
        var move = lines.FindIndex(l => l.Contains("< NPCMove", StringComparison.Ordinal));
        Assert.Contains("at (5166, 2012)", lines[move + 1]);
        Assert.Contains("took the move's destination", lines[move + 1]);
        Assert.Contains(lines, l => l.Contains("[attacker]", StringComparison.Ordinal));
        Assert.Contains("Dead", lines[^1]);
    }

    [Fact]
    public void DamageIsPutOnTheUnitsThatNamedTheClient()
    {
        var damage = new DamageObserver(TimeSpan.Zero, TimeSpan.MaxValue);
        ClientReplay.Run(scenario.Query.Game, Amazon, TimeSpan.MaxValue, [damage])?.Replayer.Dispose();

        Assert.Equal(1000, damage.TotalLost);
        var attacker = Assert.Single(damage.Attackers.Values);
        Assert.Equal("FoulCrow 0xE5", attacker.Name);
        Assert.Equal(DamageObserver.Named, attacker.Basis);
        Assert.Equal(1000, attacker.Lost);
        Assert.Contains(damage.Report(Amazon), l => l.StartsWith("attribution:", StringComparison.Ordinal));
    }

    [Fact]
    public void DamageNobodyWentForIsUnattributed()
    {
        var damage = new DamageObserver(TimeSpan.FromSeconds(12), TimeSpan.MaxValue);
        ClientReplay.Run(scenario.Query.Game, Amazon, TimeSpan.MaxValue, [damage])?.Replayer.Dispose();
        Assert.Equal(1000, damage.TotalLost);

        var none = new DamageObserver(TimeSpan.FromSeconds(30), TimeSpan.MaxValue);
        ClientReplay.Run(scenario.Query.Game, Amazon, TimeSpan.MaxValue, [none])?.Replayer.Dispose();
        Assert.Equal(0, none.TotalLost);
        Assert.Empty(none.Attackers);
    }
}

public class OutputTests
{
    [Fact]
    public void AListPastItsCapSaysHowMuchWasLeftOutAndHowToSeeIt()
    {
        var writer = new StringWriter();
        var output = new CappedOutput(writer, 3, "narrow with --from/--to");
        for (var i = 0; i < 5; i++)
        {
            output.Line($"line {i}");
        }

        output.Finish();
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["line 0", "line 1", "line 2", "... 2 more lines left out (limit 3); narrow with --from/--to, or --limit 0 for all"], lines);
    }

    [Fact]
    public void AListWithinItsCapSaysNothingMore()
    {
        var writer = new StringWriter();
        var output = new CappedOutput(writer, 0, "x");
        output.Line("only");
        output.Finish();
        Assert.Equal("only" + Environment.NewLine, writer.ToString());
    }

    [Theory]
    [InlineData("01:02.5", 62.5)]
    [InlineData("1:02", 62)]
    [InlineData("75:00.250", 4500.25)]
    [InlineData("1:00:00", 3600)]
    [InlineData("90", 90)]
    [InlineData("90.5s", 90.5)]
    [InlineData("2m", 120)]
    [InlineData("-5", -5)]
    public void TimesParseAsOffsets(string text, double seconds)
    {
        Assert.True(TraceTime.TryParse(text, out var offset));
        Assert.Equal(TimeSpan.FromSeconds(seconds), offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1:60:00")]
    [InlineData("1:2:3:4")]
    public void NonsenseIsNotATime(string text)
    {
        Assert.False(TraceTime.TryParse(text, out _));
        Assert.Throws<FormatException>(() => TraceTime.Parse(text));
    }

    [Fact]
    public void TimesFormatAsMinutesSecondsAndMilliseconds()
    {
        Assert.Equal("01:02.500", TraceTime.Format(TimeSpan.FromSeconds(62.5)));
        Assert.Equal("75:00.000", TraceTime.Format(TimeSpan.FromMinutes(75)));
        Assert.Equal("-00:05.000", TraceTime.Format(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void PacketTypesAndFieldsAreNamed()
    {
        Assert.Equal("NPCHit", PacketDecoder.Name(TraceRecordKind.D2gsReceived, [0x0C]));
        Assert.Equal("GameLogon", PacketDecoder.Name(TraceRecordKind.D2gsSent, SyntheticTrace.Logon()));
        Assert.Contains("character=nomancer", PacketDecoder.Fields(TraceRecordKind.D2gsSent, SyntheticTrace.Logon()));
        Assert.Equal("entityId=0xE9 entityType=NPC entityState=BeingHit lifePercentage=50", PacketDecoder.Fields(TraceRecordKind.D2gsReceived, SyntheticTrace.MonsterHit(0xE9, 64)));
        Assert.Equal("Zombie", Names.Npc(NPCCode.Zombie));
        Assert.Equal("FFFF", PacketDecoder.Fields(TraceRecordKind.D2gsReceived, [0xFF, 0xFF]));
    }
}
