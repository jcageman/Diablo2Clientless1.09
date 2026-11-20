using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace D2NG.Trace;

/// <summary>
/// A game's worth of valid game server packets for tests and benchmarks: a necromancer joins a Hell
/// game in the Rogue Encampment, two monsters are assigned, then monster stops, hits and life updates
/// repeat. Recorded through the live receive path, so digests are taken as a bot takes them.
/// </summary>
public static class SyntheticTrace
{
    public const string CharacterName = "nomancer";

    public static readonly uint MapId = 0x5C1B3173;

    /// <summary>The logon as the bot sends it, built by the same packet class.</summary>
    public static byte[] Logon() => Logon(CharacterName, CharacterClass.Necromancer);

    public static byte[] Logon(string name, CharacterClass characterClass) => Logon(name, characterClass, 0x0102);

    /// <summary>The logon a client sends to the game server, carrying the game's token from the realm's join answer.</summary>
    public static byte[] Logon(string name, CharacterClass characterClass, ushort gameToken)
    {
        var stats = new byte[32];
        stats[13] = (byte)(characterClass + 1);
        return new GameLogonPacket(0x12345678, gameToken, new Core.MCP.Character(name, stats)).Raw;
    }

    public static IEnumerable<byte[]> Joining()
    {
        yield return [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        yield return [0x03, 0x00, 0x73, 0x31, 0x1B, 0x5C, 0x01, 0x00, 0xB0, 0x00, 0x50, 0x24];
        yield return [0x59, 0x01, 0x00, 0x00, 0x00, 0x02, 0x6E, 0x6F, 0x6D, 0x61, 0x6E, 0x63, 0x65,
            0x72, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xE0, 0x00, 0x00, 0x00, 0x00];
        yield return [0x56, 0xE9, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x17, 0x1F, 0x79, 0x20, 0x33, 0x01, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xB0, 0x9D, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x11, 0x1A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        yield return [0x56, 0xE5, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x0C, 0x1F, 0x70, 0x20, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        yield return LifeManaUpdate;
    }

    /// <summary>Life 1616, mana 72 at (5057, 1902).</summary>
    public static readonly byte[] LifeManaUpdate = [0x95, 0x50, 0x06, 0x24, 0x00, 0xCA, 0x20, 0x78, 0xC2, 0xED, 0x00, 0x00, 0x00];

    public static byte[] MonsterStop(uint id, ushort x, ushort y)
    {
        var packet = new byte[10];
        packet[0] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7), y);
        packet[9] = 0x80;
        return packet;
    }

    public static byte[] MonsterHit(uint id, byte life) => [0x0C, 0x01, (byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24), 0x06, 0x00, life];

    public static IEnumerable<byte[]> Playing(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = i % 2 == 0 ? 0xE9u : 0xE5u;
            yield return (i % 3) switch
            {
                0 => MonsterStop(id, (ushort)(7900 + i % 100), (ushort)(8300 + i % 50)),
                1 => MonsterHit(id, (byte)(i % 128)),
                _ => LifeManaUpdate,
            };
        }
    }

    public static string Record(TraceRecorder recorder, string gameName, int playingPackets, int flushEvery = 10_000)
    {
        var channel = recorder.OpenGame(gameName, CharacterName);
        var gameServer = new GameServer();
        var game = new Game(gameServer);
        gameServer.Trace = channel;
        gameServer.DigestSource = () => TraceDigest.Capture(game);

        var logon = Logon();
        channel.Write(TraceRecordKind.D2gsSent, logon);
        game.SelectCharacter(new Core.MCP.Character(CharacterName, CharacterStats()));
        foreach (var packet in Joining())
        {
            gameServer.OnPacketReceived(new D2gsPacket(packet));
        }

        var sent = 0;
        foreach (var packet in Playing(playingPackets))
        {
            gameServer.OnPacketReceived(new D2gsPacket(packet));
            if (++sent % flushEvery == 0)
            {
                recorder.FlushNow();
            }
        }

        channel.Close();
        recorder.FlushNow();
        recorder.FlushNow();
        return channel.Path;
    }

    private static byte[] CharacterStats()
    {
        var stats = new byte[32];
        stats[13] = 3;
        return stats;
    }
}
