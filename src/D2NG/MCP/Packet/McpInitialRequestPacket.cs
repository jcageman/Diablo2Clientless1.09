namespace D2NG.MCP.Packet
{
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
}
