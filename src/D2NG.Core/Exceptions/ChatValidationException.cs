using System;

namespace D2NG.Core.Exceptions
{
    public class ChatValidationException : Exception
    {
        public ChatValidationException()
        {
        }

        public ChatValidationException(string message) : base(message)
        {
        }
    }
}