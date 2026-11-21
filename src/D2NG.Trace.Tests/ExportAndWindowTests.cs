using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Trace.Query;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>A recorded moment exported as a fixture, and a step's end that comes after its game's last packet.</summary>
public class ExportAndWindowTests
{
    [Fact]
    public void ARunEndedAfterTheGamesLastPacketButBeforeTheNextGameIsStillThisGamesEnd()
    {
        using var root = new TempRoot();
        var date = Game1.Date(root);
        new HandTrace("client1", "game1", Game1.Start).Join(1).Ticks(1, 3, 5057, 1902).WriteTo(Path.Combine(date, "game1"));
        new HandTrace("client1", "game2", Game1.Start.AddSeconds(6)).Join(1).Ticks(1, 3, 5057, 1902).WriteTo(Path.Combine(date, "game2"));
        new SidecarFile(Game1.Start)
            .Typed(0.5, "step", "client1", "game1", ("name", "run"), ("attempt", 1), ("phase", "start"))
            .Typed(4, "step", "client1", "game1", ("name", "run"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"), ("ms", 3500))
            .Typed(6.5, "step", "client1", "game2", ("name", "run"), ("attempt", 1), ("phase", "start"))
            .WriteTo(date, "120000");

        var run = Assert.Single(Steps.Of(GameQuery.Open(Path.Combine(date, "game1"))), s => s.Name == "run");

        Assert.Equal("ok", run.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(4), run.End);
    }

    [Fact]
    public void WhenTheNextGameHasTheSameNameAnEventNamingItStopsAtThisGamesEnd()
    {
        using var root = new TempRoot();
        var date = Game1.Date(root);
        new HandTrace("client1", "game1", Game1.Start, "server#1").Join(1).Ticks(1, 3, 5057, 1902).WriteTo(Path.Combine(date, "game1"));
        new HandTrace("client1", "game1", Game1.Start.AddSeconds(6), "server#2").Join(1).Ticks(1, 3, 5057, 1902).WriteTo(Path.Combine(date, "game1~2"));
        new SidecarFile(Game1.Start)
            .Typed(0.5, "step", "client1", "game1", ("name", "run"), ("attempt", 1), ("phase", "start"))
            .Typed(4, "step", "client1", "game1", ("name", "run"), ("attempt", 1), ("phase", "end"), ("outcome", "ok"), ("ms", 3500))
            .WriteTo(date, "120000");
        var games = GameTrace.GamesUnder(date);
        Assert.Equal(2, games.Count);
        var first = new GameQuery(games[0]);
        var second = new GameQuery(games[1]);

        Assert.Equal("unfinished", Assert.Single(Steps.Of(first), s => s.Name == "run").Outcome);
        Assert.Single(second.GameEntries(), e => e.Kind == "step" && e.Str("phase") == "end");
        Assert.DoesNotContain(first.GameEntries(), e => e.Kind == "step" && e.Str("phase") == "end");
    }

    private static MapSource Map()
    {
        var rows = Enumerable.Range(0, 60).Select(r => Enumerable.Range(0, 60).Select(c => r == 20 ? 7 : r == 21 ? 1 : 0).ToArray()).ToArray();
        var json = JsonSerializer.Serialize(new { levelOrigin = new { x = 5130, y = 1980 }, mapRows = rows, adjacentLevels = new { }, npcs = new { }, objects = new { } });
        return new MapSource("http://map.invalid", null, _ => json);
    }

    [Fact]
    public void AnExportedMomentNamesNobodyAndKeepsTheRawCollisionValuesAndEveryModelsPosition()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("someone-real", "party1", Game1.Start).Join(1, characterClass: CharacterClass.Barbarian)
            .OutOfTown(1)
            .Received(1.2, SyntheticGame.PlayerInGame(2, CharacterClass.Amazon, "another-real"))
            .Received(1.3, SyntheticGame.AssignPlayer(2, CharacterClass.Amazon, "another-real", 5162, 2012))
            .Received(1.5, SyntheticGame.SetSkill(1, Hand.Right, Skill.Whirlwind))
            .Received(1.6, SyntheticGame.AssignNpc(0x40, (NPCCode)345, 5160, 1998))
            .Received(2, SyntheticGame.NpcMove(0x40, 5165, 1995))
            .Ticks(3, 4)
            .WriteTo(folder);

        var json = MomentExport.Export(GameQuery.Open(folder), "someone-real", TimeSpan.FromSeconds(3), 20, Map());
        using var fixture = JsonDocument.Parse(json);
        var r = fixture.RootElement;

        Assert.DoesNotContain("someone-real", json, StringComparison.Ordinal);
        Assert.DoesNotContain("another-real", json, StringComparison.Ordinal);
        Assert.Equal("Barbarian", r.GetProperty("self").GetProperty("class").GetString());
        Assert.Equal("Whirlwind", r.GetProperty("self").GetProperty("rightSkill").GetString());
        var player = Assert.Single(r.GetProperty("players").EnumerateArray());
        Assert.Equal("player1", player.GetProperty("role").GetString());
        var unit = Assert.Single(r.GetProperty("units").EnumerateArray());
        Assert.Equal("CouncilMember1", unit.GetProperty("code").GetString());
        Assert.Equal("[5165,1995]", unit.GetProperty("legacy").GetRawText());
        Assert.Equal("[5160,1998]", unit.GetProperty("anchored").GetRawText());
        Assert.Equal("[5165,1995]", unit.GetProperty("server").GetRawText());
        var origin = r.GetProperty("gridOrigin");
        Assert.Equal("[5140,1990]", origin.GetRawText());
        var grid = r.GetProperty("grid");
        Assert.Equal(41, grid.GetArrayLength());
        Assert.Equal(7, grid[2000 - 1990][0].GetInt32());
        Assert.Equal(1, grid[2001 - 1990][0].GetInt32());
        Assert.Equal(0, grid[0][0].GetInt32());
    }
}
