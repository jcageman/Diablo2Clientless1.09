using System.Globalization;
using System.IO;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit;

/// <summary>
/// The outcome of a pickit decision together with the reason for it, so a run can be audited
/// against the nip rules that produced it.
/// </summary>
/// <param name="Result">Whether the item should be picked up or kept.</param>
/// <param name="Reason">
/// The deciding nip rule as <c>file:line</c>, or a short explanation when no rule was involved.
/// </param>
public readonly record struct PickitVerdict(bool Result, string Reason)
{
    /// <summary>A nip rule matched the item.</summary>
    public static PickitVerdict Matched(NipRule rule)
    {
        var location = string.Create(
            CultureInfo.InvariantCulture,
            $"{Path.GetFileName(rule.Source)}:{rule.Line}");

        return new PickitVerdict(true, location);
    }

    /// <summary>No nip rule matched the item, so it is left alone.</summary>
    public static PickitVerdict NoRule()
    {
        return new PickitVerdict(false, "no matching rule");
    }

    /// <summary>Unidentified items are kept regardless of the rules, so they can be identified.</summary>
    public static PickitVerdict Unidentified()
    {
        return new PickitVerdict(true, "unidentified");
    }
}
