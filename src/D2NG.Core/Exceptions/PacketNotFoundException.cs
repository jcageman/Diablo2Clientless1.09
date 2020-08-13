using System;
using System.Runtime.Serialization;

namespace D2NG.Core.Exceptions
{
    [Serializable]
    public class PacketNotFoundException : Exception
    {
        public PacketNotFoundException()
        {
        }

        public PacketNotFoundException(string message) : base(message)
        {
        }

        public PacketNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected PacketNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}