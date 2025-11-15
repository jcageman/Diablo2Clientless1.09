using System;

namespace D2NG.Core.MCP.Exceptions;

public class McpPacketException : Exception
{
    public McpPacketException()
    {
    }

    public McpPacketException(string message) : base(message)
    {
    }
}