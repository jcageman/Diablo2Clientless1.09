using System;
using System.Collections.Generic;
using System.Text;

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
