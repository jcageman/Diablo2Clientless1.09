using System;

namespace D2NG.Core.BNCS.Exceptions;

public class LogonFailedException : Exception
{
    public LogonFailedException()
    {
    }

    public LogonFailedException(string message) : base(message)
    {
    }
}