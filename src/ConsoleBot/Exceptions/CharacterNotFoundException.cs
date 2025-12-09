using System;

namespace ConsoleBot.Exceptions;

public class CharacterNotFoundException : Exception
{

    public CharacterNotFoundException(string characterName) : base($"Character with name '{characterName}' was not found")
    {
    }
}
