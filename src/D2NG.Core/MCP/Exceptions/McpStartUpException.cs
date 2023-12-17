using System;

namespace D2NG.Core.MCP.Exceptions
{
    public class McpStartUpException : Exception
    {
        public McpStartUpException()
        {
        }

        public McpStartUpException(string message) : base(message)
        {
        }
    }
}