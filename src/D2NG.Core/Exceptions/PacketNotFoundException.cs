using System;

namespace D2NG.Core.Exceptions;

public class PacketNotFoundException : Exception
{
    public PacketNotFoundException()
    {
    }

    public PacketNotFoundException(string message) : base(message)
    {
    }
}