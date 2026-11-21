using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace D2NG.Trace.Tests;

/// <summary>Writes small trace files, sidecars and realm traces by hand, with packets from the packet classes' own layouts.</summary>
internal sealed class HandTrace(string label, string game, DateTime startUtc, string serverGame = null)
{
    private readonly List<TraceRecord> _records = [];

    public DateTime StartUtc => startUtc;

    public HandTrace Received(double at, byte[] packet) => Add(TraceRecordKind.D2gsReceived, at, packet);

    public HandTrace Sent(double at, byte[] packet) => Add(TraceRecordKind.D2gsSent, at, packet);

    public HandTrace Add(TraceRecordKind kind, double at, byte[] payload)
    {
        _records.Add(new TraceRecord(kind, TimeSpan.FromSeconds(at), payload));
        return this;
    }

    /// <summary>Logon (with the token), game loading, act data in the Rogue Encampment, the town tile holding the character, the character placed and its life.</summary>
    public HandTrace Join(uint id, ushort token = 0x0102, ushort x = 5057, ushort y = 1902, int life = 1000, CharacterClass characterClass = CharacterClass.Sorceress)
    {
        Sent(0, SyntheticTrace.Logon(label, characterClass, token));
        Received(0.01, [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        Received(0.02, [0x03, 0x00, 0x73, 0x31, 0x1B, 0x5C, 0x01, 0x00, 0xB0, 0x00, 0x50, 0x24]);
        Received(0.03, SyntheticGame.AssignPlayer(id, characterClass, label, x, y));
        Received(0.04, Packets.MapReveal(x, y, Area.RogueEncampment));
        Received(0.05, [0x1F, 0x07, .. BitConverter.GetBytes(life << 8)]);
        Received(0.06, SyntheticGame.LifeMana(life, 100, x, y));
        return this;
    }

    public string WriteTo(string folder, string fileName = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, (fileName ?? label) + TraceFormat.Extension);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(TraceFormat.Header(TraceSource.Bot, startUtc, label, game, serverGame));
        var header = new byte[TraceFormat.RecordHeaderSize];
        foreach (var record in _records.OrderBy(r => r.Elapsed))
        {
            TraceFormat.WriteRecordHeader(header, record.Kind, record.Elapsed.Ticks, record.Payload.Length);
            file.Write(header);
            file.Write(record.Payload);
        }

        return path;
    }

    public static string DateFolder(string root, DateTime utc)
        => Path.Combine(root, utc.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
}

/// <summary>A sidecar of one process: typed events and log lines.</summary>
internal sealed class SidecarFile(DateTime startUtc)
{
    private readonly List<(DateTime At, object Entry)> _entries = [];

    public SidecarFile Typed(double at, string kind, string client, string game, params (string Key, object Value)[] fields)
    {
        _entries.Add((startUtc.AddSeconds(at), new TraceEvent(startUtc.AddSeconds(at), kind, client, game, fields)));
        return this;
    }

    public SidecarFile Log(double at, LogEventLevel level, string template, params (string Name, object Value)[] properties)
    {
        _entries.Add((startUtc.AddSeconds(at), new LogEvent(new DateTimeOffset(startUtc.AddSeconds(at)), level, null, new MessageTemplateParser().Parse(template),
            properties.Select(p => new LogEventProperty(p.Name, new Serilog.Events.ScalarValue(p.Value))))));
        return this;
    }

    public string WriteTo(string dateFolder, string process)
    {
        Directory.CreateDirectory(dateFolder);
        var path = Path.Combine(dateFolder, TraceSidecar.FilePrefix + process + TraceSidecar.Extension);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        foreach (var (_, entry) in _entries.OrderBy(e => e.At))
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

        return path;
    }
}

internal static class Packets
{
    public static byte[] McpRequest(byte type, byte[] body)
    {
        var packet = new byte[3 + body.Length];
        BitConverter.GetBytes((ushort)packet.Length).CopyTo(packet, 0);
        packet[2] = type;
        body.CopyTo(packet, 3);
        return packet;
    }

    public static byte[] JoinRequest(string game) => McpRequest(0x04, [0x02, 0x00, .. System.Text.Encoding.ASCII.GetBytes(game + "\0"), 0x00]);

    /// <summary>The join answer as JoinGameResponsePacket reads it: request id, token, unknown, address, hash, result.</summary>
    public static byte[] JoinAnswer(uint result) => McpRequest(0x04, [0x02, 0x00, 0x09, 0x00, 0x00, 0x00, 0x7F, 0x00, 0x00, 0x01, 0x11, 0x22, 0x33, 0x44, .. BitConverter.GetBytes(result)]);

    public static byte[] CharacterLogon(string name) => McpRequest(0x07, [.. System.Text.Encoding.ASCII.GetBytes(name + "\0")]);

    public static byte[] Pickup(uint item) => [0x16, 0x04, 0x00, 0x00, 0x00, .. BitConverter.GetBytes(item), 0x00, 0x00, 0x00, 0x00];

    public static byte[] Insert(uint item) => [0x18, .. BitConverter.GetBytes(item), .. BitConverter.GetBytes(3u), .. BitConverter.GetBytes(3u), .. BitConverter.GetBytes(0u)];

    public static byte[] Interact(uint npc) => SyntheticGame.Unit(0x13, Core.D2GS.Objects.EntityType.NPC, npc);

    public static byte[] MapReveal(ushort x, ushort y, Area area) => SyntheticGame.MapReveal((ushort)(x / 5), (ushort)(y / 5), area);
}
