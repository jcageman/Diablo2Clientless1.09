using System;
using System.Runtime.Serialization;

namespace ConsoleBot.Exceptions
{
    [Serializable]
    public class CharacterNotFoundException : Exception
    {
        public CharacterNotFoundException()
        {
        }

        public CharacterNotFoundException(string message) : base(message)
        {
        }

        public CharacterNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected CharacterNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
