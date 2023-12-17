using System;

namespace D2NG.Core.BNCS.Exceptions
{
    public class AuthCheckResponseException : Exception
    {
        public AuthCheckResponseException()
        {
        }

        public AuthCheckResponseException(string message) : base(message)
        {
        }
    }
}