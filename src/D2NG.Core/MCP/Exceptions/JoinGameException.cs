using System;

namespace D2NG.Core.MCP.Exceptions;

public class JoinGameException : Exception
{
    public JoinGameException()
    {
    }

    public JoinGameException(string message) : base(message)
    {
    }
}