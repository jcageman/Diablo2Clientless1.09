using System;

namespace D2NG.Core.MCP.Exceptions;

public class CharLogonException : Exception
{
    public CharLogonException()
    {
    }

    public CharLogonException(string message) : base(message)
    {
    }
}