using D2NG.Core.MCP.Exceptions;
using System;
using System.IO;
using System.Text;

namespace D2NG.Core.MCP.Packet;

/// <summary>
/// Result of a conversion to expansion. BNETDocs gives a single result dword: 0x00 converted, 0x7A
/// failed, 0x7B character not found, 0x7C already expansion. Read from the end of the packet, because the
/// realm's delete answer turned out to carry a request id the documentation leaves out.
/// </summary>
internal class UpgradeCharacterResponsePacket : McpPacket
{
    public UpgradeCharacterResponsePacket(McpPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
        if (Raw.Length != reader.ReadUInt16())
        {
            throw new McpPacketException("Packet length does not match");
        }
        if (Mcp.CHARUPGRADE != (Mcp)reader.ReadByte())
        {
            throw new McpPacketException("Expected Packet Type Not Found");
        }
        if (Raw.Length < 7)
        {
            throw new McpPacketException("Packet too short for a result");
        }
        Result = BitConverter.ToUInt32(Raw, Raw.Length - 4);
    }

    public uint Result { get; }

    public bool Success => Result == 0x00;
}
