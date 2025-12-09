using System;

namespace ConsoleBot.Exceptions;

public class InvalidDiabloFolderException : RankException
{
    public InvalidDiabloFolderException()
    {
    }

    public InvalidDiabloFolderException(string message) : base(message)
    {
    }
}
