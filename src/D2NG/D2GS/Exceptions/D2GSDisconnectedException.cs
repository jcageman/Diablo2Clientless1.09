using System;
using System.Runtime.Serialization;

namespace D2NG.D2GS.Exceptions
{
    [Serializable]
    public class D2GSDisconnectedException : Exception
    {
        public D2GSDisconnectedException()
        {
        }

        public D2GSDisconnectedException(string message) : base(message)
        {
        }

        public D2GSDisconnectedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected D2GSDisconnectedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}