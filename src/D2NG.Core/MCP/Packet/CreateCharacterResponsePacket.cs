using D2NG.Core.MCP.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.MCP.Packet;

/// <summary>
/// Result of a character creation request. Captured values: 0x00 for success and 0x14 for a name the
/// realm refuses, which two captures showed for names that already existed while the very same class
/// and flags succeeded under a free name.
/// </summary>
internal class CreateCharacterResponsePacket : McpPacket
{
    public const uint NameUnavailable = 0x14;

    public CreateCharacterResponsePacket(McpPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
        if (Raw.Length != reader.ReadUInt16())
        {
            throw new McpPacketException("Packet length does not match");
        }
        if (Mcp.CHARCREATE != (Mcp)reader.ReadByte())
        {
            throw new McpPacketException("Expected Packet Type Not Found");
        }
        Result = reader.ReadUInt32();
    }

    public uint Result { get; }

    public bool Success => Result == 0x00;
}
