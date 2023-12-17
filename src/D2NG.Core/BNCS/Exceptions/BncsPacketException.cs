using System;

namespace D2NG.Core.BNCS.Exceptions
{
    public class BncsPacketException : Exception
    {
        public BncsPacketException()
        {
        }

        public BncsPacketException(string message) : base(message)
        {
        }
    }
}