using System;

namespace D2NG.Core.BNCS.Exceptions;

public class UnknownAuthCheckResultException : Exception
{
    public UnknownAuthCheckResultException()
    {
    }

    public UnknownAuthCheckResultException(string message) : base(message)
    {
    }
}