using System;

namespace D2NG.Core.D2GS.Exceptions
{
    public class D2GSDisconnectedException : Exception
    {
        public D2GSDisconnectedException()
        {
        }

        public D2GSDisconnectedException(string message) : base(message)
        {
        }
    }
}