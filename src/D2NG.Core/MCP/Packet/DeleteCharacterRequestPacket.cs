using System;
using System.Text;

namespace D2NG.Core.MCP.Packet;

/// <summary>
/// Deletes a character from the realm. Captured from a real 1.09 client as a request id word followed
/// by the name as a null terminated string, which lets a test run clean up the throwaway characters
/// it created instead of leaving them on the account.
/// </summary>
public class DeleteCharacterRequestPacket : McpPacket
{
    public DeleteCharacterRequestPacket(ushort requestId, string name) :
        base(
            BuildPacket(
                Mcp.CHARDELETE,
                BitConverter.GetBytes(requestId),
                Encoding.ASCII.GetBytes(name ?? throw new ArgumentNullException(nameof(name))),
                [0x00]
            )
        )
    {
    }
}
