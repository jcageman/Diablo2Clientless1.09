using D2NG.Core.D2GS.Items;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Mule;

/// <summary>
/// Picks the mule character for an item on accounts with
/// <see cref="MuleAccount.PlaceWithSameClassification"/>: the one already holding the most items of
/// the same classification, so one character ends up with the gloves, another with the amulets.
/// Ties go to the character listed first.
/// </summary>
public static class MulePlacement
{
    /// <param name="characters">The characters still to visit, in visiting order.</param>
    /// <param name="held">Per character, how many items of each classification it holds.</param>
    public static string BestCharacter(
        ClassificationType classification,
        IReadOnlyList<string> characters,
        IReadOnlyDictionary<string, Dictionary<ClassificationType, int>> held)
    {
        string best = null;
        var bestCount = -1;
        foreach (var character in characters)
        {
            var count = held.TryGetValue(character, out var counts) ? counts.GetValueOrDefault(classification) : 0;
            if (count > bestCount)
            {
                best = character;
                bestCount = count;
            }
        }

        return best;
    }
}
