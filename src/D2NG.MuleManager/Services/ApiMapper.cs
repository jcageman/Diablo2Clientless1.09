using D2NG.Mule.Models;
using D2NG.Mule.Upgrades;
using D2NG.MuleManager.Contracts;
using D2NG.MuleManager.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.MuleManager.Services;

public static class ApiMapper
{
    /// <summary>Items counted per kind instead of listed: they come in many identical copies and carry no stats.</summary>
    public static readonly HashSet<string> Stacked =
    [
        "Gem", "Rune", "HealthPotion", "ManaPotion", "RejuvenationPotion", "StaminaPotion", "AntidotePotion", "ThawingPotion",
        "ThrowingPotion", "Elixir", "Essence", "Token", "Key", "Reagent", "Herb", "BodyPart", "Ear", "Scroll", "Tome", "Gold",
    ];

    /// <summary>Potions are never worth showing, and runes do nothing in a classic game.</summary>
    public static bool Relevant(MuleItemDb item, GameMode mode)
        => !IsStacked(item) || !((item.ClassificationType ?? "").EndsWith("Potion", StringComparison.Ordinal) || (mode == GameMode.Classic && item.ClassificationType == "Rune"));

    /// <summary>The packet repeats damage as secondary and throwing lines; a copy with the plain line's value says nothing new.</summary>
    private static string DamageCopyOf(string stat) => stat switch
    {
        "SecondaryMinimumDamage" or "MinimumThrowingDamage" => "MinimumDamage",
        "SecondaryMaximumDamage" or "MaximumThrowingDamage" => "MaximumDamage",
        _ => null,
    };

    private static string Format(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsStacked(MuleItemDb item) => Stacked.Contains(item.ClassificationType ?? "") && (item.Stats == null || item.Stats.Count == 0);

    public static bool InMode(bool? expansion, GameMode mode) => expansion == (mode == GameMode.Expansion);

    public static ItemDto Item(MuleItemDb item, GearTables tables)
    {
        var allSkills = item.Stats?.Any(s => s.Type == "AllSkills") == true;
        var stats = (item.Stats ?? [])
            .Where(s => s.Value != 0 || s.PerLevel != 0)
            .Where(s => !allSkills || !GearItem.ClassSkills.Contains(s.Type))
            .Where(s => DamageCopyOf(s.Type) is not { } plain || !(item.Stats ?? []).Any(p => p.Type == plain && p.Value == s.Value))
            .Select(s =>
            {
                var range = tables?.RangeOf(item, s.Type);
                var value = s.PerLevel != 0 ? s.PerLevel / 8.0 : s.Value;
                var text = s.PerLevel != 0
                    ? $"{StatText.Line(s.Type.EndsWith("PerLevel", StringComparison.Ordinal) ? s.Type[..^"PerLevel".Length] : s.Type, value)} per Character Level"
                    : StatText.Line(s.Type, value);
                return new StatLineDto(s.Type, StatText.Short(s.Type), text, value, range?.Min, range?.Max);
            })
            .ToList();
        foreach (var element in new[] { "Fire", "Lightning", "Cold" })
        {
            var min = stats.FindIndex(l => l.Stat == $"Minimum{element}Damage");
            var max = stats.FindIndex(l => l.Stat == $"Maximum{element}Damage");
            if (min >= 0 && max >= 0)
            {
                stats[min] = stats[min] with { Text = $"Adds {Format(stats[min].Value)}-{Format(stats[max].Value)} {element} Damage" };
                stats.RemoveAt(max);
            }
        }

        return new ItemDto(
            item.Id,
            tables?.DisplayName(item) ?? StatText.Humanize(item.ItemName ?? ""),
            tables?.BaseName(item) ?? StatText.Humanize(item.ItemName ?? ""),
            item.QualityType,
            item.ClassificationType,
            item.Ethereal,
            (int)item.Sockets,
            (int)item.Level,
            tables?.RequiredLevel(item),
            item.AccountName,
            item.CharacterName,
            item.Container,
            item.X,
            item.Y,
            item.Expansion,
            stats,
            tables?.SetNameOf(item));
    }

    public static EvaluationDto Evaluation(GearEvaluation evaluation) => new(
        evaluation.Total("Life"),
        evaluation.Total("Mana"),
        evaluation.Total("Strength"),
        evaluation.Total("Dexterity"),
        evaluation.Total("Cast Frames"),
        evaluation.Total("Hit Recovery Frames"),
        evaluation.Total("Block %"),
        [.. evaluation.Resists.Values.Select(r => new ResistDto(r.Element, r.Raw, r.Effective, r.Cap, r.OverCap))],
        [.. evaluation.Sets.Select(s => new SetBonusDto(s.Name, s.Worn, s.Total, [.. s.Stats.Select(b => StatText.Line(b.Stat, b.Value))]))],
        evaluation.Totals.TryGetValue("Attack Frames", out var attack) ? attack : null);

    public static PresetDto Preset(UpgradePreset preset) => new(
        preset.Id,
        preset.Name,
        preset.Description,
        preset.Mode.ToString(),
        [.. preset.Weights.Select(w => new StatWeightDto(w.Stat, w.Weight, w.Cap, w.Slot))],
        preset.ResistWeights,
        preset.ColdOverCap);

    public static UpgradePreset WithWeights(UpgradePreset preset, List<StatWeightDto> weights)
    {
        if (weights == null)
        {
            return preset;
        }

        return new UpgradePreset
        {
            Id = preset.Id,
            Name = preset.Name,
            Description = preset.Description,
            DefaultFor = preset.DefaultFor,
            Mode = preset.Mode,
            ResistWeights = preset.ResistWeights,
            Weapons = preset.Weapons,
            ColdOverCap = preset.ColdOverCap,
            Weights = [.. weights.Select(w => new StatWeight { Stat = w.Stat, Weight = w.Weight, Cap = w.Cap, Slot = w.Slot })],
        };
    }

    public static RefreshJobDto Job(RefreshJob job) => new(
        job.Id,
        job.Description,
        job.State.ToString(),
        job.QueuedAt,
        job.StartedAt,
        job.FinishedAt,
        [.. job.Accounts.Select(a => new AccountProgressDto(a.Account, a.Kind.ToString(), a.State.ToString(), a.Characters, a.Recorded, a.Skipped, a.Failed, a.Current, a.Problem))]);

    public static GameMode ParseMode(string mode)
        => string.Equals(mode, "expansion", StringComparison.OrdinalIgnoreCase) ? GameMode.Expansion : GameMode.Classic;
}
