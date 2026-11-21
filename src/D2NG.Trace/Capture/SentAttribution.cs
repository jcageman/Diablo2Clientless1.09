using D2NG.Core;
using D2NG.Core.Capture;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Capture;

/// <summary>One game server connection of a text capture, with the game its received packets built.</summary>
internal sealed class CapturedClient(int instance, CaptureConnection id, DateTime openedUtc, DateTime lastUtc)
{
    public int Instance { get; } = instance;
    public CaptureConnection Id { get; } = id;
    public DateTime OpenedUtc { get; } = openedUtc;

    /// <summary>When the capture last saw the server send on this connection.</summary>
    public DateTime LastUtc { get; } = lastUtc;

    public LiveClient Live { get; } = new(openedUtc);
    public DateTime LastSentUtc { get; set; } = DateTime.MinValue;
    public DateTime LastPingUtc { get; set; } = DateTime.MinValue;
    public DateTime LogonUtc { get; set; } = DateTime.MinValue;

    /// <summary>The game token of the logon: clients with the same one are in the same game, where unit ids mean the same units.</summary>
    public ushort? Token { get; set; }

    /// <summary>The NPC this client last interacted with or started a chat with, and when.</summary>
    public (uint Npc, DateTime Utc)? Chat { get; set; }
}

/// <summary>
/// The sniffer's text log names no connection for what a client sent, so a sent segment is given to one of
/// the game connections open at the time. Strongest is what the server did next, which only the sender
/// sees: a pong after a ping, an NPC's offer after an interact, a waypoint menu, an item packet naming the
/// item, and what the other clients of the game saw that client do (its move, its skill). Then what the
/// packet refers to: a logon goes to the newest connection, a chat continues with the client already talking
/// to that NPC, a unit or item to the client whose game knows it, a point to the client the server last placed
/// nearest, a skill to the class that has it, a leave to the connection that ends next. Without evidence the
/// client that sent last keeps it. The live sniffer knows the connection and needs none of this.
/// </summary>
internal sealed class SentAttribution
{
    private const double Margin = 1.0;
    private static readonly TimeSpan ChatMemory = TimeSpan.FromSeconds(20);

    public Dictionary<string, long> Methods { get; } = [];

    /// <summary>Location sends far from where the chosen client stood, and skills of another class: attribution that is probably wrong.</summary>
    public long Inconsistent { get; private set; }

    /// <param name="following">What every client received in the moments after the send, in capture order.</param>
    public CapturedClient Choose(IReadOnlyList<CapturedClient> open, byte[] segment, DateTime atUtc, IReadOnlyList<(CapturedClient Client, byte[] Packet)> following = null)
    {
        if (open.Count == 0)
        {
            Count("no connection open");
            return null;
        }

        var packets = Split(segment);
        if (packets.Count > 0 && packets[0][0] == 0x65)
        {
            var newest = open.Where(c => !c.Live.LoggedOn).OrderByDescending(c => c.OpenedUtc).FirstOrDefault()
                ?? open.OrderByDescending(c => c.OpenedUtc).First();
            newest.LogonUtc = atUtc;
            if (packets[0].Length >= 7)
            {
                newest.Token = U16(packets[0], 5);
            }

            return Chosen(newest, "logon to the newest connection", atUtc, packets);
        }

        if (open.Count == 1)
        {
            return Chosen(open[0], "only connection open", atUtc, packets);
        }

        following ??= [];
        var positional = packets.Select(p => Positional(open, p)).ToList();
        var scores = open
            .Select(client => (Client: client, Score: packets.Select((p, i) => Score(client, p, atUtc) + Replies(client, p, following) + positional[i].GetValueOrDefault(client)).Sum() + Sticky(client, atUtc)))
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.Client.LastSentUtc).ToList();
        var evidence = scores[0].Score - scores[1].Score >= Margin;
        return Chosen(scores[0].Client, evidence ? "evidence" : NoEvidence, atUtc, packets);
    }

    public const string NoEvidence = "no evidence, the client that sent last";

    /// <summary>Whether the last <see cref="Choose"/> had no evidence and fell back to the client that sent last.</summary>
    public bool LastWithoutEvidence { get; private set; }

    private CapturedClient Chosen(CapturedClient client, string method, DateTime atUtc, List<byte[]> packets)
    {
        LastWithoutEvidence = method == NoEvidence;
        Count(method);
        client.LastSentUtc = atUtc;
        foreach (var p in packets)
        {
            if (p[0] == 0x6A)
            {
                client.LastPingUtc = atUtc;
            }
            else if (p[0] is 0x13 or 0x2F && p.Length >= 9 && (EntityType)U32(p, 1) == EntityType.NPC)
            {
                client.Chat = (U32(p, 5), atUtc);
            }
        }

        if (packets.Any(p => Contradicts(client, p)))
        {
            Inconsistent++;
        }

        return client;
    }

    private void Count(string method) => Methods[method] = Methods.GetValueOrDefault(method) + 1;

    private static double Sticky(CapturedClient client, DateTime atUtc)
    {
        var since = atUtc - client.LastSentUtc;
        return since <= TimeSpan.FromMilliseconds(500) ? 0.6 : since <= TimeSpan.FromSeconds(2) ? 0.3 : 0;
    }

    /// <summary>Evidence from what the server sent after the packet: to this client alone, or to its game mates about it.</summary>
    private static double Replies(CapturedClient client, byte[] p, IReadOnlyList<(CapturedClient Client, byte[] Packet)> following)
    {
        var type = p[0];
        var own = following.Where(f => f.Client == client).Select(f => f.Packet);
        var selfId = client.Live.Game.Me?.Id;
        switch (type)
        {
            case 0x6A:
                return own.Any(r => r[0] == 0x8F) ? 6 : 0;
            case 0x13 when p.Length >= 9:
                var unit = U32(p, 5);
                return own.Any(r => r[0] == 0x27 && r.Length >= 6 && U32(r, 2) == unit || r[0] == 0x63 && r.Length >= 5 && U32(r, 1) == unit) ? 8 : 0;
            case 0x40:
                return own.Any(r => r[0] == 0x28) ? 6 : 0;
            case 0x3C when p.Length >= 3:
                var skill = U16(p, 1);
                return own.Any(r => r[0] == 0x23 && r.Length >= 9 && r[1] == (byte)EntityType.Player && U32(r, 2) == selfId && U16(r, 7) == skill) ? 8 : 0;
            case 0x49:
                return own.Any(r => r[0] is 0x03 or 0x07 || r[0] == 0x15 && r.Length >= 6 && r[1] == (byte)EntityType.Player && U32(r, 2) == selfId) ? 6 : 0;
        }

        if (ItemOffset(type) is var offset and > 0 && p.Length >= offset + 4)
        {
            var item = U32(p, offset);
            return own.Any(r => r[0] is 0x9C or 0x9D && r.Length >= 8 && U32(r, 4) == item) ? 6 : 0;
        }

        if (type is 0x01 or 0x03 or 0x05 or 0x06 or 0x08 or 0x0C or 0x0D or 0x0E or 0x0F or 0x10 or 0x11 && client.Live.Game.Me is { } me && client.Token is { } token)
        {
            var seen = following.Any(f => f.Client != client && f.Client.Token == token && f.Packet[0] is 0x0F or 0x4C or 0x4D && f.Packet.Length >= 6
                && f.Packet[1] == (byte)EntityType.Player && U32(f.Packet, 2) == me.Id);
            return seen ? 4 : 0;
        }

        return 0;
    }

    private static double Score(CapturedClient client, byte[] p, DateTime atUtc)
    {
        var game = client.Live.Game;
        var type = p[0];
        if (!client.Live.LoggedOn)
        {
            return -20;
        }

        if (type is 0x2F or 0x30 or 0x31 or 0x38 or 0x59 or 0x04 && client.Chat is { } chat && atUtc - chat.Utc <= ChatMemory && p.Length >= 5
            && chat.Npc == (type switch { 0x31 => U32(p, 1), 0x38 or 0x59 when p.Length >= 9 => U32(p, 5), _ when p.Length >= 9 => U32(p, 5), _ => uint.MaxValue }))
        {
            return 6;
        }

        switch (type)
        {
            case 0x68 or 0x6B:
                return atUtc - client.LogonUtc <= TimeSpan.FromSeconds(10) ? 5 : 0;
            case 0x66:
                return client.LastUtc >= atUtc - TimeSpan.FromSeconds(1) ? 10 - Math.Min(9, (client.LastUtc - atUtc).TotalSeconds) : -5;
            case 0x6A:
                return Math.Min(3, (atUtc - client.LastPingUtc).TotalSeconds / 2);
            case 0x3C when p.Length >= 3:
                var skillClass = SkillClass(U16(p, 1));
                return skillClass < 0 || client.Live.Class < 0 ? 0 : skillClass == client.Live.Class ? 3 : -10;
            case 0x5E when p.Length >= 6:
                return game.Me?.Id == U32(p, 2) ? -10 : 0;
            case 0x50 or 0x44 when p.Length >= 5:
                return game.Me?.Id == U32(p, 1) ? 10 : -3;
            case 0x4B when p.Length >= 9:
                return (EntityType)U32(p, 1) == EntityType.Player && game.Me?.Id == U32(p, 5) ? 10 : Unit(game, (EntityType)U32(p, 1), U32(p, 5));
            case 0x59 when p.Length >= 17:
                return Unit(game, EntityType.NPC, U32(p, 5));
            case 0x02 or 0x04 or 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11 or 0x13 or 0x2F or 0x30 when p.Length >= 9:
                var targetType = (EntityType)U32(p, 1);
                if (targetType == EntityType.Player && game.Me?.Id == U32(p, 5))
                {
                    return -10;
                }

                return Unit(game, targetType, U32(p, 5));
            case 0x31 or 0x34 or 0x35 or 0x36 or 0x62 when p.Length >= 5:
                return Unit(game, EntityType.NPC, U32(p, 1));
            case 0x38 when p.Length >= 9:
                return Unit(game, EntityType.NPC, U32(p, 5));
        }

        if (ItemOffset(type) is var offset and > 0 && p.Length >= offset + 4)
        {
            return Item(game, U32(p, offset));
        }

        return 0;
    }

    /// <summary>A unit the client's game knows scores, more the nearer it stands; one it never saw counts against it.</summary>
    private static double Unit(Game game, EntityType type, uint id)
    {
        var location = SnifferClientActions.UnitLocation(game, type, id);
        if (location == null)
        {
            return -3;
        }

        var me = game.Me?.Location;
        return 3 + (me == null ? 0 : 3 / (1 + me.Distance(location) / 10));
    }

    private static double Item(Game game, uint id)
    {
        try
        {
            if (!game.Items.TryGetValue(id, out var item))
            {
                return game.CursorItem?.Id == id ? 8 : -3;
            }

            return item.Ground ? 2 : 8;
        }
        catch (NullReferenceException)
        {
            return 0;
        }
    }

    /// <summary>A click or a skill aimed this far from where a client stands was not that client's.</summary>
    private const double Reach = 150;

    /// <summary>
    /// For a packet naming a point, each placed client's score by how near it stands compared with the nearest one:
    /// the nearest gets the full weight, one twice as far half of it. A client beyond <see cref="Reach"/> while
    /// another stands within it, or ten times as far as the nearest when none does (another act), counts against.
    /// </summary>
    private static Dictionary<CapturedClient, double> Positional(IReadOnlyList<CapturedClient> open, byte[] p)
    {
        var scores = new Dictionary<CapturedClient, double>();
        var (target, scale, weight) = p[0] switch
        {
            0x01 or 0x03 or 0x05 or 0x08 or 0x0C or 0x0F or 0x5F when p.Length >= 5 => (new Point(U16(p, 1), U16(p, 3)), 15.0, 4.0),
            0x59 when p.Length >= 17 => (new Point((ushort)U32(p, 9), (ushort)U32(p, 13)), 5.0, 6.0),
            _ => (null, 0, 0),
        };
        if (target == null)
        {
            return scores;
        }

        var distances = new Dictionary<CapturedClient, double>();
        foreach (var client in open)
        {
            if (client.Live.LoggedOn && client.Live.Game.Me?.Location is { } me && (me.X != 0 || me.Y != 0))
            {
                distances[client] = me.Distance(target);
            }
        }

        if (distances.Count == 0)
        {
            return scores;
        }

        var nearest = distances.Values.Min();
        foreach (var (client, distance) in distances)
        {
            scores[client] = nearest <= Reach && distance > Reach || distance > 10 * (nearest + scale)
                ? -10
                : weight * (scale + nearest) / (scale + distance);
        }

        return scores;
    }

    /// <summary>Where the item id sits in the sent packets that carry one.</summary>
    private static int ItemOffset(byte type) => type switch
    {
        0x16 or 0x32 or 0x33 => 5,
        0x17 or 0x18 or 0x19 or 0x1A or 0x1D or 0x1F or 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x29 or 0x2A or 0x37 or 0x63 => 1,
        _ => 0,
    };

    private static bool Contradicts(CapturedClient client, byte[] p)
    {
        var game = client.Live.Game;
        switch (p[0])
        {
            case 0x3C when p.Length >= 3:
                var skillClass = SkillClass(U16(p, 1));
                return skillClass >= 0 && client.Live.Class >= 0 && skillClass != client.Live.Class;
            case 0x01 or 0x03 or 0x5F when p.Length >= 5:
                var me = game.Me?.Location;
                return me != null && (me.X != 0 || me.Y != 0) && me.Distance(new Point(U16(p, 1), U16(p, 3))) > 100;
            default:
                return false;
        }
    }

    /// <summary>The class a skill belongs to, -1 for the skills every class has.</summary>
    internal static int SkillClass(ushort skill) => skill switch
    {
        >= 6 and <= 35 => 0,
        >= 36 and <= 65 => 1,
        >= 66 and <= 95 => 2,
        >= 96 and <= 125 => 3,
        >= 126 and <= 155 => 4,
        _ => -1,
    };

    /// <summary>A sent segment's packets, cut as the capture cuts them.</summary>
    internal static List<byte[]> Split(byte[] segment)
    {
        var packets = new List<byte[]>();
        var stream = new D2gsClientStream();
        stream.Append(segment, packets);
        stream.Flush(packets);
        return packets;
    }

    private static uint U32(byte[] p, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(offset));

    private static ushort U16(byte[] p, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(offset));
}
