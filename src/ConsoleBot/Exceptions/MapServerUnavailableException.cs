using System;

namespace ConsoleBot.Exceptions;

/// <summary>
/// Thrown when the map API cannot be reached. The bot cannot path anywhere without it, so this is
/// fatal: it stops the bot instead of letting it restart into the same failure.
/// </summary>
public class MapServerUnavailableException : Exception
{
    public MapServerUnavailableException()
    {
    }

    public MapServerUnavailableException(string message) : base(message)
    {
    }

    public MapServerUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
