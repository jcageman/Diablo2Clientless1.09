using System;
using System.Runtime.Serialization;

namespace ConsoleBot.Exceptions
{
    [Serializable]
    public class CharacterNotFoundException : Exception
    {

        public CharacterNotFoundException(string characterName) : base($"Character with name '{characterName}' was not found")
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
