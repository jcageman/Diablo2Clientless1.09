using D2NG.Core.D2GS.Items;
using D2NG.Pickit.Nip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ConsoleBot.Mule;

/// <summary>
/// Decides which mule account takes an item: the account's nip file, where the item goes when any
/// line matches. Every account must name one, and every named file is read up front, so a missing
/// name, a missing file or an empty one stops the bot at startup instead of sending items to the
/// wrong place.
/// </summary>
public sealed class MuleItemRules
{
    private readonly MuleConfiguration _config;
    private readonly Dictionary<string, IReadOnlyList<NipRule>> _files = new(StringComparer.OrdinalIgnoreCase);

    public MuleItemRules(MuleConfiguration config)
    {
        _config = config;
        foreach (var account in config.Accounts ?? [])
        {
            if (string.IsNullOrEmpty(account.Nip))
            {
                throw new InvalidDataException($"Mule account {account.Username} names no nip file; every account needs \"nip\"");
            }

            Rules(account.Nip);
        }

        if (!string.IsNullOrEmpty(config.NeverMuleNip))
        {
            Rules(config.NeverMuleNip);
        }
    }

    public bool Accepts(MuleAccount account, Item item)
    {
        return Matches(account.Nip, item);
    }

    public bool NeverMule(Item item)
    {
        return !string.IsNullOrEmpty(_config.NeverMuleNip) && Matches(_config.NeverMuleNip, item);
    }

    private bool Matches(string file, Item item)
    {
        var context = new NipEvaluationContext(item)
        {
            KeepRuleSources = () => D2NG.Pickit.Pickit.KeepRuleSources(item)
        };
        return Rules(file).Any(rule => rule.MatchesExpression(context));
    }

    private IReadOnlyList<NipRule> Rules(string file)
    {
        lock (_files)
        {
            if (_files.TryGetValue(file, out var rules))
            {
                return rules;
            }

            var path = Path.IsPathRooted(file) ? file : Path.Combine(_config.NipDirectory ?? string.Empty, file);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Mule nip file {path} does not exist; set mule.nipDirectory or fix the file name", path);
            }

            rules = NipParser.ParseFile(path);
            if (rules.Count == 0)
            {
                throw new InvalidDataException($"Mule nip file {path} holds no rules");
            }

            _files[file] = rules;
            return rules;
        }
    }
}
