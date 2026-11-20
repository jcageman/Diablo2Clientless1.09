using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ConsoleBot.Escape;

/// <summary>
/// The accounts that are ours: farmers, party members and mules. Anyone else whispering one of our
/// characters or joining one of our games has noticed a bot.
/// </summary>
public sealed class KnownAccounts
{
    public const string FileName = "known-accounts.json";

    private readonly HashSet<string> _accounts;

    public KnownAccounts(IEnumerable<string> accounts)
    {
        _accounts = accounts.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _accounts.Count;

    public static KnownAccounts Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var accounts = document.RootElement.GetProperty("knownAccounts").EnumerateArray().Select(a => a.GetString());
        return new KnownAccounts(accounts);
    }

    public bool IsKnown(string account) => account != null && _accounts.Contains(account.Trim());

    /// <summary>
    /// The account behind a battle.net whisper. The realm names the sender "Character*Account"; a
    /// sender without a character logged on comes as "*Account".
    /// </summary>
    public static string AccountOfSender(string sender)
    {
        if (string.IsNullOrEmpty(sender))
        {
            return sender;
        }

        var star = sender.IndexOf('*');
        return star < 0 ? sender : sender[(star + 1)..];
    }
}
