using D2NG.Core.D2GS.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cuber;

/// <summary>
/// An item on a mule character, as the mule database last saw it.
/// </summary>
public sealed record HeldItem(string Account, string Character, Item Item);

public sealed record PlannedTransmute(CubeRecipe Recipe, int Count);

/// <summary>
/// One mule character that has to join and hand over <see cref="Items"/>.
/// </summary>
public sealed record GiverPlan(string Account, string Character, List<Item> Items);

public sealed record CubePlan(List<PlannedTransmute> Transmutes, List<GiverPlan> Givers)
{
    public bool IsEmpty => Transmutes.Count == 0;
}

/// <summary>
/// Decides from the mule database which transmutes one cube run makes and which mule characters
/// hand over which ingredients. Only whole transmutes are collected, so nothing is fetched that
/// then sits unused on the cube character.
/// </summary>
public static class CubePlanner
{
    /// <param name="maxItems">Most ingredients one run collects; what the cube character can hold at once.</param>
    /// <param name="isFinished">For <see cref="CubeRecipe.RerollUntilKept"/> recipes: an item already good enough to keep, which is not rerolled.</param>
    /// <param name="canHandOver">For <see cref="CubeRecipe.RerollUntilKept"/> recipes: whether the mule can get the item to the trade screen at all.</param>
    public static CubePlan Plan(IReadOnlyList<CubeRecipe> recipes, IReadOnlyList<HeldItem> held, int maxItems, Func<Item, bool> isFinished = null, Func<HeldItem, bool> canHandOver = null)
    {
        // Mules holding many ingredients are asked first, so a run needs as few logins as possible.
        var ingredientsPerMule = held
            .Where(h => recipes.Any(r => r.Requirements.Any(q => q.Matches(h.Item))))
            .GroupBy(Owner)
            .ToDictionary(g => g.Key, g => g.Count());
        var used = new bool[held.Count];
        var joining = new HashSet<(string, string)>();
        var taken = new List<HeldItem>();
        var transmutes = new List<PlannedTransmute>();

        foreach (var recipe in recipes)
        {
            if (recipe.RerollUntilKept)
            {
                var rerolls = PlanRerolls(recipe, held, used, joining, ingredientsPerMule, maxItems - taken.Count, isFinished, canHandOver);
                foreach (var index in rerolls.Picked)
                {
                    used[index] = true;
                    joining.Add(Owner(held[index]));
                    taken.Add(held[index]);
                }

                if (rerolls.Count > 0)
                {
                    transmutes.Add(new PlannedTransmute(recipe, rerolls.Count));
                }

                continue;
            }

            var perTransmute = recipe.Requirements.Sum(r => r.Amount);
            var count = 0;
            while (taken.Count + perTransmute <= maxItems)
            {
                var picked = PickOneTransmute(recipe, held, used, joining, ingredientsPerMule);
                if (picked == null)
                {
                    break;
                }

                foreach (var index in picked)
                {
                    used[index] = true;
                    joining.Add(Owner(held[index]));
                    taken.Add(held[index]);
                }

                count++;
            }

            if (count > 0)
            {
                transmutes.Add(new PlannedTransmute(recipe, count));
            }
        }

        var givers = taken
            .GroupBy(Owner)
            .Select(g => new GiverPlan(g.Key.Account, g.Key.Character, g.Select(h => h.Item).ToList()))
            .ToList();
        return new CubePlan(transmutes, givers);
    }

    /// <summary>
    /// One item to reroll, and the other ingredients for as many rerolls of it as fit: the output of
    /// each transmute is the input of the next.
    /// </summary>
    private static (List<int> Picked, int Count) PlanRerolls(
        CubeRecipe recipe,
        IReadOnlyList<HeldItem> held,
        bool[] used,
        HashSet<(string, string)> joining,
        Dictionary<(string, string), int> ingredientsPerMule,
        int room,
        Func<Item, bool> isFinished,
        Func<HeldItem, bool> canHandOver)
    {
        var target = recipe.Requirements[0];
        var others = recipe.Requirements.Skip(1).ToList();
        var perReroll = others.Sum(r => r.Amount);
        var item = Enumerable.Range(0, held.Count)
            .Where(i => !used[i] && target.Matches(held[i].Item) && !(isFinished?.Invoke(held[i].Item) ?? false) && (canHandOver?.Invoke(held[i]) ?? true))
            .OrderByDescending(i => held[i].Item.Quality != QualityType.Normal && held[i].Item.Quality != QualityType.Superior)
            .Cast<int?>()
            .FirstOrDefault();
        if (item == null || target.Amount != 1 || room < 1 + perReroll)
        {
            return ([], 0);
        }

        var picked = new List<int> { item.Value };
        var count = 0;
        while (picked.Count + perReroll <= room && count < recipe.MaxRerollsPerRun)
        {
            var set = new List<int>();
            foreach (var requirement in others)
            {
                set.AddRange(Enumerable.Range(0, held.Count)
                    .Where(i => !used[i] && !picked.Contains(i) && !set.Contains(i) && requirement.Matches(held[i].Item))
                    .OrderByDescending(i => joining.Contains(Owner(held[i])) || picked.Any(p => Owner(held[p]) == Owner(held[i])))
                    .ThenByDescending(i => ingredientsPerMule[Owner(held[i])])
                    .ThenBy(i => held[i].Account)
                    .ThenBy(i => held[i].Character)
                    .Take(requirement.Amount));
            }

            if (set.Count < perReroll)
            {
                break;
            }

            picked.AddRange(set);
            count++;
        }

        return count == 0 ? ([], 0) : (picked, count);
    }

    private static List<int> PickOneTransmute(
        CubeRecipe recipe,
        IReadOnlyList<HeldItem> held,
        bool[] used,
        HashSet<(string, string)> joining,
        Dictionary<(string, string), int> ingredientsPerMule)
    {
        var picked = new List<int>();
        foreach (var requirement in recipe.Requirements)
        {
            var unused = Enumerable.Range(0, held.Count)
                .Where(i => !used[i] && !picked.Contains(i) && requirement.Matches(held[i].Item))
                .ToList();
            if (unused.Count - requirement.Reserve < requirement.Amount)
            {
                return null;
            }

            var candidates = unused
                .OrderByDescending(i => joining.Contains(Owner(held[i])))
                .ThenByDescending(i => ingredientsPerMule[Owner(held[i])])
                .ThenBy(i => held[i].Account)
                .ThenBy(i => held[i].Character)
                .Take(requirement.Amount)
                .ToList();
            if (candidates.Count < requirement.Amount)
            {
                return null;
            }

            picked.AddRange(candidates);
        }

        return picked;
    }

    private static (string Account, string Character) Owner(HeldItem item)
    {
        return (item.Account, item.Character);
    }
}
