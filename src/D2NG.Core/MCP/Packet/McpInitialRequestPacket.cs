namespace D2NG.Core.MCP.Packet;

public class McpInitialRequestPacket : McpPacket
{
    public McpInitialRequestPacket()
        : base(BuildPacket(
            Mcp.STARTUP
            )
        )
    {
    }
}
