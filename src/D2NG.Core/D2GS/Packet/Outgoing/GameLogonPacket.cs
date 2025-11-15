using D2NG.Core.MCP;
using System;
using System.Linq;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class GameLogonPacket : D2gsPacket
{
    private const int Version = 0x09;

    private static readonly byte[] Locale = { 0x00 };

    private static readonly byte[] PostFixBytes = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE4, 0xBB, 0xAA, 0x6F, 0x4B, 0x00, 0x00, 0x00, 0x4C };

    public GameLogonPacket(uint gameHash, ushort gameToken, Character character) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.GameLogon,
                BitConverter.GetBytes(gameHash),
                BitConverter.GetBytes(gameToken),
                new[] { (byte)character.Class },
                BitConverter.GetBytes(Version),
                Locale,
                Encoding.ASCII.GetBytes(character.Name),
                new byte[] { 0x00 },
                PostFixBytes.TakeLast(15 - character.Name.Length)
            )
        )
    {
    }
}