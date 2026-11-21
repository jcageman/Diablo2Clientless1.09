using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Core.MCP.Packet;
using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace D2NG.Trace.Query;

public static class Names
{
    private static readonly string[] IncomingNames = [.. Enumerable.Range(0, 256).Select(t => Enum.IsDefined(typeof(InComingPacket), (byte)t) ? ((InComingPacket)t).ToString() : $"0x{t:X2}")];

    private static readonly string[] OutgoingNames = [.. Enumerable.Range(0, 256).Select(t => Enum.IsDefined(typeof(OutGoingPacket), (byte)t) ? ((OutGoingPacket)t).ToString() : $"0x{t:X2}")];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NPCCode, string> NpcNames = new();

    public static string Incoming(byte type) => IncomingNames[type];

    public static string Outgoing(byte type) => OutgoingNames[type];

    public static string Npc(NPCCode code) => NpcNames.GetOrAdd(code, static c => Enum.IsDefined(c) ? c.ToString() : $"npc{(ushort)c}");

    public static string Skill(ushort skill) => Enum.IsDefined(typeof(Skill), (int)skill) ? ((Skill)skill).ToString() : $"skill{skill}";

    public static string Area(Area area) => Enum.IsDefined(area) ? area.ToString() : $"area{(int)area}";

    /// <summary>A position; (0, 0) is what a character holds before the server has placed it.</summary>
    public static string Point(Point point) => point == null ? "-" : point.X == 0 && point.Y == 0 ? "(not placed yet)" : $"({point.X}, {point.Y})";

    /// <summary>
    /// A skill a unit used. Monsters use skills named after their missile, which reads as something else
    /// (a Hydra's is Hireablemissile), so a monster's skill says whose it is.
    /// </summary>
    public static string SkillOf(ushort skill, EntityType casterType, string casterCode)
        => casterType == EntityType.Player || casterCode == null ? Skill(skill) : $"{casterCode}'s skill {Skill(skill)}";

    public static string Id(uint id) => $"0x{id:X}";
}

/// <summary>
/// One line per packet: the packet classes of D2NG.Core decode where they exist, a few layouts the
/// bot never parses are read here, and anything else is shown as hex.
/// </summary>
public static class PacketDecoder
{
    private const int HexShown = 48;

    private static readonly Dictionary<byte, Func<D2gsPacket, object>> Incoming = new()
    {
        [0x03] = p => new ActDataPacket(p),
        [0x07] = p => new MapRevealPacket(p),
        [0x09] = p => new AssignLevelWarpPacket(p),
        [0x0A] = p => new RemoveObjectPacket(p),
        [0x0C] = p => new NpcHitPacket(p),
        [0x0D] = p => new PlayerStopPacket(p),
        [0x0E] = p => new ObjectStatePacket(p),
        [0x0F] = p => new EntityMovePacket(p),
        [0x12] = p => new UpdateEntityEffectsPacket(p),
        [0x13] = p => new AddEntityEffectPacket(p),
        [0x14] = p => new AddEntityEffectPacket2(p),
        [0x15] = p => new ReassignPlayerPacket(p),
        [0x18] = p => new LifeManaPotUpdatePacket(p),
        [0x1A] = p => new AddExpPacket(p),
        [0x1B] = p => new AddExpPacket(p),
        [0x1C] = p => new AddExpPacket(p),
        [0x1D] = p => new BaseAttributePacket(p),
        [0x1E] = p => new BaseAttributePacket(p),
        [0x1F] = p => new BaseAttributePacket(p),
        [0x21] = p => new SetItemSkillPacket(p),
        [0x22] = p => new SetItemSkillPacket(p),
        [0x23] = p => new SetActiveSkillPacket(p),
        [0x26] = p => new ChatPacket(p),
        [0x27] = p => new NpcInfoPacket(p),
        [0x2A] = p => new NpcTransactionPacket(p),
        [0x3E] = p => new UpdateItemStatsPacket(p),
        [0x3F] = p => new UseStackableItemPacket(p),
        [0x51] = p => new AssignObjectPacket(p),
        [0x55] = p => new AssignNpcPacket(p),
        [0x56] = p => new AssignNpcPacket(p),
        [0x59] = p => new AssignPlayerPacket(p),
        [0x5A] = p => new EventNotifyPacket(p),
        [0x5B] = p => new PlayerInGamePacket(p),
        [0x5C] = p => new PlayerLeftGamePacket(p),
        [0x60] = p => new TownPortalStatePacket(p),
        [0x63] = p => new WaypointMenuPacket(p),
        [0x67] = p => new NPCMovePacket(p),
        [0x68] = p => new NPCMoveToTargetPacket(p),
        [0x69] = p => new NpcStatePacket(p),
        [0x6D] = p => new NPCStopPacket(p),
        [0x77] = p => new ButtonActionPacket(p),
        [0x7A] = p => new PetActionPacket(p),
        [0x7F] = p => new AllyPartyInfoPacket(p),
        [0x81] = p => new AssignMercPacket(p),
        [0x82] = p => new PortalOwnerPacket(p),
        [0x8D] = p => new AssignPlayerToPartyPacket(p),
        [0x8E] = p => new CorpseAssignPacket(p),
        [0x90] = p => new PartyAutomapInfoPacket(p),
        [0x94] = p => new BaseSkillLevelsPacket(p),
        [0x95] = p => new LifeManaUpdatePacket(p),
        [0x96] = p => new WalkVerifyPacket(p),
    };

    public static string Name(TraceRecordKind kind, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return "empty";
        }

        return kind switch
        {
            TraceRecordKind.D2gsReceived => Names.Incoming(payload[0]),
            TraceRecordKind.D2gsSent => Names.Outgoing(payload[0]),
            TraceRecordKind.McpReceived or TraceRecordKind.McpSent when payload.Length > 2
                => Enum.IsDefined(typeof(Mcp), payload[2]) ? "MCP " + (Mcp)payload[2] : $"MCP 0x{payload[2]:X2}",
            TraceRecordKind.BncsReceived or TraceRecordKind.BncsSent when payload.Length > 1
                => Enum.IsDefined(typeof(Sid), payload[1]) ? "SID " + (Sid)payload[1] : $"SID 0x{payload[1]:X2}",
            _ => kind.ToString(),
        };
    }

    public static string Fields(TraceRecordKind kind, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return "";
        }

        try
        {
            return kind switch
            {
                TraceRecordKind.D2gsReceived => IncomingFields(payload),
                TraceRecordKind.D2gsSent => OutgoingFields(payload),
                TraceRecordKind.McpSent or TraceRecordKind.McpReceived => McpFields(kind, payload),
                TraceRecordKind.BncsSent or TraceRecordKind.BncsReceived => $"{payload.Length} bytes",
                _ => Hex(payload),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"{Hex(payload)} (undecodable: {e.Message})";
        }
    }

    private static string IncomingFields(byte[] payload)
    {
        var type = payload[0];
        switch (type)
        {
            case 0x9C or 0x9D:
                var item = new ParseItemPacket(new D2gsPacket(payload)).Item;
                return $"id={Names.Id(item.Id)} action={item.Action} name={item.Name} quality={item.Quality} container={item.Container} at={Names.Point(item.Location)} owner={Names.Id(item.PlayerId)}";
            case 0x28 or 0x29:
                return $"words: {QuestWords(payload, type == 0x28 ? 7 : 1)}";
            case 0x4C when payload.Length >= 16:
                return $"unit={UnitType(payload[1])} {Names.Id(U32(payload, 2))} skill={Names.Skill(U16(payload, 6))} target={UnitType(payload[9])} {Names.Id(U32(payload, 10))} (layout from 1.09 protocol notes)";
            case 0x4D when payload.Length >= 13:
                return $"unit={UnitType(payload[1])} {Names.Id(U32(payload, 2))} skill={Names.Skill(U16(payload, 6))} at=({U16(payload, 9)}, {U16(payload, 11)}) (layout from 1.09 protocol notes)";
            case 0x6C when payload.Length >= 16:
                return $"unit={Names.Id(U32(payload, 1))} attack={U16(payload, 5)} target={UnitType(payload[11])} {Names.Id(U32(payload, 7))} at=({U16(payload, 12)}, {U16(payload, 14)}) (layout from 1.09 protocol notes)";
            case 0x0D when payload.Length >= 13:
                return $"unit={UnitType(payload[1])} {Names.Id(U32(payload, 2))} byte6={payload[6]} location=({U16(payload, 7)}, {U16(payload, 9)}) byte11={payload[11]} life={payload[12]}%";
            case 0x6B when payload.Length >= 6:
                return $"unit={Names.Id(U32(payload, 1))} action={payload[5]} {Hex(payload[6..])}";
        }

        if (!Incoming.TryGetValue(type, out var decode))
        {
            return Hex(payload);
        }

        var packet = decode(new D2gsPacket(payload));
        var fields = Properties(packet);
        if (type == 0x68 && payload.Length >= 15)
        {
            fields += $" target={UnitType(payload[10])} {Names.Id(U32(payload, 11))}";
        }

        return fields;
    }

    private static string OutgoingFields(byte[] payload)
    {
        var type = payload[0];
        return type switch
        {
            0x01 or 0x03 or 0x05 or 0x08 or 0x0C or 0x0F or 0x5F when payload.Length >= 5 => $"at=({U16(payload, 1)}, {U16(payload, 3)})",
            0x02 or 0x04 or 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11 or 0x13 or 0x2F or 0x30 or 0x4B or 0x59 when payload.Length >= 9
                => $"unit={UnitType((byte)U32(payload, 1))} {Names.Id(U32(payload, 5))}",
            0x16 when payload.Length >= 9 => $"item={Names.Id(U32(payload, 5))}",
            0x17 or 0x19 or 0x20 or 0x24 or 0x26 or 0x27 or 0x2A or 0x37 or 0x63 when payload.Length >= 5 => $"item={Names.Id(U32(payload, 1))}",
            0x32 when payload.Length >= 13 => $"npc={Names.Id(U32(payload, 1))} item={Names.Id(U32(payload, 5))}{((payload[9] & 0x02) != 0 ? " gamble" : "")}",
            0x18 when payload.Length >= 17 => $"item={Names.Id(U32(payload, 1))} at=({U32(payload, 5)}, {U32(payload, 9)}) buffer={U32(payload, 13)}",
            0x1A when payload.Length >= 7 => $"item={Names.Id(U32(payload, 1))} slot={U16(payload, 5)}",
            0x1F or 0x20 when payload.Length >= 13 => $"item={Names.Id(U32(payload, 1))} at=({U32(payload, 5)}, {U32(payload, 9)})",
            0x31 when payload.Length >= 9 => $"unit={Names.Id(U32(payload, 1))} message={Names.Id(U32(payload, 5))}",
            0x33 or 0x32 when payload.Length >= 9 => $"npc={Names.Id(U32(payload, 1))} item={Names.Id(U32(payload, 5))}",
            0x1C or 0x61 when payload.Length >= 3 => $"slot={(Core.D2GS.Items.DirectoryType)U16(payload, 1)}",
            0x38 when payload.Length >= 13 => $"action={U32(payload, 1)} npc={Names.Id(U32(payload, 5))} data={U32(payload, 9)}",
            0x3C when payload.Length >= 5 => $"skill={Names.Skill(U16(payload, 1))} hand={(U16(payload, 3) == 0x8000 ? "left" : "right")}",
            0x49 when payload.Length >= 9 => $"waypoint={Names.Id(U32(payload, 1))} level={U32(payload, 5)}",
            0x15 when payload.Length > 3 => $"text=\"{Encoding.ASCII.GetString(payload, 3, Math.Max(0, Array.IndexOf(payload, (byte)0, 3) - 3))}\"",
            0x65 or 0x68 when payload.Length > 13 => $"character={Encoding.ASCII.GetString(payload, 13, Math.Max(0, (Array.IndexOf(payload, (byte)0, 13) is var end && end < 0 ? payload.Length : end) - 13))} class={payload[7]}",
            _ => payload.Length == 1 ? "" : Hex(payload),
        };
    }

    private static string McpFields(TraceRecordKind kind, byte[] payload)
    {
        if (payload.Length < 3)
        {
            return Hex(payload);
        }

        var sent = kind == TraceRecordKind.McpSent;
        return (payload[2], sent) switch
        {
            (0x03, true) when payload.Length >= 12 => $"game={Text(payload, 12)} difficulty={(Difficulty)(payload[6] >> 4)}",
            (0x03, false) when payload.Length >= 13 => Result(U32(payload, 9), RealmAnswer.CreateMeaning),
            (0x04, true) => $"game={Text(payload, 5)}",
            (0x04, false) when payload.Length >= 21 => Result(U32(payload, 17), RealmAnswer.JoinMeaning),
            (0x07, true) => $"character={Text(payload, 3)}",
            (0x07, false) when payload.Length >= 7 => Result(U32(payload, 3), CharacterLogonMeaning),
            (0x01, false) when payload.Length >= 7 => Result(U32(payload, 3), r => r == 0 ? "ok" : "refused"),
            _ => Hex(payload),
        };
    }

    private static string CharacterLogonMeaning(uint result) => result switch
    {
        0x00 => "ok",
        0x46 => "character not found",
        0x7A => "logon failed",
        0x7B => "character expired",
        _ => "unknown reason",
    };

    private static string Result(uint result, Func<uint, string> meaning) => $"result=0x{result:X2} ({meaning(result)})";

    private static string QuestWords(byte[] payload, int offset)
    {
        var words = new List<string>();
        for (var word = 0; offset + word * 2 + 1 < payload.Length && word < 48; word++)
        {
            var value = (ushort)(payload[offset + word * 2] | (payload[offset + word * 2 + 1] << 8));
            if (value != 0)
            {
                words.Add($"{word}=0x{value:X4}");
            }
        }

        return words.Count == 0 ? "all zero" : string.Join(" ", words);
    }

    private static string UnitType(byte type) => type switch
    {
        0 => "player",
        1 => "npc",
        2 => "object",
        3 => "missile",
        4 => "item",
        5 => "warp",
        _ => $"type{type}",
    };

    internal static uint U32(byte[] payload, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));

    internal static ushort U16(byte[] payload, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));

    private static string Text(byte[] payload, int offset)
    {
        if (offset >= payload.Length)
        {
            return "";
        }

        var end = Array.IndexOf(payload, (byte)0, offset);
        return Encoding.ASCII.GetString(payload, offset, (end < 0 ? payload.Length : end) - offset);
    }

    public static string Hex(byte[] payload)
        => payload.Length <= HexShown
            ? Convert.ToHexString(payload)
            : $"{Convert.ToHexString(payload, 0, HexShown)}.. ({payload.Length} bytes)";

    private static string Properties(object packet)
    {
        var parts = new List<string>();
        foreach (var property in packet.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name is "Raw" or "Type" || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            parts.Add($"{char.ToLowerInvariant(property.Name[0])}{property.Name[1..]}={Value(property.GetValue(packet))}");
        }

        return string.Join(" ", parts);
    }

    private static string Value(object value) => value switch
    {
        null => "-",
        Point point => Names.Point(point),
        double number => number.ToString("0.#", CultureInfo.InvariantCulture),
        uint id => Names.Id(id),
        string text => text,
        byte[] bytes => Convert.ToHexString(bytes.Length > 16 ? bytes[..16] : bytes),
        IEnumerable sequence => "[" + string.Join(",", sequence.Cast<object>().Take(8).Select(Value)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
