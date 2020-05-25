using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace ConsoleBot
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
