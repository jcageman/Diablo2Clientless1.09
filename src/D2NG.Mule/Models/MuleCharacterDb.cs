using System;

namespace D2NG.Mule.Models;

/// <summary>
/// What a mule character looked like the last time either the bot or the mule manager was in a
/// game with it.
/// </summary>
public class MuleCharacterDb
{
    public string Id { get; set; }
    public string AccountName { get; set; }
    public string CharacterName { get; set; }
    public DateTimeOffset SeenAt { get; set; }

    /// <summary>
    /// Free cells across stash and inventory after the visit. Zero means nothing else fits.
    /// </summary>
    public int FreeCells { get; set; }

    /// <summary>
    /// Per item height, the widest item that still had a spot after the visit. Null on records written before this
    /// was tracked, which reads as "anything might fit".
    /// </summary>
    public int[] FitProfile { get; set; }

    public static string MakeId(string accountName, string characterName)
    {
        return $"{MuleNames.Normalize(accountName)}/{MuleNames.Normalize(characterName)}";
    }
}
