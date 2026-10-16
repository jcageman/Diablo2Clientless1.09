using ConsoleBot.Bots.Types.Cuber;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Tests;

public class CubePlannerTests
{
    private static readonly CubeRecipe Ko = Recipe("Ko", ItemName.KoRune, (ItemName.LumRune, 3), (ItemName.FlawedAmethyst, 1));
    private static readonly CubeRecipe PerfectRuby = Recipe("Perfect Ruby", ItemName.PerfectRuby, (ItemName.FlawlessRuby, 3));

    private static CubeRecipe Recipe(string name, ItemName result, params (ItemName Name, int Amount)[] inputs)
    {
        return new CubeRecipe
        {
            Name = name,
            Requirements = inputs.Select(i => new RecipeRequirement { ItemNames = [i.Name], Amount = i.Amount }).ToList(),
            Result = new RecipeResult { ItemNames = [result] }
        };
    }

    private static IEnumerable<HeldItem> On(string character, ItemName name, int count)
    {
        return Enumerable.Range(0, count).Select(_ => new HeldItem("account", character, new Item { Name = name, Quality = QualityType.Normal, Properties = [] }));
    }

    private static Dictionary<string, Dictionary<ItemName, int>> Givers(CubePlan plan)
    {
        return plan.Givers.ToDictionary(
            g => g.Character,
            g => g.Items.GroupBy(i => i.Name).ToDictionary(n => n.Key, n => n.Count()));
    }

    [Fact]
    public void NothingHeld_EmptyPlan()
    {
        var plan = CubePlanner.Plan([Ko], [], 30);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Givers);
    }

    [Fact]
    public void MissingIngredient_EmptyPlan()
    {
        var plan = CubePlanner.Plan([Ko], On("runes", ItemName.LumRune, 5).ToList(), 30);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Givers);
    }

    [Fact]
    public void IngredientsOnTwoMules_BothGive()
    {
        var held = On("runes", ItemName.LumRune, 3).Concat(On("gems", ItemName.FlawedAmethyst, 1)).ToList();

        var plan = CubePlanner.Plan([Ko], held, 30);

        Assert.Equal(1, Assert.Single(plan.Transmutes).Count);
        var givers = Givers(plan);
        Assert.Equal(3, givers["runes"][ItemName.LumRune]);
        Assert.Equal(1, givers["gems"][ItemName.FlawedAmethyst]);
    }

    [Fact]
    public void OnlyWholeTransmutesAreCollected()
    {
        var held = On("runes", ItemName.LumRune, 7).Concat(On("gems", ItemName.FlawedAmethyst, 5)).ToList();

        var plan = CubePlanner.Plan([Ko], held, 30);

        Assert.Equal(2, Assert.Single(plan.Transmutes).Count);
        var givers = Givers(plan);
        Assert.Equal(6, givers["runes"][ItemName.LumRune]);
        Assert.Equal(2, givers["gems"][ItemName.FlawedAmethyst]);
    }

    [Fact]
    public void PrefersFewerMules()
    {
        var held = On("a", ItemName.LumRune, 3)
            .Concat(On("b", ItemName.LumRune, 3))
            .Concat(On("b", ItemName.FlawedAmethyst, 1))
            .ToList();

        var plan = CubePlanner.Plan([Ko], held, 30);

        Assert.Equal("b", Assert.Single(plan.Givers).Character);
    }

    [Fact]
    public void StaysWithinItemsPerRun()
    {
        var plan = CubePlanner.Plan([PerfectRuby], On("gems", ItemName.FlawlessRuby, 20).ToList(), 10);

        Assert.Equal(3, Assert.Single(plan.Transmutes).Count);
        Assert.Equal(9, Assert.Single(plan.Givers).Items.Count);
    }

    [Fact]
    public void RecipesShareTheItemsPerRun_InConfigOrder()
    {
        var held = On("runes", ItemName.LumRune, 6)
            .Concat(On("gems", ItemName.FlawedAmethyst, 2))
            .Concat(On("gems", ItemName.FlawlessRuby, 6))
            .ToList();

        var plan = CubePlanner.Plan([Ko, PerfectRuby], held, 11);

        Assert.Equal([("Ko", 2), ("Perfect Ruby", 1)], plan.Transmutes.Select(t => (t.Recipe.Name, t.Count)));
    }

    [Fact]
    public void QualityFilterIsRespected()
    {
        var magicRing = new CubeRecipe
        {
            Name = "ring",
            Requirements = [new RecipeRequirement { ItemNames = [ItemName.Ring], Quality = QualityType.Magical, Amount = 1 }],
            Result = new RecipeResult { ItemNames = [ItemName.Ring] }
        };
        var held = new List<HeldItem>
        {
            new("account", "rings", new Item { Name = ItemName.Ring, Quality = QualityType.Rare, Properties = [] })
        };

        Assert.True(CubePlanner.Plan([magicRing], held, 30).IsEmpty);
    }

    private static readonly CubeRecipe SwordReroll = new()
    {
        Name = "Colossus Sword reroll",
        RerollUntilKept = true,
        Requirements =
        [
            new RecipeRequirement { ItemNames = [ItemName.ColossusSword], Amount = 1 },
            new RecipeRequirement { ItemNames = [ItemName.ChippedRuby, ItemName.ChippedSkull], Amount = 3 }
        ],
        Result = new RecipeResult { ItemNames = [ItemName.ColossusSword], Quality = QualityType.Magical }
    };

    private static HeldItem Sword(string character, QualityType quality, int enhancedDamage = 0)
    {
        var item = new Item { Name = ItemName.ColossusSword, Quality = quality, Properties = [] };
        item.Properties[StatType.EnhancedMaximumDamage] = new ItemProperty { Type = StatType.EnhancedMaximumDamage, Value = enhancedDamage };
        return new HeldItem("account", character, item);
    }

    private static bool KeptAt260(Item item) => item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 260;

    [Fact]
    public void Reroll_OneSwordAndGemsForEveryRollThatFits()
    {
        var held = new List<HeldItem> { Sword("swords", QualityType.Normal) }
            .Concat(On("chips", ItemName.ChippedRuby, 8))
            .Concat(On("chips", ItemName.ChippedSkull, 8))
            .ToList();

        var plan = CubePlanner.Plan([SwordReroll], held, 11, KeptAt260);

        Assert.Equal(3, Assert.Single(plan.Transmutes).Count);
        var givers = Givers(plan);
        Assert.Equal(1, givers["swords"][ItemName.ColossusSword]);
        Assert.Equal(9, givers["chips"].Values.Sum());
    }

    [Fact]
    public void Reroll_ContinuesTheSwordRolledLast_AndLeavesKeepersAlone()
    {
        var held = new List<HeldItem>
        {
            Sword("keeper", QualityType.Magical, 280),
            Sword("white", QualityType.Normal),
            Sword("rolled", QualityType.Magical, 150)
        }.Concat(On("chips", ItemName.ChippedRuby, 3)).ToList();

        var plan = CubePlanner.Plan([SwordReroll], held, 30, KeptAt260);

        Assert.Equal(1, Assert.Single(plan.Transmutes).Count);
        Assert.Contains(plan.Givers, g => g.Character == "rolled");
        Assert.DoesNotContain(plan.Givers, g => g.Character is "keeper" or "white");
    }

    [Fact]
    public void Reroll_NoGems_EmptyPlan()
    {
        var plan = CubePlanner.Plan([SwordReroll], [Sword("white", QualityType.Normal)], 30, KeptAt260);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Givers);
    }

    [Fact]
    public void Reserve_StaysOnTheMules()
    {
        var gul = new CubeRecipe
        {
            Name = "Gul",
            Requirements =
            [
                new RecipeRequirement { ItemNames = [ItemName.IstRune], Amount = 2, Reserve = 5 },
                new RecipeRequirement { ItemNames = [ItemName.Sapphire], Amount = 1 }
            ],
            Result = new RecipeResult { ItemNames = [ItemName.GulRune] }
        };
        var held = On("runes", ItemName.IstRune, 9).Concat(On("gems", ItemName.Sapphire, 5)).ToList();

        var plan = CubePlanner.Plan([gul], held, 30);

        Assert.Equal(2, Assert.Single(plan.Transmutes).Count);
        Assert.Equal(4, Givers(plan)["runes"][ItemName.IstRune]);
    }
}
