using D2NG.Core.MCP.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.MCP.Packet;

/// <summary>
/// Result of a character deletion. Captured successes carry a request id word followed by a zero
/// result dword.
/// </summary>
internal class DeleteCharacterResponsePacket : McpPacket
{
    public DeleteCharacterResponsePacket(McpPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
        if (Raw.Length != reader.ReadUInt16())
        {
            throw new McpPacketException("Packet length does not match");
        }
        if (Mcp.CHARDELETE != (Mcp)reader.ReadByte())
        {
            throw new McpPacketException("Expected Packet Type Not Found");
        }
        RequestId = reader.ReadUInt16();
        Result = reader.ReadUInt32();
    }

    public ushort RequestId { get; }

    public uint Result { get; }

    public bool Success => Result == 0x00;
}
