using System;
using System.Runtime.Serialization;

namespace ConsoleBot.Exceptions
{
    [Serializable]
    public class InvalidDiabloFolderException : RankException
    {
        public InvalidDiabloFolderException()
        {
        }

        public InvalidDiabloFolderException(string message) : base(message)
        {
        }

        public InvalidDiabloFolderException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected InvalidDiabloFolderException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
