using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.D2GS.Quest;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

/// <summary>
/// Old recordings must keep replaying to the state their bot held. Each committed synthetic trace in data/rules is
/// replayed under every client rules version this build knows, and a hash of the whole replayed state is compared with
/// the one recorded for that version in data/rules/golden.json. A change to how Game reads a packet that is not behind a
/// new rule changes the hash of an old version and fails here.
/// </summary>
public class RulesGuardTests
{
    /// <summary>Set to "add" to write missing fixtures and the golden hashes of versions not recorded yet; recorded ones are never rewritten.</summary>
    public const string Variable = "D2NG_RULES_GOLDEN";

    private const string RegenerateCommand = "$env:D2NG_RULES_GOLDEN='add'; dotnet test --solution D2NG.slnx -c Debug; Remove-Item env:D2NG_RULES_GOLDEN";

    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string Folder([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source), "data", "rules");

    private static string GoldenPath => Path.Combine(Folder(), "golden.json");

    /// <summary>The value that adds without comparing with git's committed golden file, for a checkout without git.</summary>
    public const string AddWithoutGit = "add-without-git";

    private static bool Adding
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(Variable);
            var adding = string.Equals(value, "add", StringComparison.OrdinalIgnoreCase) || string.Equals(value, AddWithoutGit, StringComparison.OrdinalIgnoreCase);
            if (adding && AddRefusal.Value is { } refusal)
            {
                Assert.Fail(refusal);
            }

            return adding;
        }
    }

    private static readonly Lazy<string> AddRefusal = new(() => RefuseToAdd(Folder(), GoldenPath,
        string.Equals(Environment.GetEnvironmentVariable(Variable), AddWithoutGit, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Why "add" must not run: a fixture or golden key the committed golden file has is missing from the working one, so
    /// adding would record it afresh from today's code. Null when nothing is missing. Without git the caller must have
    /// asked for <see cref="AddWithoutGit"/>.
    /// </summary>
    internal static string RefuseToAdd(string folder, string goldenPath, bool withoutGit, Func<string, string> gitShow = null)
    {
        gitShow ??= GitShow;
        var committedText = gitShow(goldenPath);
        if (committedText == null)
        {
            return withoutGit ? null
                : $"{Variable}=add compares {goldenPath} with git's committed version, and git could not show it; set {Variable}={AddWithoutGit} to add without that check";
        }

        var committed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(committedText);
        var working = File.Exists(goldenPath) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(goldenPath)) : [];
        var missing = new List<string>();
        foreach (var (scenario, versions) in committed)
        {
            if (!File.Exists(Path.Combine(folder, scenario + TraceFormat.Extension)))
            {
                missing.Add($"fixture {scenario}{TraceFormat.Extension}");
            }

            missing.AddRange(versions.Keys.Where(v => !working.TryGetValue(scenario, out var have) || !have.ContainsKey(v)).Select(v => $"{scenario} version {v}"));
        }

        return missing.Count == 0 ? null
            : $"{Variable}=add refuses to run: committed golden state is missing from the working copy ({string.Join(", ", missing)}); adding would record it again from today's code. Restore it with git checkout first.";
    }

    private static string GitShow(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            using var root = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["-C", directory, "rev-parse", "--show-prefix"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var prefix = root.StandardOutput.ReadToEnd().Trim();
            root.WaitForExit();
            if (root.ExitCode != 0)
            {
                return null;
            }

            using var show = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["-C", directory, "show", $"HEAD:{prefix}{Path.GetFileName(path)}"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var text = show.StandardOutput.ReadToEnd();
            show.WaitForExit();
            return show.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    [Fact]
    public void AddingRefusesWhenCommittedGoldenStateIsMissing()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var golden = Path.Combine(root.Path, "golden.json");
        const string Committed = """{"monsters":{"0":"A","1":"B"},"items":{"0":"C"}}""";
        File.WriteAllText(Path.Combine(root.Path, "monsters" + TraceFormat.Extension), "");
        File.WriteAllText(Path.Combine(root.Path, "items" + TraceFormat.Extension), "");
        File.WriteAllText(golden, """{"monsters":{"0":"A","1":"B"},"items":{"0":"C"},"self":{"0":"D"}}""");
        Assert.Null(RefuseToAdd(root.Path, golden, withoutGit: false, _ => Committed));

        File.WriteAllText(golden, """{"monsters":{"0":"A"},"items":{"0":"C"}}""");
        Assert.Contains("monsters version 1", RefuseToAdd(root.Path, golden, withoutGit: false, _ => Committed), StringComparison.Ordinal);

        File.WriteAllText(golden, """{"monsters":{"0":"A","1":"B"},"items":{"0":"C"}}""");
        File.Delete(Path.Combine(root.Path, "items" + TraceFormat.Extension));
        Assert.Contains("fixture items", RefuseToAdd(root.Path, golden, withoutGit: false, _ => Committed), StringComparison.Ordinal);

        Assert.Contains(AddWithoutGit, RefuseToAdd(root.Path, golden, withoutGit: false, _ => null), StringComparison.Ordinal);
        Assert.Null(RefuseToAdd(root.Path, golden, withoutGit: true, _ => null));
    }

    public static TheoryData<string, int> Cases()
    {
        var cases = new TheoryData<string, int>();
        foreach (var scenario in Scenarios.Keys)
        {
            for (var version = 0; version <= ClientRules.LatestVersion; version++)
            {
                cases.Add(scenario, version);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AnOldRulesVersionReplaysToTheStateItAlwaysDid(string scenario, int version)
    {
        var path = Path.Combine(Folder(), scenario + TraceFormat.Extension);
        if (!File.Exists(path))
        {
            Assert.True(Adding, $"the fixture {path} is missing; write it with: {RegenerateCommand}");
            Scenarios[scenario]().WriteTo(Folder(), scenario);
        }

        var dump = Dump(path, ClientRules.ForVersion(version));
        var hash = Hash(dump);
        var golden = LoadGolden();
        var key = version.ToString(CultureInfo.InvariantCulture);
        if (!golden.TryGetValue(scenario, out var versions) || !versions.TryGetValue(key, out var expected))
        {
            Assert.True(Adding, $"no golden state for {scenario} under client rules {version}. A new rules version needs its golden values: run {RegenerateCommand}, check that only the new version was added to {GoldenPath}, and commit it. State now:\n{dump}");
            lock (GoldenLock)
            {
                golden = LoadGolden();
                golden.TryAdd(scenario, []);
                golden[scenario].TryAdd(key, hash);
                File.WriteAllText(GoldenPath, JsonSerializer.Serialize(golden, Indented) + "\n");
            }

            return;
        }

        Assert.True(expected == hash,
            $"{scenario} replayed under client rules {version} no longer holds the state it held ({expected}, now {hash}). A change to how Game interprets a packet or a "
            + $"client action must not change what an old rules version replays to: put it behind a new rule at version {ClientRules.LatestVersion + 1} in ClientRules, keep the old "
            + $"behaviour when the rule is off, and add that version's golden values with {RegenerateCommand}. State now:\n{dump}");
    }

    [Fact]
    public void EachRuleChangesTheStateOfAFixture()
    {
        foreach (var (rule, _, _) in ClientRules.Table)
        {
            var changed = Scenarios.Keys.Any(scenario =>
            {
                var path = Path.Combine(Folder(), scenario + TraceFormat.Extension);
                return File.Exists(path) && Hash(Dump(path, ClientRules.Latest.Without(rule))) != Hash(Dump(path, ClientRules.Latest));
            });
            Assert.True(changed, $"no fixture in {Folder()} shows {rule}: add a scenario that exercises it");
        }
    }

    private static readonly object GoldenLock = new();

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static Dictionary<string, Dictionary<string, string>> LoadGolden()
        => File.Exists(GoldenPath) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(GoldenPath)) : [];

    private static string Hash(string dump) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..16];

    /// <summary>The replayed state at the end of the trace, one fact per line in a fixed order.</summary>
    internal static string Dump(string path, ClientRules rules)
    {
        using var replayer = new Replayer(path, rules);
        var result = replayer.ReplayToEnd();
        var game = replayer.Game;
        var text = new StringBuilder();
        void Line(FormattableString line) => text.Append(FormattableString.Invariant(line)).Append('\n');
        string At(Point point) => point == null ? "-" : FormattableString.Invariant($"{point.X},{point.Y}");

        Line($"handler failures {result.HandlerFailures}, digests {result.Digests} mismatched {result.MismatchedDigests}");
        var me = game.Me;
        Line($"self {me?.Id} at {At(me?.Location)} life {me?.Life} mana {me?.Mana} act {game.Act} area {game.Area} resolved {game.ResolvedArea} confirmed {At(game.ServerConfirmedLocation)}");
        Line($"cursor {game.CursorItem?.Id}");
        foreach (var unit in game.WorldObjects.Values.OrderBy(u => u.Type).ThenBy(u => u.Id))
        {
            Line($"unit {unit.Type} {unit.Id} code {unit.Code} npc {unit.NPCCode} state {unit.State} life {unit.LifePercentage:F4} at {At(unit.LegacyLocation)} known {At(unit.LastKnownLocation)} to {At(unit.Destination)} chasing {unit.ChaseTargetId}");
        }

        foreach (var item in game.Items.Values.OrderBy(i => i.Id))
        {
            Line($"item {item.Id} {item.Name} {item.Action} in {item.Container} at {At(item.Location)}");
        }

        Line($"inventory {string.Join(",", game.Inventory.Items.Select(i => i.Id).Order())}");
        Line($"stash {string.Join(",", game.Stash.Items.Select(i => i.Id).Order())}");
        Line($"cube {string.Join(",", game.Cube.Items.Select(i => i.Id).Order())}");
        // Only where a carried item has a count, so the fixtures from before the line keep their hashes.
        var counted = game.Inventory.Items.Concat(game.Stash.Items).Concat(game.Cube.Items).Where(i => i.Amount != 0).OrderBy(i => i.Id).ToList();
        if (counted.Count > 0)
        {
            Line($"carried amounts {string.Join(",", counted.Select(i => FormattableString.Invariant($"{i.Id}={i.Amount}")))}");
        }

        Line($"belt {game.Belt.Width}x{game.Belt.Height} {string.Join(",", game.Belt.Items.OrderBy(i => i.Id).Select(i => FormattableString.Invariant($"{i.Id}@{At(i.Location)}")))}");
        Line($"quest words {string.Join(",", game.Quests.CharacterWords.Select(w => w.ToString("X4", CultureInfo.InvariantCulture)))}");
        Line($"game quest words {string.Join(",", game.Quests.GameWords.Select(w => w.ToString("X4", CultureInfo.InvariantCulture)))}");
        if (me != null)
        {
            Line($"self experience {me.Experience} stamina {me.Stamina} life% {me.LifePercentage:F4} act {me.Act} area {me.Area} party {me.PartyId} merc {me.MercId} corpse {me.CorpseId}");
            Line($"self attributes {string.Join(",", me.Attributes.OrderBy(a => a.Key).Select(a => FormattableString.Invariant($"{a.Key}={a.Value}")))}");
            Line($"self skills {string.Join(",", me.Skills.OrderBy(s => s.Key).Select(s => FormattableString.Invariant($"{s.Key}={s.Value}")))}");
            Line($"self item skills {string.Join(",", me.ItemSkills.OrderBy(s => s.Key).Select(s => FormattableString.Invariant($"{s.Key}={s.Value}")))}");
            Line($"self active skills {string.Join(",", me.ActiveSkills.OrderBy(s => s.Key).Select(s => FormattableString.Invariant($"{s.Key}={s.Value}")))}");
            Line($"self effects {string.Join(",", me.Effects.Keys.Order())}");
            Line($"self equipment {string.Join(",", me.Equipment.OrderBy(e => e.Key).Select(e => FormattableString.Invariant($"{e.Key}={e.Value?.Id}")))}");
            Line($"self waypoints {string.Join(",", me.AllowedWaypoints.Order())} selected {me.LastSelectedWaypointId}");
            Line($"self summons {string.Join(",", me.Summons.OrderBy(s => s.Id).Select(s => FormattableString.Invariant($"{s.Id}:{s.NPCCode}")))}");
        }

        foreach (var quest in Enum.GetValues<QuestId>())
        {
            Line($"quest {quest} {game.Quests.GetCharacterFlags(quest):X4} {game.Quests.GetGameFlags(quest):X4}");
        }

        foreach (var player in game.Players.OrderBy(p => p.Id))
        {
            Line($"player {player.Id} {player.Name} at {At(player.Location)}");
            Line($"player {player.Id} class {player.Class} act {player.Act} area {player.Area} party {player.PartyId} merc {player.MercId} corpse {player.CorpseId} life% {player.LifePercentage:F4} effects {string.Join(",", player.Effects.Keys.Order())} equipment {string.Join(",", player.Equipment.OrderBy(e => e.Key).Select(e => FormattableString.Invariant($"{e.Key}={e.Value?.Id}")))}");
        }

        return text.ToString();
    }

    private static byte[] Action(ClientAction action, uint argument) => [(byte)action, .. BitConverter.GetBytes(argument)];

    /// <summary>The synthetic item as an owned-item packet (0x9D) of the joined player, id 1.</summary>
    private static byte[] Owned(D2NG.Core.D2GS.Items.Action action, uint id)
    {
        var world = SyntheticGame.Item(action, id);
        byte[] packet = [0x9D, world[1], (byte)(world.Length + 5), world[3], .. world.AsSpan(4, 4), 0x00, 0x01, 0x00, 0x00, 0x00, .. world.AsSpan(8)];
        return packet;
    }

    private static byte[] WithByte(byte[] packet, int index, byte value)
    {
        packet[index] = value;
        return packet;
    }

    /// <summary>The scenarios a fixture is written from, once; afterwards the committed file is the input.</summary>
    private static readonly Dictionary<string, Func<HandTrace>> Scenarios = new()
    {
        ["monsters"] = () =>
        {
            var trace = new HandTrace(SyntheticTrace.CharacterName, "rules-monsters", Start);
            trace.Sent(0, SyntheticTrace.Logon());
            foreach (var packet in SyntheticTrace.Joining())
            {
                trace.Received(0.01, packet);
            }

            trace.Received(0.5, SyntheticGame.QuestInfo(1, (1, 0x1001), (4, 0x2002)));
            trace.Received(1.0, WithByte(SyntheticTrace.MonsterStop(0xE9, 5070, 1905), 9, 0x50));
            trace.Received(1.5, SyntheticGame.NpcMove(0xE5, 5080, 1910));
            trace.Received(2.0, SyntheticGame.NpcMoveToUnit(0xE5, 5082, 1911, 1));
            trace.Received(2.5, SyntheticGame.NpcHit(0xE9, 30));
            trace.Received(3.0, SyntheticGame.NpcState(0xE9, EntityState.Dieing, 0, 0, 40));
            trace.Received(3.5, WithByte(SyntheticTrace.MonsterStop(0xE9, 5071, 1906), 9, 0x60));
            trace.Received(4.0, WithByte(SyntheticGame.AssignNpc(0xF1, NPCCode.Zombie, 5100, 1920), 11, 0x40));
            trace.Received(4.2, WithByte(SyntheticTrace.MonsterStop(0xF1, 5101, 1921), 9, 0x00));
            trace.Received(4.5, SyntheticGame.NpcState(0xF1, EntityState.Dead, 5100, 1920, 20));
            trace.Received(5.0, SyntheticTrace.LifeManaUpdate);
            trace.Received(5.5, WithByte(SyntheticTrace.MonsterStop(0xE5, 5083, 1912), 9, 0x50));
            return trace;
        },
        ["items"] = () =>
        {
            var trace = new HandTrace(SyntheticTrace.CharacterName, "rules-items", Start);
            trace.Sent(0, SyntheticTrace.Logon());
            foreach (var packet in SyntheticTrace.Joining())
            {
                trace.Received(0.01, packet);
            }

            trace.Received(0.1, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x50));
            trace.Add(TraceRecordKind.ClientAction, 1.0, Action(ClientAction.Lift, 0x50));
            trace.Received(1.1, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.RemoveFromContainer, 0x50));
            trace.Received(1.5, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x50));
            trace.Received(2.0, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x51));
            trace.Received(2.5, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.RemoveFromContainer, 0x51));
            trace.Received(3.0, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x52));
            trace.Add(TraceRecordKind.ClientAction, 3.1, Action(ClientAction.Sell, 0x52));
            trace.Received(3.2, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.RemoveFromContainer, 0x52));
            trace.Received(3.5, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x53));
            trace.Add(TraceRecordKind.ClientAction, 3.6, Action(ClientAction.Use, 0x53));
            trace.Received(3.7, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.RemoveFromContainer, 0x53));
            trace.Add(TraceRecordKind.ClientAction, 4.0, Action(ClientAction.OpenShop, 0xE9));
            trace.Received(4.1, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.AddToShop, 0x60));
            trace.Add(TraceRecordKind.ClientAction, 4.5, Action(ClientAction.RemoveMerchantItems, 0));
            trace.Received(4.6, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.AddToShop, 0x61));
            trace.Add(TraceRecordKind.ClientAction, 5.0, Action(ClientAction.Move, ClientActions.Pack(new Point(5060, 1903))));
            return trace;
        },
        ["self"] = () =>
        {
            var trace = new HandTrace(SyntheticTrace.CharacterName, "rules-self", Start);
            trace.Sent(0, SyntheticTrace.Logon());
            foreach (var packet in SyntheticTrace.Joining())
            {
                trace.Received(0.01, packet);
            }

            trace.Received(0.2, [0x94, 0x02, 0x01, 0x00, 0x00, 0x00, 0x24, 0x00, 0x05, 0x25, 0x00, 0x03]);
            trace.Received(0.3, SyntheticGame.SetSkill(1, Hand.Right, (Skill)0x24));
            trace.Received(0.4, SyntheticGame.Attribute(0x0E, 120));
            trace.Received(0.45, SyntheticGame.Attribute(0x0C, 15));
            trace.Received(0.5, SyntheticGame.AddEffect(EntityType.Player, 1, (EntityEffect)32));
            trace.Received(0.6, SyntheticGame.Reassign(1, 5060, 1905));
            trace.Received(0.7, SyntheticGame.WalkVerify(5062, 1906));
            trace.Received(0.8, SyntheticGame.LifeMana(900, 50, 5062, 1906));
            trace.Received(0.9, Owned(D2NG.Core.D2GS.Items.Action.PutInBelt, 0x70));
            trace.Received(0.92, Owned(D2NG.Core.D2GS.Items.Action.PutInBelt, 0x74));
            trace.Received(0.95, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x71));
            trace.Received(1.0, Owned(D2NG.Core.D2GS.Items.Action.RemoveFromBelt, 0x70));
            trace.Received(1.05, [0x3F, 0x00, 0x71, 0x00, 0x00, 0x00, 0x00, 0x00]);
            trace.Received(1.1, Owned(D2NG.Core.D2GS.Items.Action.Equip, 0x72));
            trace.Received(1.15, Owned(D2NG.Core.D2GS.Items.Action.Equip, 0x73));
            trace.Received(1.2, Owned(D2NG.Core.D2GS.Items.Action.Unequip, 0x73));
            trace.Received(1.3, SyntheticGame.Attribute(0x0E, 90));
            trace.Received(1.4, SyntheticGame.PlayerInGame(2, CharacterClass.Sorceress, "visitor"));
            trace.Received(1.45, SyntheticGame.AssignPlayer(2, CharacterClass.Sorceress, "visitor", 5070, 1910));
            trace.Received(1.5, [0x8D, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00]);
            trace.Received(1.55, [0x8D, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00]);
            trace.Received(1.6, SyntheticGame.PlayerInGame(3, CharacterClass.Amazon, "stayer"));
            trace.Received(1.65, SyntheticGame.AssignPlayer(3, CharacterClass.Amazon, "stayer", 5072, 1912));
            trace.Received(1.7, SyntheticGame.PlayerLeft(2));
            trace.Received(1.8, [0x63, 0x77, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            trace.Received(1.9, [0x81, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x99, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            trace.Received(2.0, SyntheticGame.QuestInfo(1, (0, 0x0001), (7, 0x1003), (40, 0x2005), (47, 0x8001)));
            trace.Received(2.1, SyntheticGame.CorpseAssign(1, 0x55));
            trace.Received(2.5, [0x03, 0x01, 0x73, 0x31, 0x1B, 0x5C, 0x28, 0x00, 0xB0, 0x00, 0x50, 0x24]);
            trace.Received(2.6, SyntheticGame.Reassign(1, 5100, 5090));
            trace.Received(2.7, SyntheticGame.LifeMana(950, 60, 5100, 5090));
            return trace;
        },
        ["lifts"] = () =>
        {
            var trace = new HandTrace(SyntheticTrace.CharacterName, "rules-lifts", Start);
            trace.Sent(0, SyntheticTrace.Logon());
            foreach (var packet in SyntheticTrace.Joining())
            {
                trace.Received(0.01, packet);
            }

            trace.Received(0.1, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x50));
            trace.Add(TraceRecordKind.ClientAction, 1.0, Action(ClientAction.Lift, 0x50));
            trace.Add(TraceRecordKind.ClientAction, 3.5, Action(ClientAction.Lift, 0x51));
            trace.Received(3.6, SyntheticGame.Item(D2NG.Core.D2GS.Items.Action.RemoveFromContainer, 0x50));
            return trace;
        },
        ["carried"] = () =>
        {
            var trace = new HandTrace(SyntheticTrace.CharacterName, "rules-carried", Start);
            trace.Sent(0, SyntheticTrace.Logon());
            foreach (var packet in SyntheticTrace.Joining())
            {
                trace.Received(0.01, packet);
            }

            // A portal tome in the bag, filled by a stack buy: the server answers with its new count.
            trace.Received(0.1, ItemPackets.Stack(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x60, "tbk", 2, 1, container: D2NG.Core.D2GS.Items.ContainerType.Inventory));
            trace.Received(0.5, ItemPackets.Quantity(0x60, 100));
            trace.Received(0.6, ItemPackets.Quantity(0x60, 99));
            // The lifetime total on joining and again later, with gains between.
            trace.Received(1.0, [0x1C, .. BitConverter.GetBytes(399_999_000u)]);
            trace.Received(1.1, [0x1B, .. BitConverter.GetBytes((ushort)1_000)]);
            trace.Received(2.0, [0x1C, .. BitConverter.GetBytes(400_000_000u)]);
            trace.Received(2.1, [0x1A, 0x10]);
            return trace;
        },
        ["socketed"] = () =>
        {
            var trace = new HandTrace(SyntheticTrace.CharacterName, "rules-socketed", Start);
            trace.Sent(0, SyntheticTrace.Logon());
            foreach (var packet in SyntheticTrace.Joining())
            {
                trace.Received(0.01, packet);
            }

            // A three-socket shield and a perfect diamond in the bag; the diamond lifted and set in the shield.
            trace.Received(0.1, ItemPackets.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x70, "lrg", D2NG.Core.D2GS.Items.ContainerType.Inventory, 0, 0));
            trace.Received(0.2, ItemPackets.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x71, "gpw", D2NG.Core.D2GS.Items.ContainerType.Inventory, 4, 0));
            trace.Add(TraceRecordKind.ClientAction, 1.0, Action(ClientAction.Lift, 0x71));
            trace.Received(1.05, ItemPackets.Item(D2NG.Core.D2GS.Items.Action.RemoveFromContainer, 0x71, "gpw", D2NG.Core.D2GS.Items.ContainerType.Inventory, 4, 0));
            trace.Received(1.2, ItemPackets.Owned(D2NG.Core.D2GS.Items.Action.ItemInSocket, 0x71, "gpw", 0x70, D2NG.Core.D2GS.Items.ContainerType.Item));
            return trace;
        },
        ["quiver"] = () =>
        {
            var trace = new HandTrace(SyntheticTrace.CharacterName, "rules-quiver", Start);
            trace.Sent(0, SyntheticTrace.Logon());
            foreach (var packet in SyntheticTrace.Joining())
            {
                trace.Received(0.01, packet);
            }

            const D2NG.Core.D2GS.Items.DirectoryType LeftHand = D2NG.Core.D2GS.Items.DirectoryType.LeftHand;
            trace.Received(0.05, ItemPackets.Stack(D2NG.Core.D2GS.Items.Action.Equip, 0x98, "aqv", 0, 1, LeftHand));
            trace.Received(0.07, ItemPackets.Stack(D2NG.Core.D2GS.Items.Action.Unequip, 0x98, "aqv", 0, 1, LeftHand));
            trace.Received(0.1, ItemPackets.Stack(D2NG.Core.D2GS.Items.Action.Equip, 0x94, "aqv", 3, 1, LeftHand));
            trace.Received(0.2, ItemPackets.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x95, "aqv", D2NG.Core.D2GS.Items.ContainerType.Inventory, 3, 5));
            trace.Received(1.0, ItemPackets.Quantity(0x94, 2));
            trace.Received(1.5, ItemPackets.Stack(D2NG.Core.D2GS.Items.Action.Unequip, 0x94, "aqv", 2, 1, LeftHand));
            trace.Received(1.6, ItemPackets.Item(D2NG.Core.D2GS.Items.Action.PutInContainer, 0x94, "aqv", D2NG.Core.D2GS.Items.ContainerType.Inventory, 5, 5));
            trace.Received(2.0, ItemPackets.Stack(D2NG.Core.D2GS.Items.Action.Equip, 0x97, "cqv", 2, 1, LeftHand));
            trace.Received(2.5, ItemPackets.Quantity(0x97, 1));
            trace.Received(3.0, ItemPackets.Quantity(0x97, 0));
            trace.Received(3.5, ItemPackets.Stack(D2NG.Core.D2GS.Items.Action.Unequip, 0x97, "cqv", 2, 1, LeftHand));
            return trace;
        },
    };
}
