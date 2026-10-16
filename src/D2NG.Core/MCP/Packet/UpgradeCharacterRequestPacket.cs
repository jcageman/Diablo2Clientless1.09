using System;
using System.Text;

namespace D2NG.Core.MCP.Packet;

/// <summary>
/// Converts a classic character to expansion, what the character screen's "Convert to Expansion" does.
/// One way: the realm has no request to turn it back. Layout from BNETDocs: the name as a null
/// terminated string. ClassicToExpansion converts characters on the realm with it.
/// </summary>
public class UpgradeCharacterRequestPacket : McpPacket
{
    public UpgradeCharacterRequestPacket(string name) :
        base(
            BuildPacket(
                Mcp.CHARUPGRADE,
                Encoding.ASCII.GetBytes(name ?? throw new ArgumentNullException(nameof(name))),
                [0x00]
            )
        )
    {
    }
}
