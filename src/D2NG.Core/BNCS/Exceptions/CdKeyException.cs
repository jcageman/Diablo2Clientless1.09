using System;

namespace D2NG.Core.BNCS.Exceptions
{
    public class CdKeyException : Exception
    {
        public CdKeyException()
        {
        }

        public CdKeyException(string message) : base(message)
        {
        }
    }
}