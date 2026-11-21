using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Players;
using D2NG.Core.MCP;
using D2NG.Core.Tracing;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace D2NG.Trace;

/// <summary>
/// A game folder written the way the recorder lays one out - client traces, a process sidecar and the
/// realm trace - with chosen timestamps, for tests and benchmarks.
///
/// <see cref="WriteScenario"/>: two clients, client 1 (a sorceress) and client 2 (an amazon). Client 1
/// teleports into the Blood Moor, a zombie walks up and hits it from 1000 to 600 life in under a
/// second while it casts Frozen Orb, the zombie dies, the game server goes silent for 10.5 s and the
/// sorceress chickens and leaves. Client 2 lifts an item to the cursor and puts it back, sees the
/// sorceress's town portal and a quest word change, fails a crossing step, is killed by a foul crow
/// and loses records to a full buffer. Client 1's realm trace shows a create refused for a taken name
/// before the game was created.
/// </summary>
public static class SyntheticGame
{
    public const string GameName = "synthetic-1";
    public const string RefusedGameName = "synthetic-0";
    public const string Sorceress = "client1";
    public const string Amazon = "client2";
    public const uint SorceressId = 1;
    public const uint AmazonId = 2;
    public const uint Zombie = 0xE9;
    public const uint FoulCrow = 0xE5;
    public const uint FarMonster = 0xF1;
    public const uint CursorItem = 0x40;
    public const uint Portal = 0x77;
    public const uint Corpse = 0x99;

    private static readonly byte[] HoradricCube = Convert.FromHexString("9C0417101B0000001000800001001022F686078286F01F");

    /// <summary>Writes the scenario under <paramref name="root"/>; returns the game folder.</summary>
    public static string WriteScenario(string root, DateTime? startUtc = null)
    {
        var start = startUtc ?? new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddHours(-1);
        var layout = new Layout(root, start, GameName);

        using var sorceress = new ClientWriter(GameName, Sorceress, SorceressId, CharacterClass.Sorceress, start);
        sorceress.Join(Amazon, AmazonId, CharacterClass.Amazon, other: true);
        sorceress.Receive(0.5, SetSkill(SorceressId, Hand.Right, Skill.Teleport));
        var position = new Point(5057, 1902);
        foreach (var (at, x, y) in new[] { (1.0, 5080, 1930), (1.35, 5110, 1965), (1.7, 5160, 2010) })
        {
            sorceress.Send(at, Location(0x0C, (ushort)x, (ushort)y));
            if (x == 5160)
            {
                sorceress.Receive(at + 0.2, MapReveal((ushort)(x / 5), (ushort)(y / 5), Area.BloodMoor));
            }

            sorceress.Receive(at + 0.23, Reassign(SorceressId, (ushort)x, (ushort)y));
            position = new Point((ushort)x, (ushort)y);
        }

        sorceress.Receive(2.0, LifeMana(1000, 180, position.X, position.Y));
        sorceress.Receive(4.0, SetSkill(SorceressId, Hand.Right, Skill.FrozenOrb));
        sorceress.Receive(5.0, AssignNpc(Zombie, NPCCode.Zombie, 5172, 2016));
        sorceress.Receive(5.1, AssignNpc(FarMonster, NPCCode.FoulCrow, 5300, 2150));
        sorceress.Receive(5.5, NpcMove(Zombie, 5166, 2012));
        for (var i = 0; i < 14; i++)
        {
            var at = 6.0 + i * 0.25;
            sorceress.Send(at, i % 4 == 3 ? Unit(0x0D, EntityType.NPC, Zombie) : Location(0x0C, 5166, 2012));
            sorceress.Receive(at + 0.1, NpcHit(Zombie, (byte)(100 - i * 6)));
        }

        foreach (var (at, life) in new[] { (7.0, 880), (7.4, 700), (7.8, 600) })
        {
            sorceress.Receive(at, NpcAttack(Zombie, SorceressId, 5166, 2012));
            sorceress.Receive(at + 0.05, LifeMana(life, 150, position.X, position.Y));
        }

        sorceress.Receive(9.6, NpcState(Zombie, EntityState.Dead, 5166, 2012, 0));
        sorceress.Digest(11.0, digest => digest with { Life = 999 });
        sorceress.Receive(12.5, LifeMana(600, 160, position.X, position.Y));
        sorceress.Receive(23.0, LifeMana(600, 170, position.X, position.Y));
        sorceress.Send(24.2, [(byte)OutGoingPacket.LeaveGame]);

        using var amazon = new ClientWriter(GameName, Amazon, AmazonId, CharacterClass.Amazon, start.AddSeconds(0.5));
        amazon.Join(Sorceress, SorceressId, CharacterClass.Sorceress, other: true);
        amazon.Receive(1.0, QuestInfo(AmazonId, (1, 0x0000)));
        amazon.Receive(2.5, Item(Core.D2GS.Items.Action.ToCursor, CursorItem));
        amazon.Receive(3.5, Item(Core.D2GS.Items.Action.PutInContainer, CursorItem));
        amazon.Receive(5.5, AssignObject(Portal, EntityCode.TownPortal, 5062, 1906));
        amazon.Receive(5.6, PortalOwner(SorceressId, Sorceress, Portal));
        amazon.Receive(8.0, QuestInfo(AmazonId, (1, 0x2012)));
        amazon.Receive(13.0, LifeMana(1000, 60, 5057, 1902));
        amazon.Receive(17.0, AssignNpc(FoulCrow, NPCCode.FoulCrow, 5065, 1910));
        foreach (var (at, life) in new[] { (19.0, 700), (19.5, 300), (20.0, 0) })
        {
            amazon.Receive(at - 0.1, NpcAttack(FoulCrow, AmazonId, 5057, 1902));
            amazon.Receive(at, LifeMana(life, 50, 5057, 1902));
        }

        amazon.Receive(20.9, CorpseAssign(AmazonId, Corpse));
        amazon.Gap(22.0, 7);
        amazon.Receive(23.7, PlayerLeft(SorceressId));
        amazon.Receive(25.0, LifeMana(0, 50, 5057, 1902));

        sorceress.WriteTo(layout.GameFolder, start.AddSeconds(25));
        amazon.WriteTo(layout.GameFolder, start.AddSeconds(25.5));

        var sidecar = new SidecarWriter(start, GameName);
        sidecar.Log(0.2, LogEventLevel.Information, "Joining game: {Game} with {Account} as {Character}", ("Game", GameName), ("Account", "account-1"), ("Character", Sorceress));
        sidecar.Typed(0.3, "step", Sorceress, ("name", "run"), ("attempt", 1), ("phase", "start"));
        sidecar.Typed(1.9, "move", Sorceress, ("from", new Point(5057, 1902)), ("to", new Point(5160, 2010)), ("area", "RogueEncampment"), ("kind", "teleport"), ("outcome", "arrived"));
        sidecar.Typed(6.0, "target", Sorceress, ("id", Zombie), ("code", "Zombie"), ("reason", "nearest"), ("rejected", new[] { new RejectedUnit(FarMonster, "FoulCrow", "farther than 40") }));
        sidecar.Log(7.1, LogEventLevel.Debug, "{Character} target {Id} at {Distance}", ("Character", Sorceress), ("Id", Zombie), ("Distance", 6.3));
        sidecar.Log(9.0, LogEventLevel.Information, "Run pickups: nothing worth picking up");
        sidecar.Typed(14.0, "wait", Amazon, ("reason", "taxi portal"), ("state", "entered"));
        sidecar.Typed(15.0, "step", Amazon, ("name", "cross BloodMoor->ColdPlains"), ("attempt", 1), ("phase", "end"), ("outcome", "failed"), ("reason", "no path"));
        sidecar.Typed(15.8, "step", Amazon, ("name", "cross BloodMoor->ColdPlains"), ("attempt", 2), ("phase", "end"), ("outcome", "ok"));
        sidecar.Typed(16.0, "step", Amazon, ("name", "cross ColdPlains->StonyField"), ("attempt", 3), ("phase", "end"), ("outcome", "failed"), ("reason", "not in StonyField at (5057, 1902)"));
        sidecar.Typed(16.5, "wait", Amazon, ("reason", "taxi portal"), ("state", "left"));
        sidecar.Typed(20.95, "decision", Amazon, ("name", "death"), ("f", new (string, object)[] { ("corpse", Corpse), ("damagedRecently", true), ("at", new Point(5057, 1902)), ("area", "RogueEncampment") }));
        sidecar.Log(21.5, LogEventLevel.Error, "{Character} run threw: {Message}", new InvalidOperationException("Sequence contains no elements"), ("Character", Amazon), ("Message", "Sequence contains no elements"));
        sidecar.Log(23.0, LogEventLevel.Warning, "Instance 1 game server was silent for 10.5s and then resumed");
        sidecar.Typed(24.0, "decision", Sorceress, ("name", "chicken"), ("f", new (string, object)[] { ("life", 600), ("maxLife", 1000), ("reason", "client1 at 600 of 1000 life (60%)") }));
        sidecar.Log(24.0, LogEventLevel.Information, "Leaving game: {Reason}", ("Reason", "client1 at 600 of 1000 life (60%)"));
        sidecar.Typed(24.3, "step", Sorceress, ("name", "run"), ("attempt", 1), ("phase", "end"), ("outcome", "failed"), ("ms", TimeSpan.FromSeconds(24)));
        sidecar.WriteTo(layout.SidecarPath);

        using var realm = new ClientWriter("", $"instance{SorceressId}", 0, CharacterClass.Sorceress, start.AddSeconds(-20), live: false);
        realm.Mcp(0.0, sent: true, McpRequest(0x07, Encoding.ASCII.GetBytes(Sorceress + "\0")));
        realm.Mcp(2.0, sent: true, CreateRequest(RefusedGameName));
        realm.Mcp(2.1, sent: false, McpResult(0x03, 0x1F, 9));
        realm.Mcp(10.0, sent: true, CreateRequest(GameName));
        realm.Mcp(10.1, sent: false, McpResult(0x03, 0x00, 9));
        realm.WriteRealm(layout.RealmPath(SorceressId), start.AddSeconds(-9));
        return layout.GameFolder;
    }

    public const string WalkGameName = "synthetic-walk";
    public const uint Ormus = 0xF5;

    /// <summary>A zombie and a town NPC each stand, walk 10 subtiles east from 6 s to 8 s and stop on the destination.</summary>
    public static string WriteWalkScenario(string root)
    {
        var start = new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddHours(-1);
        var layout = new Layout(root, start, WalkGameName);
        using var sorceress = new ClientWriter(WalkGameName, Sorceress, SorceressId, CharacterClass.Sorceress, start);
        sorceress.Join(Amazon, AmazonId, CharacterClass.Amazon, other: false);
        foreach (var (id, code, y) in new[] { (Zombie, NPCCode.Zombie, (ushort)2000), (Ormus, NPCCode.Ormus, (ushort)2050) })
        {
            sorceress.Receive(5.0, AssignNpc(id, code, 5100, y));
            sorceress.Receive(5.5, MonsterStop(id, 5100, y));
            sorceress.Receive(6.0, NpcMove(id, 5110, y));
            sorceress.Receive(8.0, MonsterStop(id, 5110, y));
        }

        sorceress.WriteTo(layout.GameFolder, start.AddSeconds(9));
        return layout.GameFolder;
    }

    /// <summary>
    /// <paramref name="clients"/> clients over <paramref name="length"/>, <paramref name="packetsPerClient"/>
    /// packets each: monsters assigned, moving, hit, attacking and dying, life updates, casts and
    /// teleports, digests every five seconds, plus a sidecar of log lines and typed events.
    /// </summary>
    public static string WriteBenchmark(string root, int clients, int packetsPerClient, TimeSpan length, int logLines = 50_000)
    {
        var start = new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc) - length - TimeSpan.FromMinutes(5);
        const string game = "bench-1";
        var layout = new Layout(root, start, game);
        var labels = Enumerable.Range(1, clients).Select(i => $"client{i}").ToArray();
        for (var c = 0; c < clients; c++)
        {
            var random = new Random(c + 1);
            var id = (uint)(c + 1);
            using var client = new ClientWriter(game, labels[c], id, (CharacterClass)(c % 7), start.AddSeconds(c * 0.3));
            client.Join(labels[(c + 1) % clients], (uint)((c + 1) % clients + 1), (CharacterClass)((c + 1) % 7), other: clients > 1);
            client.Receive(0.5, SetSkill(id, Hand.Right, Skill.FrozenOrb));
            var monsters = new List<uint>();
            var nextMonster = 0x1000u;
            var x = (ushort)5057;
            var y = (ushort)1902;
            var life = 1000;
            var step = length.TotalSeconds / packetsPerClient;
            var nextDigest = 5.0;
            for (var i = 0; i < packetsPerClient; i++)
            {
                var at = 1 + i * step;
                if (at >= nextDigest)
                {
                    client.Digest(at, null);
                    nextDigest += 5;
                }

                if (monsters.Count < 30 || random.Next(200) == 0)
                {
                    var monster = nextMonster++;
                    monsters.Add(monster);
                    client.Receive(at, AssignNpc(monster, (NPCCode)random.Next(0, 300), (ushort)(x + random.Next(-40, 40)), (ushort)(y + random.Next(-40, 40))));
                    continue;
                }

                var target = monsters[random.Next(monsters.Count)];
                var roll = random.Next(100);
                if (roll < 25)
                {
                    client.Receive(at, MonsterStop(target, (ushort)(x + random.Next(-30, 30)), (ushort)(y + random.Next(-30, 30))));
                }
                else if (roll < 45)
                {
                    client.Receive(at, NpcHit(target, (byte)random.Next(1, 100)));
                }
                else if (roll < 60)
                {
                    life = Math.Clamp(life + random.Next(-60, 50), 100, 1000);
                    client.Receive(at, LifeMana(life, 200, x, y));
                }
                else if (roll < 70)
                {
                    client.Send(at, random.Next(3) == 0 ? Unit(0x0D, EntityType.NPC, target) : Location(0x0C, (ushort)(x + 5), (ushort)(y + 5)));
                }
                else if (roll < 77)
                {
                    client.Receive(at, NpcMove(target, (ushort)(x + random.Next(-20, 20)), (ushort)(y + random.Next(-20, 20))));
                }
                else if (roll < 83)
                {
                    client.Receive(at, NpcAttack(target, id, x, y));
                }
                else if (roll < 88)
                {
                    x = (ushort)Math.Clamp(x + random.Next(-25, 26), 4000, 6000);
                    y = (ushort)Math.Clamp(y + random.Next(-25, 26), 1000, 3000);
                    client.Receive(at, Reassign(id, x, y));
                }
                else if (roll < 92)
                {
                    client.Receive(at, SkillOnPoint(target, Skill.FireBolt, x, y));
                }
                else if (roll < 95)
                {
                    client.Receive(at, NpcState(target, EntityState.Dead, x, y, 0));
                    client.Receive(at, Remove(EntityType.NPC, target));
                    monsters.Remove(target);
                }
                else
                {
                    client.Receive(at, NpcState(target, EntityState.BeingHit, (ushort)(x + 3), (ushort)(y + 3), (byte)random.Next(1, 128)));
                }
            }

            client.WriteTo(layout.GameFolder, start + length);
        }

        var sidecar = new SidecarWriter(start, game);
        var logRandom = new Random(99);
        for (var i = 0; i < logLines; i++)
        {
            var at = 1 + i * length.TotalSeconds / logLines;
            var label = labels[logRandom.Next(labels.Length)];
            switch (i % 10)
            {
                case 0:
                    sidecar.Typed(at, "step", label, ("name", "kill pack"), ("attempt", 1), ("phase", "end"), ("outcome", i % 1000 == 0 ? "failed" : "ok"), ("ms", TimeSpan.FromSeconds(3)));
                    break;
                case 1:
                    sidecar.Typed(at, "move", label, ("from", new Point(5057, 1902)), ("to", new Point(5100, 1950)), ("area", "BloodMoor"), ("kind", "teleport"), ("outcome", "arrived"));
                    break;
                case 2 or 3 or 4:
                    sidecar.Log(at, LogEventLevel.Debug, "{Character} attacking {Id} at distance {Distance}", ("Character", label), ("Id", 0x1000 + i % 300), ("Distance", 12.5));
                    break;
                default:
                    sidecar.Log(at, LogEventLevel.Information, "{Character} picked up {Item} from {Location}", ("Character", label), ("Item", "Grand Charm"), ("Location", "(5057, 1902)"));
                    break;
            }
        }

        sidecar.WriteTo(layout.SidecarPath);
        return layout.GameFolder;
    }

    public const string RealisticGameName = "realistic-1";

    private static readonly byte[][] GroundItems =
    [
        Convert.FromHexString("9C0414107F0000001000A0000100308206570302"),
        HoradricCube,
    ];

    private static readonly byte[][] OwnedItems =
    [
        Convert.FromHexString("9D153F05F300000000010000001100800001000A9877E70682A5117AF30AB51004D5F006CDE0D15145015670FE07604E5C64B9091EAC5122D84C096DB284FF"),
        Convert.FromHexString("9D153A01DF0000000001000000110080000100702824370782A5C1DAB40013B0066FB0040FF084CC0884192382131415840BC22721C18A88E13F"),
    ];

    /// <summary>
    /// What a real 29-minute four-client leveling game received and sent, per thousand packets of one
    /// client, so a long synthetic game costs what a real one does.
    /// </summary>
    private static readonly (string Kind, int Weight)[] RealisticMix =
    [
        ("player-stop", 118), ("entity-move", 99), ("npc-stop", 69), ("remove", 56), ("skill-on-unit", 49),
        ("add-effect", 46), ("send-run", 38), ("ground-item", 36), ("assign-object", 36), ("kill", 30),
        ("life-mana", 30), ("npc-hit", 30), ("npc-move-to-unit", 27), ("npc-attack", 27), ("send-skill-on-unit", 26),
        ("update-effects", 25), ("npc-move", 23), ("npc-state", 17), ("walk-verify", 15), ("map-reveal", 12),
        ("map-hide", 11), ("owned-item", 8), ("assign-npc", 15), ("reassign", 7), ("skill-on-point", 6),
        ("ping", 6), ("pong", 6), ("ally-party", 6), ("item-stats", 5), ("npc-action", 4), ("experience", 4),
    ];

    /// <summary>
    /// <paramref name="clients"/> clients over <paramref name="length"/>, with the packet mix and rate of a
    /// real leveling game (about 30 records a second per client), digests every five seconds and a
    /// sidecar of about 50,000 lines an hour.
    /// </summary>
    public static string WriteRealistic(string root, int clients, TimeSpan length, int packetsPerSecond = 30)
    {
        var start = new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc) - length - TimeSpan.FromMinutes(5);
        var layout = new Layout(root, start, RealisticGameName);
        var labels = Enumerable.Range(1, clients).Select(i => $"client{i}").ToArray();
        var totalWeight = RealisticMix.Sum(m => m.Weight);
        var packets = (int)(length.TotalSeconds * packetsPerSecond);
        for (var c = 0; c < clients; c++)
        {
            var random = new Random(c + 1);
            var id = (uint)(c + 1);
            using var client = new ClientWriter(RealisticGameName, labels[c], id, (CharacterClass)(c % 7), start.AddSeconds(c * 0.3));
            client.Join(labels[(c + 1) % clients], (uint)((c + 1) % clients + 1), (CharacterClass)((c + 1) % 7), other: clients > 1);
            client.Receive(0.5, SetSkill(id, Hand.Right, Skill.FrozenOrb));
            var monsters = new List<uint>();
            var objects = new List<uint>();
            var items = new List<uint>();
            var nextMonster = 0x1000u;
            var nextObject = 0x20000u;
            var nextItem = 0x40000u;
            var x = (ushort)5057;
            var y = (ushort)1902;
            var life = 1000;
            var step = length.TotalSeconds / packets;
            var nextDigest = 5.0;
            ushort Near(ushort at, int spread) => (ushort)(at + random.Next(-spread, spread + 1));
            uint Monster() => monsters[random.Next(monsters.Count)];
            for (var i = 0; i < packets; i++)
            {
                var at = 1 + i * step;
                if (at >= nextDigest)
                {
                    client.Digest(at, null);
                    nextDigest += 5;
                }

                if (monsters.Count < 20)
                {
                    var monster = nextMonster++;
                    monsters.Add(monster);
                    client.Receive(at, AssignNpc(monster, (NPCCode)random.Next(0, 300), Near(x, 40), Near(y, 40)));
                    continue;
                }

                var roll = random.Next(totalWeight);
                var kind = RealisticMix[0].Kind;
                foreach (var (name, weight) in RealisticMix)
                {
                    if (roll < weight)
                    {
                        kind = name;
                        break;
                    }

                    roll -= weight;
                }

                var other = (uint)(random.Next(clients) + 1);
                switch (kind)
                {
                    case "player-stop":
                        client.Receive(at, PlayerStop(other, Near(x, 20), Near(y, 20)));
                        break;
                    case "entity-move":
                        client.Receive(at, EntityMove(other, Near(x, 20), Near(y, 20), Near(x, 20), Near(y, 20)));
                        break;
                    case "npc-stop":
                        client.Receive(at, MonsterStop(Monster(), Near(x, 30), Near(y, 30)));
                        break;
                    case "remove" when random.Next(items.Count + objects.Count + monsters.Count) is var pick && pick < items.Count:
                        var item = items[pick];
                        items.RemoveAt(pick);
                        client.Receive(at, Remove(EntityType.Item, item));
                        break;
                    case "remove" when objects.Count > 0 && random.Next(objects.Count + monsters.Count) < objects.Count:
                        var removed = objects[random.Next(objects.Count)];
                        objects.Remove(removed);
                        client.Receive(at, Remove(EntityType.Object, removed));
                        break;
                    case "remove":
                        var dead = Monster();
                        monsters.Remove(dead);
                        client.Receive(at, Remove(EntityType.NPC, dead));
                        break;
                    case "skill-on-unit":
                        client.Receive(at, SkillOnUnit(Monster(), Skill.FireBolt, other));
                        break;
                    case "add-effect":
                        client.Receive(at, AddEffect(EntityType.Player, other, (EntityEffect)random.Next(1, 150)));
                        break;
                    case "send-run":
                        client.Send(at, Location(0x03, Near(x, 10), Near(y, 10)));
                        break;
                    case "ground-item":
                        var drop = (byte[])GroundItems[random.Next(GroundItems.Length)].Clone();
                        drop[1] = (byte)Core.D2GS.Items.Action.AddToGround;
                        var dropped = items.Count >= 30 ? items[random.Next(items.Count)] : nextItem++;
                        BinaryPrimitives.WriteUInt32LittleEndian(drop.AsSpan(4), dropped);
                        if (!items.Contains(dropped))
                        {
                            items.Add(dropped);
                        }
                        client.Receive(at, drop);
                        break;
                    case "assign-object":
                        var assigned = objects.Count >= 60 ? objects[random.Next(objects.Count)] : nextObject++;
                        if (!objects.Contains(assigned))
                        {
                            objects.Add(assigned);
                        }

                        client.Receive(at, AssignObject(assigned, (EntityCode)random.Next(1, 500), Near(x, 40), Near(y, 40)));
                        break;
                    case "kill":
                        client.Receive(at, [0x11, 0x01, .. BitConverter.GetBytes(Monster()), 0x00, 0x00, 0x00]);
                        break;
                    case "life-mana":
                        life = Math.Clamp(life + random.Next(-60, 50), 100, 1000);
                        client.Receive(at, LifeMana(life, 200, x, y));
                        break;
                    case "npc-hit":
                        client.Receive(at, NpcHit(Monster(), (byte)random.Next(1, 100)));
                        break;
                    case "npc-move-to-unit":
                        client.Receive(at, NpcMoveToUnit(Monster(), Near(x, 20), Near(y, 20), other));
                        break;
                    case "npc-attack":
                        client.Receive(at, NpcAttack(Monster(), other, Near(x, 5), Near(y, 5)));
                        break;
                    case "send-skill-on-unit":
                        client.Send(at, Unit(0x0D, EntityType.NPC, Monster()));
                        break;
                    case "update-effects":
                        client.Receive(at, UpdateEffects(EntityType.Player, other, random));
                        break;
                    case "npc-move":
                        client.Receive(at, NpcMove(Monster(), Near(x, 20), Near(y, 20)));
                        break;
                    case "npc-state":
                        var state = random.Next(4) == 0 ? EntityState.Dead : EntityState.BeingHit;
                        client.Receive(at, NpcState(Monster(), state, Near(x, 20), Near(y, 20), (byte)random.Next(1, 128)));
                        break;
                    case "walk-verify":
                        client.Receive(at, WalkVerify(x, y));
                        break;
                    case "map-reveal":
                        client.Receive(at, MapReveal((ushort)(x / 5 + random.Next(-3, 4)), (ushort)(y / 5 + random.Next(-3, 4)), Area.BloodMoor));
                        break;
                    case "map-hide":
                        client.Receive(at, [0x08, .. BitConverter.GetBytes((ushort)(x / 5)), .. BitConverter.GetBytes((ushort)(y / 5)), (byte)Area.BloodMoor]);
                        break;
                    case "owned-item":
                        var owned = (byte[])OwnedItems[random.Next(OwnedItems.Length)].Clone();
                        BinaryPrimitives.WriteUInt32LittleEndian(owned.AsSpan(4), 0x80000u + (uint)random.Next(40));
                        client.Receive(at, owned);
                        break;
                    case "assign-npc":
                        var spawned = monsters.Count >= 40 ? Monster() : nextMonster++;
                        if (!monsters.Contains(spawned))
                        {
                            monsters.Add(spawned);
                        }

                        client.Receive(at, AssignNpc(spawned, (NPCCode)random.Next(0, 300), Near(x, 40), Near(y, 40)));
                        break;
                    case "reassign":
                        x = (ushort)Math.Clamp(x + random.Next(-25, 26), 4000, 6000);
                        y = (ushort)Math.Clamp(y + random.Next(-25, 26), 1000, 3000);
                        client.Receive(at, Reassign(id, x, y));
                        break;
                    case "skill-on-point":
                        client.Receive(at, SkillOnPoint(Monster(), Skill.FireBolt, Near(x, 10), Near(y, 10)));
                        break;
                    case "ping":
                        client.Send(at, [0x6A, .. BitConverter.GetBytes((uint)i), 0x00, 0x00, 0x00, 0x00]);
                        break;
                    case "pong":
                        client.Receive(at, [0x8F, .. new byte[32]]);
                        break;
                    case "ally-party":
                        client.Receive(at, [0x7F, 0x01, 0x64, 0x00, .. BitConverter.GetBytes(other), (byte)Area.BloodMoor, 0x00]);
                        break;
                    case "item-stats" when items.Count > 0:
                        client.Receive(at, [0x3E, .. BitConverter.GetBytes(items[random.Next(items.Count)]), 0x00, 0x01, 0x05, 0x00, 0x00, 0x00]);
                        break;
                    case "npc-action":
                        client.Receive(at, NpcAction(Monster(), Near(x, 20), Near(y, 20)));
                        break;
                    case "experience":
                        client.Receive(at, [0x1B, (byte)random.Next(1, 255), 0x00]);
                        break;
                }
            }

            client.WriteTo(layout.GameFolder, start + length);
        }

        var sidecar = new SidecarWriter(start, RealisticGameName);
        var logRandom = new Random(99);
        var logLines = (int)(length.TotalHours * 50_000);
        for (var i = 0; i < logLines; i++)
        {
            var at = 1 + i * length.TotalSeconds / logLines;
            var label = labels[logRandom.Next(labels.Length)];
            switch (i % 10)
            {
                case 0:
                    sidecar.Typed(at, "step", label, ("name", "kill pack"), ("attempt", 1), ("phase", "end"), ("outcome", i % 1000 == 0 ? "failed" : "ok"), ("ms", TimeSpan.FromSeconds(3)));
                    break;
                case 1:
                    sidecar.Typed(at, "move", label, ("from", new Point(5057, 1902)), ("to", new Point(5100, 1950)), ("area", "BloodMoor"), ("kind", "teleport"), ("outcome", "arrived"));
                    break;
                case 2 or 3 or 4:
                    sidecar.Log(at, LogEventLevel.Debug, "{Character} attacking {Id} at distance {Distance}", ("Character", label), ("Id", 0x1000 + i % 300), ("Distance", 12.5));
                    break;
                default:
                    sidecar.Log(at, LogEventLevel.Information, "{Character} picked up {Item} from {Location}", ("Character", label), ("Item", "Grand Charm"), ("Location", "(5057, 1902)"));
                    break;
            }
        }

        sidecar.WriteTo(layout.SidecarPath);
        return layout.GameFolder;
    }

    public static byte[] PlayerStop(uint id, ushort x, ushort y) => [0x0D, 0x00, .. BitConverter.GetBytes(id), 0x00, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), 0x00, 0x00];

    public static byte[] EntityMove(uint id, ushort toX, ushort toY, ushort x, ushort y)
        => [0x0F, 0x00, .. BitConverter.GetBytes(id), 0x01, .. BitConverter.GetBytes(toX), .. BitConverter.GetBytes(toY), 0x00, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y)];

    public static byte[] SkillOnUnit(uint caster, Skill skill, uint target)
        => [0x4C, 0x01, .. BitConverter.GetBytes(caster), .. BitConverter.GetBytes((ushort)skill), 0x01, 0x00, .. BitConverter.GetBytes(target), 0x00, 0x00];

    public static byte[] AddEffect(EntityType type, uint id, EntityEffect effect)
        => [0x13, (byte)type, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes((uint)effect), 0x00, 0x00, 0x00, 0x00];

    public static byte[] UpdateEffects(EntityType type, uint id, Random random)
    {
        var packet = new byte[26];
        packet[0] = 0x12;
        packet[1] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), id);
        for (var i = 0; i < 4; i++)
        {
            var effect = random.Next(160);
            packet[6 + effect / 8] |= (byte)(1 << (effect % 8));
        }

        return packet;
    }

    public static byte[] NpcMoveToUnit(uint id, ushort x, ushort y, uint target)
    {
        var packet = new byte[20];
        packet[0] = 0x68;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        packet[5] = 0x18;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), y);
        packet[10] = (byte)EntityType.Player;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), target);
        packet[17] = 0x02;
        return packet;
    }

    public static byte[] NpcAction(uint id, ushort x, ushort y)
    {
        var packet = new byte[16];
        packet[0] = 0x6B;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        packet[5] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), y);
        return packet;
    }

    /// <summary>0x96: stamina in 15 bits, x in 15, one bit, y in 15, least significant bit first.</summary>
    public static byte[] WalkVerify(ushort x, ushort y)
    {
        var packet = new byte[9];
        packet[0] = 0x96;
        var bit = 8;
        void Put(long value, int bits)
        {
            for (var i = 0; i < bits; i++, bit++)
            {
                if (((value >> i) & 1) != 0)
                {
                    packet[bit / 8] |= (byte)(1 << (bit % 8));
                }
            }
        }

        Put(500, 15);
        Put(x, 15);
        Put(0, 1);
        Put(y, 15);
        return packet;
    }

    private sealed class Layout
    {
        public Layout(string root, DateTime startUtc, string game)
        {
            var local = startUtc.ToLocalTime();
            DateFolder = Path.Combine(root, local.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Process = local.AddSeconds(-30).ToString("HHmmss", CultureInfo.InvariantCulture);
            GameFolder = Path.Combine(DateFolder, game);
            Directory.CreateDirectory(GameFolder);
        }

        public string DateFolder { get; }

        public string Process { get; }

        public string GameFolder { get; }

        public string SidecarPath => Path.Combine(DateFolder, TraceSidecar.FilePrefix + Process + TraceSidecar.Extension);

        public string RealmPath(uint instance)
        {
            var folder = Path.Combine(DateFolder, "realm");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, $"instance{instance}-{Process}{TraceFormat.Extension}");
        }
    }

    private sealed class ClientWriter : IDisposable
    {
        private readonly string _gameName;
        private readonly string _label;
        private readonly uint _id;
        private readonly CharacterClass _class;
        private readonly DateTime _start;
        private readonly List<TraceRecord> _records = [];
        private readonly GameServer _gameServer;
        private readonly Game _game;

        public ClientWriter(string gameName, string label, uint id, CharacterClass characterClass, DateTime start, bool live = true)
        {
            _gameName = gameName;
            _label = label;
            _id = id;
            _class = characterClass;
            _start = start;
            if (live)
            {
                _gameServer = new GameServer();
                _game = new Game(_gameServer);
                var stats = new byte[32];
                stats[13] = (byte)(characterClass + 1);
                _game.SelectCharacter(new Character(label, stats));
            }
        }

        public void Join(string otherLabel, uint otherId, CharacterClass otherClass, bool other)
        {
            Send(0, Logon(_label, _class));
            Receive(0.01, [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            Receive(0.02, [0x03, 0x00, 0x73, 0x31, 0x1B, 0x5C, 0x01, 0x00, 0xB0, 0x00, 0x50, 0x24]);
            Receive(0.03, AssignPlayer(_id, _class, _label, 5057, 1902));
            Receive(0.04, Attribute(0x0C, 50));
            Receive(0.05, [0x1F, 0x07, .. BitConverter.GetBytes(1000 << 8)]);
            Receive(0.06, LifeMana(1000, 200, 5057, 1902));
            if (other)
            {
                Receive(0.07, PlayerInGame(otherId, otherClass, otherLabel));
                Receive(0.08, AssignPlayer(otherId, otherClass, otherLabel, 5060, 1905));
            }
        }

        public void Receive(double at, byte[] packet) => Add(TraceRecordKind.D2gsReceived, at, packet);

        public void Send(double at, byte[] packet) => Add(TraceRecordKind.D2gsSent, at, packet);

        public void Mcp(double at, bool sent, byte[] packet) => _records.Add(new TraceRecord(sent ? TraceRecordKind.McpSent : TraceRecordKind.McpReceived, TimeSpan.FromSeconds(at), packet));

        public void Gap(double at, uint dropped)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, dropped);
            _records.Add(new TraceRecord(TraceRecordKind.Gap, TimeSpan.FromSeconds(at), payload));
        }

        public void Digest(double at, Func<TraceDigest, TraceDigest> change)
        {
            var digest = TraceDigest.Capture(_game);
            _records.Add(new TraceRecord(TraceRecordKind.Digest, TimeSpan.FromSeconds(at), (change?.Invoke(digest) ?? digest).ToBytes()));
        }

        private void Add(TraceRecordKind kind, double at, byte[] packet)
        {
            _records.Add(new TraceRecord(kind, TimeSpan.FromSeconds(at), packet));
            if (_gameServer == null)
            {
                return;
            }

            var sent = kind == TraceRecordKind.D2gsSent;
            if (sent && packet[0] == (byte)OutGoingPacket.GameLogon)
            {
                return;
            }

            _gameServer.Replay(new D2gsPacket(packet), sent);
        }

        public void WriteTo(string folder, DateTime lastWriteUtc) => Write(Path.Combine(folder, _label + TraceFormat.Extension), lastWriteUtc);

        public void WriteRealm(string path, DateTime lastWriteUtc) => Write(path, lastWriteUtc);

        public void Dispose() => _gameServer?.Dispose();

        private void Write(string path, DateTime lastWriteUtc)
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                file.Write(TraceFormat.Header(TraceSource.Bot, _start, _label, _gameName));
                var header = new byte[TraceFormat.RecordHeaderSize];
                foreach (var record in _records.OrderBy(r => r.Elapsed))
                {
                    TraceFormat.WriteRecordHeader(header, record.Kind, record.Elapsed.Ticks, record.Payload.Length);
                    file.Write(header);
                    file.Write(record.Payload);
                }
            }

            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }
    }

    private sealed class SidecarWriter(DateTime start, string game)
    {
        private readonly List<object> _entries = [];

        public void Typed(double at, string kind, string client, params (string Key, object Value)[] fields)
            => _entries.Add(new TraceEvent(start.AddSeconds(at), kind, client, game, fields));

        public void Log(double at, LogEventLevel level, string template, params (string Name, object Value)[] properties)
            => Log(at, level, template, null, properties);

        public void Log(double at, LogEventLevel level, string template, Exception exception, params (string Name, object Value)[] properties)
            => _entries.Add(new LogEvent(
                new DateTimeOffset(start.AddSeconds(at)),
                level,
                exception,
                new MessageTemplateParser().Parse(template),
                properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value)))));

        public void WriteTo(string path)
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            foreach (var entry in _entries.OrderBy(e => e is TraceEvent t ? t.Utc : ((LogEvent)e).Timestamp.UtcDateTime))
            {
                buffer.ResetWrittenCount();
                json.Reset(buffer);
                if (entry is TraceEvent typed)
                {
                    TraceSidecar.Render(json, typed);
                }
                else
                {
                    TraceSidecar.Render(json, (LogEvent)entry);
                }

                json.Flush();
                file.Write(buffer.WrittenSpan);
                file.WriteByte((byte)'\n');
            }
        }
    }

    public static byte[] Logon(string name, CharacterClass characterClass) => SyntheticTrace.Logon(name, characterClass);

    public static byte[] AssignPlayer(uint id, CharacterClass characterClass, string name, ushort x, ushort y)
    {
        var packet = new byte[26];
        packet[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        packet[5] = (byte)characterClass;
        Encoding.ASCII.GetBytes(name).CopyTo(packet, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(22), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(24), y);
        return packet;
    }

    public static byte[] PlayerInGame(uint id, CharacterClass characterClass, string name)
    {
        var packet = new byte[26];
        packet[0] = 0x5B;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), id);
        packet[7] = (byte)characterClass;
        Encoding.ASCII.GetBytes(name).CopyTo(packet, 8);
        return packet;
    }

    public static byte[] PlayerLeft(uint id) => [0x5C, .. BitConverter.GetBytes(id)];

    public static byte[] Attribute(byte attribute, byte value) => [0x1D, attribute, value];

    /// <summary>0x95: life, mana and stamina in 15 bits each, then x and y, packed least significant bit first.</summary>
    public static byte[] LifeMana(int life, int mana, ushort x, ushort y)
    {
        var packet = new byte[13];
        packet[0] = 0x95;
        var bit = 8;
        void Put(long value, int bits)
        {
            for (var i = 0; i < bits; i++, bit++)
            {
                if (((value >> i) & 1) != 0)
                {
                    packet[bit / 8] |= (byte)(1 << (bit % 8));
                }
            }
        }

        Put(life, 15);
        Put(mana, 15);
        Put(0, 15);
        Put(x, 16);
        Put(y, 16);
        return packet;
    }

    public static byte[] SetSkill(uint unit, Hand hand, Skill skill)
    {
        var packet = new byte[13];
        packet[0] = 0x23;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), unit);
        packet[6] = (byte)hand;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7), (ushort)skill);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(9), uint.MaxValue);
        return packet;
    }

    public static byte[] Location(byte type, ushort x, ushort y) => [type, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y)];

    public static byte[] Unit(byte type, EntityType entityType, uint id) => [type, .. BitConverter.GetBytes((uint)entityType), .. BitConverter.GetBytes(id)];

    public static byte[] Reassign(uint id, ushort x, ushort y) => [0x15, 0x00, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), 0x01];

    public static byte[] MapReveal(ushort x, ushort y, Area area) => [0x07, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), (byte)area];

    public static byte[] AssignNpc(uint id, NPCCode code, ushort x, ushort y)
    {
        var packet = new byte[46];
        packet[0] = 0x56;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)code);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), y);
        packet[11] = 0x80;
        return packet;
    }

    public static byte[] NpcMove(uint id, ushort x, ushort y)
    {
        var packet = new byte[15];
        packet[0] = 0x67;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        packet[5] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), y);
        return packet;
    }

    public static byte[] MonsterStop(uint id, ushort x, ushort y) => SyntheticTrace.MonsterStop(id, x, y);

    public static byte[] NpcHit(uint id, byte lifePercent) => [0x0C, 0x01, .. BitConverter.GetBytes(id), 0x06, 0x00, (byte)Math.Min(127, lifePercent * 1.28)];

    public static byte[] NpcState(uint id, EntityState state, ushort x, ushort y, byte life)
        => [0x69, .. BitConverter.GetBytes(id), (byte)state, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), life, 0x00];

    /// <summary>0x6C as the 1.09 protocol notes lay it out: attacker, attack type, target id, target type, x, y.</summary>
    public static byte[] NpcAttack(uint attacker, uint target, ushort x, ushort y)
        => [0x6C, .. BitConverter.GetBytes(attacker), 0x01, 0x00, .. BitConverter.GetBytes(target), 0x00, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y)];

    public static byte[] SkillOnPoint(uint caster, Skill skill, ushort x, ushort y)
        => [0x4D, 0x01, .. BitConverter.GetBytes(caster), .. BitConverter.GetBytes((ushort)skill), 0x00, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), 0x00, 0x00];

    public static byte[] Remove(EntityType type, uint id) => [0x0A, (byte)type, .. BitConverter.GetBytes(id)];

    public static byte[] AssignObject(uint id, EntityCode code, ushort x, ushort y)
        => [0x51, 0x02, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes((ushort)code), .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), 0x00, 0x00];

    public static byte[] PortalOwner(uint owner, string name, uint portal)
    {
        var packet = new byte[29];
        packet[0] = 0x82;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), owner);
        Encoding.ASCII.GetBytes(name).CopyTo(packet, 5);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(21), portal);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(25), portal + 1);
        return packet;
    }

    public static byte[] QuestInfo(uint unit, params (int Word, ushort Value)[] words)
    {
        var packet = new byte[103];
        packet[0] = 0x28;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), unit);
        foreach (var (word, value) in words)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7 + word * 2), value);
        }

        return packet;
    }

    public static byte[] CorpseAssign(uint player, uint corpse) => [0x8E, 0x01, .. BitConverter.GetBytes(player), .. BitConverter.GetBytes(corpse)];

    public static byte[] Item(Core.D2GS.Items.Action action, uint id)
    {
        var packet = (byte[])HoradricCube.Clone();
        packet[1] = (byte)action;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), id);
        return packet;
    }

    private static byte[] McpRequest(byte type, byte[] body)
    {
        var packet = new byte[3 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
        packet[2] = type;
        body.CopyTo(packet, 3);
        return packet;
    }

    private static byte[] CreateRequest(string game)
        => McpRequest(0x03, [0x01, 0x00, 0x00, 0x20, 0x00, 0x00, 0x01, 0xFF, 0x08, .. Encoding.ASCII.GetBytes(game + "\0"), 0x00, 0x00]);

    private static byte[] McpResult(byte type, uint result, int offset)
    {
        var packet = new byte[offset + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
        packet[2] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset), result);
        return packet;
    }
}
