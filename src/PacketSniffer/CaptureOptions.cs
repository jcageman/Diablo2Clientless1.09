using D2NG.Core.D2GS.Packet;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PacketSniffer;

public enum CaptureProtocol
{
    D2gs,
    Bncs,
    Mcp
}

/// <summary>
/// Command line options controlling what the sniffer writes to its log, given as key=value pairs in
/// the same style as the bot itself, for example <c>device=3 preset=quest raw=true</c>.
/// </summary>
/// <remarks>
/// Type filtering only suppresses log lines, it never skips decoding: the incoming D2GS and BNCS
/// streams are stateful, so not parsing a packet corrupts every packet after it. A protocol turned
/// off entirely is dropped before it reaches a stream, which is safe precisely because nothing
/// decodes it for the whole run.
/// </remarks>
public sealed class CaptureOptions
{
    /// <summary>
    /// Everything needed to reverse engineer a quest: the quest state pushes, the NPC dialog and
    /// entity interactions that drive them, act and warp transitions, corpse assignment, and the
    /// outgoing packets a real client sends while completing one.
    /// </summary>
    private const string QuestPreset =
        "in:QuestInfo,in:GameQuestInfo,in:QuestLogInfo,in:SpecialQuestEvent,in:WaypointMenu," +
        "in:NPCWantInteract,in:NPCInfo,in:NPCTransaction,in:PlayerCorpseAssign,in:CorpseAssign," +
        "in:LoadAct,in:LoadActComplete,in:UnloadActComplete,in:AssignLevelWarp,in:AllyPartyInfo," +
        "in:PlayerInGame,in:AssignPlayer,in:ReportKill,in:ButtonAction,in:TownPortalState," +
        "in:PortalOwner," +
        "out:QuestMessage,out:RequestQuestData,out:QuestComplete,out:ActivateInifussScroll," +
        "out:Resurrect,out:EntityAction,out:EntityInteract,out:InitiateEntityChat," +
        "out:TerminateEntityChat,out:AddStatPoint,out:AddSkillPoint,out:TakeWaypoint," +
        "out:PlayNPCMessage,out:ClickButton,out:InsertHoradricStaff";

    /// <summary>
    /// The noisiest movement, combat and state chatter, for captures that want everything except
    /// the spam. Applied through <c>preset=quiet</c>.
    /// </summary>
    private const string NoisePreset =
        "in:EntityMove,in:NPCMove,in:NPCMoveToTarget,in:PlayerStop,in:PlayerToTarget,in:NPCHit," +
        "in:NPCAttack,in:NPCAction,in:ObjectState,in:AddEntityEffect,in:AddEntityEffect2," +
        "in:UpdateEntityEffects,in:MapReveal,in:MapHide,in:PlaySound,in:Pong," +
        "out:Walk,out:Run,out:WalkToUnit,out:RunToUnit,out:Ping,out:RequestEntityUpdate," +
        "out:UpdatePlayerLocation";

    public static CaptureOptions Current { get; private set; } = new();

    public int? DeviceIndex { get; private set; }

    /// <summary>
    /// Print the available capture devices and exit, so an index can be picked without entering the
    /// interactive prompt.
    /// </summary>
    public bool ListDevices { get; private set; }

    public int D2gsPort { get; private set; } = 4000;

    public int BncsPort { get; private set; } = 6112;

    public int McpPort { get; private set; } = 6113;

    /// <summary>
    /// Whether to log the raw undecoded TCP payload of every captured packet. Off by default: it is
    /// the single biggest source of log spam and largely duplicates what the decoders print.
    /// </summary>
    public bool LogRawPayloads { get; private set; }

    public bool LogIncoming { get; private set; } = true;

    public bool LogOutgoing { get; private set; } = true;

    public HashSet<CaptureProtocol> Protocols { get; private set; } =
        [CaptureProtocol.D2gs, CaptureProtocol.Bncs, CaptureProtocol.Mcp];

    private readonly HashSet<byte> _includeIncoming = [];
    private readonly HashSet<byte> _includeOutgoing = [];
    private readonly HashSet<byte> _excludeIncoming = [];
    private readonly HashSet<byte> _excludeOutgoing = [];

    public static void Parse(string[] args)
    {
        var options = new CaptureOptions();
        foreach (var argument in args ?? [])
        {
            var separator = argument.IndexOf('=');
            if (separator <= 0)
            {
                Log.Warning("Ignoring argument without a value: {Argument}", argument);
                continue;
            }

            var key = argument[..separator].Trim().ToLowerInvariant();
            var value = argument[(separator + 1)..].Trim();
            switch (key)
            {
                case "device":
                    if (int.TryParse(value, out var device))
                    {
                        options.DeviceIndex = device;
                    }
                    else
                    {
                        Log.Warning("Ignoring non numeric device {Value}", value);
                    }
                    break;
                case "devices":
                case "list":
                    options.ListDevices = ParseBool(value, true);
                    break;
                case "port":
                case "d2gsport":
                    options.D2gsPort = ParsePort(value, options.D2gsPort);
                    break;
                case "bncsport":
                    options.BncsPort = ParsePort(value, options.BncsPort);
                    break;
                case "mcpport":
                    options.McpPort = ParsePort(value, options.McpPort);
                    break;
                case "raw":
                    options.LogRawPayloads = ParseBool(value, options.LogRawPayloads);
                    break;
                case "direction":
                    options.SetDirection(value);
                    break;
                case "protocols":
                    options.SetProtocols(value);
                    break;
                case "preset":
                    options.ApplyPreset(value);
                    break;
                case "include":
                    AddTokens(value, options._includeIncoming, options._includeOutgoing);
                    break;
                case "exclude":
                    AddTokens(value, options._excludeIncoming, options._excludeOutgoing);
                    break;
                default:
                    Log.Warning("Ignoring unknown argument {Key}", key);
                    break;
            }
        }

        Current = options;
    }

    public bool ShouldLog(CaptureProtocol protocol, bool incoming)
    {
        return Protocols.Contains(protocol) && (incoming ? LogIncoming : LogOutgoing);
    }

    public bool ShouldLogIncomingD2gs(byte packetType)
    {
        return ShouldLog(CaptureProtocol.D2gs, true)
            && !_excludeIncoming.Contains(packetType)
            && (_includeIncoming.Count == 0 || _includeIncoming.Contains(packetType));
    }

    public bool ShouldLogOutgoingD2gs(byte packetType)
    {
        return ShouldLog(CaptureProtocol.D2gs, false)
            && !_excludeOutgoing.Contains(packetType)
            && (_includeOutgoing.Count == 0 || _includeOutgoing.Contains(packetType));
    }

    /// <summary>
    /// Berkeley packet filter limiting the capture to the ports of the enabled protocols, so a
    /// disabled protocol never even reaches the handler.
    /// </summary>
    public string BuildPcapFilter()
    {
        var ports = new List<int>();
        if (Protocols.Contains(CaptureProtocol.D2gs))
        {
            ports.Add(D2gsPort);
        }

        if (Protocols.Contains(CaptureProtocol.Bncs))
        {
            ports.Add(BncsPort);
        }

        if (Protocols.Contains(CaptureProtocol.Mcp))
        {
            ports.Add(McpPort);
        }

        if (ports.Count == 0)
        {
            ports.Add(D2gsPort);
        }

        return "tcp port " + string.Join(" or ", ports);
    }

    public string Describe()
    {
        var protocols = string.Join(",", Protocols.Select(p => p.ToString().ToLowerInvariant()).Order());
        var direction = LogIncoming && LogOutgoing ? "both" : LogIncoming ? "in" : "out";
        return $"device={DeviceIndex?.ToString() ?? "prompt"} d2gsPort={D2gsPort} protocols={protocols} " +
            $"direction={direction} raw={LogRawPayloads}" +
            $"{DescribeSet("include in", _includeIncoming, true)}" +
            $"{DescribeSet("include out", _includeOutgoing, false)}" +
            $"{DescribeSet("exclude in", _excludeIncoming, true)}" +
            $"{DescribeSet("exclude out", _excludeOutgoing, false)}";
    }

    private void SetDirection(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "in":
            case "incoming":
                LogIncoming = true;
                LogOutgoing = false;
                break;
            case "out":
            case "outgoing":
                LogIncoming = false;
                LogOutgoing = true;
                break;
            case "both":
                LogIncoming = true;
                LogOutgoing = true;
                break;
            default:
                Log.Warning("Ignoring unknown direction {Value}, keeping both", value);
                break;
        }
    }

    private void SetProtocols(string value)
    {
        var protocols = new HashSet<CaptureProtocol>();
        foreach (var token in Split(value))
        {
            if (Enum.TryParse<CaptureProtocol>(token, true, out var protocol))
            {
                protocols.Add(protocol);
            }
            else
            {
                Log.Warning("Ignoring unknown protocol {Token}", token);
            }
        }

        if (protocols.Count == 0)
        {
            Log.Warning("No valid protocols given, keeping the current ones");
            return;
        }

        Protocols = protocols;
    }

    private void ApplyPreset(string value)
    {
        foreach (var preset in Split(value))
        {
            switch (preset.ToLowerInvariant())
            {
                case "quest":
                    AddTokens(QuestPreset, _includeIncoming, _includeOutgoing);
                    Protocols = [CaptureProtocol.D2gs];
                    break;
                case "quiet":
                    AddTokens(NoisePreset, _excludeIncoming, _excludeOutgoing);
                    break;
                case "all":
                    _includeIncoming.Clear();
                    _includeOutgoing.Clear();
                    break;
                default:
                    Log.Warning("Ignoring unknown preset {Preset}, expected quest, quiet or all", preset);
                    break;
            }
        }
    }

    private static void AddTokens(string value, HashSet<byte> incoming, HashSet<byte> outgoing)
    {
        foreach (var rawToken in Split(value))
        {
            var token = rawToken;
            var forIncoming = true;
            var forOutgoing = true;
            if (token.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
            {
                token = token[3..];
                forOutgoing = false;
            }
            else if (token.StartsWith("out:", StringComparison.OrdinalIgnoreCase))
            {
                token = token[4..];
                forIncoming = false;
            }

            if (TryParseId(token, out var id))
            {
                if (forIncoming)
                {
                    incoming.Add(id);
                }

                if (forOutgoing)
                {
                    outgoing.Add(id);
                }

                continue;
            }

            var matched = false;
            if (forIncoming && Enum.TryParse<InComingPacket>(token, true, out var incomingPacket))
            {
                incoming.Add((byte)incomingPacket);
                matched = true;
            }

            if (forOutgoing && Enum.TryParse<OutGoingPacket>(token, true, out var outgoingPacket))
            {
                outgoing.Add((byte)outgoingPacket);
                matched = true;
            }

            if (!matched)
            {
                Log.Warning("Ignoring unknown packet {Token}, expected a packet name or an id like 0x9C", rawToken);
            }
        }
    }

    private static bool TryParseId(string token, out byte id)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        }

        return byte.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static string DescribeSet(string label, HashSet<byte> packetTypes, bool incoming)
    {
        if (packetTypes.Count == 0)
        {
            return "";
        }

        var names = packetTypes
            .Order()
            .Select(t => incoming ? DescribeType<InComingPacket>(t) : DescribeType<OutGoingPacket>(t));
        return $" {label}=[{string.Join(",", names)}]";
    }

    private static string DescribeType<TPacket>(byte packetType) where TPacket : struct, Enum
    {
        return Enum.IsDefined(typeof(TPacket), packetType)
            ? ((TPacket)Enum.ToObject(typeof(TPacket), packetType)).ToString()
            : $"0x{packetType,2:X2}";
    }

    private static int ParsePort(string value, int fallback)
    {
        if (int.TryParse(value, out var port) && port is > 0 and <= 65535)
        {
            return port;
        }

        Log.Warning("Ignoring invalid port {Value}, keeping {Port}", value, fallback);
        return fallback;
    }

    private static bool ParseBool(string value, bool fallback)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value switch
        {
            "1" or "yes" or "on" => true,
            "0" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static string[] Split(string value)
    {
        return value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
