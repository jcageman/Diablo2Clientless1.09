using System;

namespace D2NG.Core.D2GS.Exceptions;

public class D2GSPacketException : Exception
{
    public D2GSPacketException()
    {
    }

    public D2GSPacketException(string message) : base(message)
    {
    }
}