using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// A resist shield the party makes itself: every diamond it finds is kept, cubed up three to one until perfect, and
/// three perfect diamonds go into a three-socket Large Shield from a merchant - all resistances 19 each, which in
/// Nightmare and Hell is worth more than the block a better shield gives. Nothing comes from the mules: the party
/// levels on its own.
/// </summary>
public static class LevelingGems
{
    private static readonly ItemName[] Grades = [ItemName.ChippedDiamond, ItemName.FlawedDiamond, ItemName.Diamond, ItemName.FlawlessDiamond, ItemName.PerfectDiamond];

    private const int PerShield = 3;
    private const int GemsPerUpgrade = 3;

    /// <summary>True while a shield bearer still lacks its diamond shield; diamonds are kept, not sold, while it is.</summary>
    public static bool Collecting { get; private set; } = true;

    /// <summary>
    /// True while the party holds fewer diamonds than its missing shields need, counted in perfect ones (three flawless
    /// make one): past that they are left on the ground, and once the shields are made the rest are sold.
    /// </summary>
    public static bool PickingUp { get; private set; } = true;

    private static double PerfectWorth(Item item) => Math.Pow(GemsPerUpgrade, Array.IndexOf(Grades, item.Name) - (Grades.Length - 1));

    public static bool IsDiamond(Item item) => Array.IndexOf(Grades, item.Name) >= 0;

    private static bool HasDiamondShield(PartyMember member) => HasDiamondShield(member.Game);

    /// <summary>A shield with the diamonds its sockets were bought for, worn or carried.</summary>
    private static bool HasDiamondShield(D2NG.Core.Game game)
        => game.Inventory.Items.Concat(game.Me.Equipment.Values).Select(item => Fresh(game, item))
            .Any(item => IsShield(item) && item.SocketedItems.Values.Count(IsDiamond) >= PerShield);

    /// <summary>
    /// The item as the game holds it now: a stats update replaces the item, and the gem socketed after it went to the new one
    /// - the shield fetched before the socketing never showed the diamond the server had put in.
    /// </summary>
    private static Item Fresh(D2NG.Core.Game game, Item item) => game.Items.GetValueOrDefault(item.Id) ?? item;

    private static int Used(Item item) => (int)Math.Max(item.UsedSockets, (uint)item.SocketedItems.Count);

    /// <summary>A shield of three sockets or more, its filled ones holding diamonds and not all of them filled yet.</summary>
    private static bool OpenForDiamonds(Item item)
        => IsShield(item) && item.Sockets >= PerShield && Used(item) < PerShield && item.SocketedItems.Values.All(IsDiamond);

    /// <summary>
    /// Gems a member holds beyond this many - bag, cube and stash - go to the gem mules before its next game. The party never
    /// took from the mules and never gave to them either: the paladin, who every flawless gem was handed to, sat with 14
    /// perfect diamonds, 44 other perfect gems and some 80 flawless ones, his bag full, so nothing more could be handed to
    /// him or cubed or picked up.
    /// </summary>
    public const int MuleGemsFrom = 20;

    public static bool IsGem(Item item) => item.Classification == ClassificationType.Gem;

    public static int GemsHeld(D2NG.Core.Game game) => Held(game).Count(IsGem);

    private static IEnumerable<Item> Held(D2NG.Core.Game game) => game.Inventory.Items.Concat(game.Cube.Items).Concat(game.Stash.Items);

    /// <summary>The perfect diamonds the party's missing shields still need: three for each shield bearer without one.</summary>
    public static int PerfectDiamondsOwed(IEnumerable<PartyMember> members)
        => members.Count(member => member.InGame && member.Plan.Shield && !HasDiamondShield(member)) * PerShield;

    /// <summary>
    /// What a gem mule pass takes: every gem but the perfect diamonds the shields still need - <paramref name="perfectKept"/>
    /// of them, lowest ids first - and, while fewer are held, the lesser diamonds that cube up into them. Decided afresh at
    /// every look: the pass reads its list again each round, and a refused trade renumbers every item.
    /// </summary>
    public static Func<Item, bool> GemsToMule(D2NG.Core.Game game, int perfectKept) => GemsToMule(() => Held(game), perfectKept);

    public static Func<Item, bool> GemsToMule(Func<IEnumerable<Item>> held, int perfectKept)
        => item =>
        {
            if (!IsGem(item))
            {
                return false;
            }

            if (perfectKept == 0 || !IsDiamond(item))
            {
                return true;
            }

            var kept = held().Where(gem => gem.Name == ItemName.PerfectDiamond).OrderBy(gem => gem.Id).Take(perfectKept).ToList();
            return item.Name == ItemName.PerfectDiamond ? kept.All(perfect => perfect.Id != item.Id) : kept.Count >= perfectKept;
        };

    private static bool IsShield(Item item) => item.Classification is ClassificationType.Shield or ClassificationType.PaladinShield;

    /// <summary>The shield bearer the diamonds go to: a paladin first, and one with a cube to upgrade them in.</summary>
    public static PartyMember Collector(IEnumerable<PartyMember> members)
        => members.Where(member => member.InGame && member.Plan.Shield && !HasDiamondShield(member)
                && member.Game.Inventory.FindItemByName(ItemName.HoradricCube) != null)
            .OrderByDescending(member => member.Plan.Class == CharacterClass.Paladin)
            .FirstOrDefault();

    /// <summary>
    /// A shield bought or found for the diamonds is kept for them until they are in: plain, it scores below the magic Bone
    /// Shield, and the paladin sold the three-socket Large Shield from Fara in the next town visit for 563 gold, before
    /// the socketing that comes after the sale.
    /// </summary>
    public static bool ReservedForDiamonds(Item item)
        => Collecting && OpenForDiamonds(item) && item.Quality <= QualityType.Superior;

    /// <summary>The stash counts: a mule pass ends by stashing what the nips keep, perfect diamonds among them.</summary>
    private static int Perfect(PartyMember member)
        => member.Game.Inventory.Items.Concat(member.Game.Stash.Items).Count(item => item.Name == ItemName.PerfectDiamond);

    private static Item Socketable(PartyMember member)
        => member.Game.Inventory.Items.Select(item => Fresh(member.Game, item)).FirstOrDefault(OpenForDiamonds);

    /// <summary>Whether this member should look for the shield at the merchants: it holds the diamonds and no shield to put them in.</summary>
    public static bool WantsShield(PartyMember member, PartyMember collector)
        => member == collector && Perfect(member) >= PerShield && Socketable(member) == null;

    /// <summary>In town: diamonds handed to the collector, cubed up, and socketed once the shield is there.</summary>
    public static async Task Work(List<PartyMember> present, IReadOnlyList<PartyMember> everyone)
    {
        // Whether to keep them is the whole party's question, not of those in town: a member restocking alone found no
        // collector beside it, and on the next visit the paladin sold a perfect, two flawless and four lesser diamonds.
        Collecting = Collector(everyone) != null;
        var needed = everyone.Count(member => member.InGame && member.Plan.Shield && !HasDiamondShield(member)) * PerShield;
        var held = everyone.Where(member => member.InGame).Sum(member => member.Game.Inventory.Items.Concat(member.Game.Cube.Items).Where(IsDiamond).Sum(PerfectWorth));
        PickingUp = Collecting && held < needed;
        var collector = Collector(present);
        if (collector == null)
        {
            return;
        }

        foreach (var member in present.Where(member => member != collector && member.InGame))
        {
            foreach (var gem in member.Game.Inventory.Items.Where(IsDiamond).ToList())
            {
                await LevelingGear.HandQuestItem(member, collector, gem);
            }
        }

        await Upgrade(collector);
        await SocketShield(collector);
    }

    /// <summary>Each flawless gem and the perfect one three of them cube into.</summary>
    private static readonly Dictionary<ItemName, ItemName> FlawlessToPerfect = new()
    {
        [ItemName.FlawlessAmethyst] = ItemName.PerfectAmethyst, [ItemName.FlawlessTopaz] = ItemName.PerfectTopaz,
        [ItemName.FlawlessSapphire] = ItemName.PerfectSapphire, [ItemName.FlawlessEmerald] = ItemName.PerfectEmerald,
        [ItemName.FlawlessRuby] = ItemName.PerfectRuby, [ItemName.FlawlessDiamond] = ItemName.PerfectDiamond,
        [ItemName.FlawlessSkull] = ItemName.PerfectSkull,
    };

    /// <summary>
    /// Flawless gems of every kind go to one member with a cube, who cubes each three of a kind into a perfect one; the
    /// perfect gems are stashed with the rest of what the nips keep. The party went through Hell without a single perfect
    /// gem in a stash. Diamonds stay with the shield's collector while it collects.
    /// </summary>
    public static async Task CubeFlawlessGems(List<PartyMember> present)
    {
        bool Cubed(Item item) => FlawlessToPerfect.ContainsKey(item.Name) && !(Collecting && IsDiamond(item));
        var keeper = present.Where(member => member.InGame && member.Game.Inventory.FindItemByName(ItemName.HoradricCube) != null)
            .OrderByDescending(member => member.Game.Inventory.Items.Concat(member.Game.Stash.Items).Count(Cubed))
            .FirstOrDefault();
        if (keeper == null)
        {
            return;
        }

        foreach (var member in present.Where(member => member != keeper && member.InGame))
        {
            foreach (var gem in member.Game.Inventory.Items.Where(Cubed).ToList())
            {
                await LevelingGear.HandQuestItem(member, keeper, gem);
            }
        }

        var game = keeper.Game;
        foreach (var kind in game.Inventory.Items.Concat(game.Stash.Items).Where(Cubed).GroupBy(gem => gem.Name).Where(group => group.Count() >= GemsPerUpgrade).ToList())
        {
            var stashed = kind.Where(gem => game.Stash.FindItemById(gem.Id) != null).ToList();
            if (stashed.Count > 0)
            {
                var reached = await LevelingGear.WalkToStash(keeper);
                var moved = reached ? InventoryHelpers.MoveStashItemsToInventory(game, stashed) : MoveItemResult.Failed;
                if (moved != MoveItemResult.Succes)
                {
                    Log.Information("{Character} could NOT take {Count} {Gem} out of its stash to cube them: {Reason}", keeper.Name, stashed.Count, kind.Key,
                        reached ? moved.ToString() : "no stash reached");
                    continue;
                }
            }

            while (game.Inventory.Items.Count(gem => gem.Name == kind.Key) >= GemsPerUpgrade && await CubeThree(keeper, kind.Key, FlawlessToPerfect[kind.Key]))
            {
            }
        }
    }

    /// <summary>Three gems of one name into the cube, transmuted, and the gem made put back in the inventory.</summary>
    private static async Task<bool> CubeThree(PartyMember member, ItemName from, ItemName to)
    {
        var game = member.Game;
        // A transmute with anything else in the cube does nothing.
        foreach (var other in game.Cube.Items.ToList())
        {
            if (InventoryHelpers.PutCubeItemInInventory(game, other) is var emptied and not MoveItemResult.Succes)
            {
                Log.Information("{Character} could NOT cube three {From}: taking {Other} out of the cube gave {Result}", member.Name, from, other.Name, emptied);
                return false;
            }
        }

        foreach (var gem in game.Inventory.Items.Where(item => item.Name == from).Take(GemsPerUpgrade).ToList())
        {
            var space = game.Cube.FindFreeSpace(gem);
            var put = space == null ? MoveItemResult.Failed : InventoryHelpers.PutInventoryItemInCube(game, gem, space);
            if (put != MoveItemResult.Succes)
            {
                Log.Information("{Character} could NOT cube three {From}: putting one in the cube gave {Result}", member.Name, from, space == null ? "no room" : put.ToString());
                return false;
            }
        }

        var made = InventoryHelpers.TransmuteItemsInCube(game, true) && game.Cube.FindItemByName(to) is { } upgraded
            && InventoryHelpers.PutCubeItemInInventory(game, upgraded) == MoveItemResult.Succes;
        Log.Information("{Character} {Outcome} three {From} into a {To}", member.Name, made ? "cubed" : "could NOT cube", from, to);
        await Task.Delay(200);
        return made;
    }

    private static async Task Upgrade(PartyMember member)
    {
        var game = member.Game;
        for (var grade = 0; grade < Grades.Length - 1; grade++)
        {
            while (game.Inventory.Items.Count(item => item.Name == Grades[grade]) >= GemsPerUpgrade)
            {
                // A transmute with anything else in the cube does nothing.
                foreach (var other in game.Cube.Items.ToList())
                {
                    if (InventoryHelpers.PutCubeItemInInventory(game, other) != MoveItemResult.Succes)
                    {
                        return;
                    }
                }

                foreach (var gem in game.Inventory.Items.Where(item => item.Name == Grades[grade]).Take(GemsPerUpgrade).ToList())
                {
                    if (game.Cube.FindFreeSpace(gem) is not { } space || InventoryHelpers.PutInventoryItemInCube(game, gem, space) != MoveItemResult.Succes)
                    {
                        return;
                    }
                }

                var made = InventoryHelpers.TransmuteItemsInCube(game, true) && game.Cube.FindItemByName(Grades[grade + 1]) is { } upgraded
                    && InventoryHelpers.PutCubeItemInInventory(game, upgraded) == MoveItemResult.Succes;
                Log.Information("{Character} {Outcome} three {From} into a {To}", member.Name, made ? "cubed" : "could NOT cube", Grades[grade], Grades[grade + 1]);
                if (!made)
                {
                    return;
                }

                await Task.Delay(200);
            }
        }
    }

    private static async Task SocketShield(PartyMember member)
    {
        var game = member.Game;
        if (Socketable(member) is not { } shield)
        {
            return;
        }

        var open = PerShield - Used(shield);
        if (Perfect(member) < open)
        {
            return;
        }

        var carried = game.Inventory.Items.Count(item => item.Name == ItemName.PerfectDiamond);
        if (carried < open)
        {
            var stashed = game.Stash.Items.Where(item => item.Name == ItemName.PerfectDiamond).Take(open - carried).ToList();
            var moved = await LevelingGear.WalkToStash(member) ? InventoryHelpers.MoveStashItemsToInventory(game, stashed) : MoveItemResult.Failed;
            if (moved != MoveItemResult.Succes)
            {
                Log.Information("{Character} could NOT take its perfect diamonds out of the stash for the shield: {Reason}", member.Name, moved);
                return;
            }
        }

        while (Used(Fresh(game, shield)) < PerShield)
        {
            var gem = game.Inventory.Items.FirstOrDefault(item => item.Name == ItemName.PerfectDiamond);
            if (gem == null)
            {
                return;
            }

            game.RemoveItemFromContainer(gem);
            if (!GeneralHelpers.TryWithTimeout(_ => game.CursorItem?.Id == gem.Id, TimeSpan.FromSeconds(2)))
            {
                Log.Warning("{Character} could not lift a perfect diamond to socket it", member.Name);
                return;
            }

            game.SocketCursorItem(gem, Fresh(game, shield));
            var socketed = GeneralHelpers.TryWithTimeout(_ => Fresh(game, shield).SocketedItems.ContainsKey(gem.Id), TimeSpan.FromSeconds(3));
            Log.Information("{Character} {Outcome} a perfect diamond in the {Shield} ({Used} of {Sockets})", member.Name,
                socketed ? "put" : "could NOT put", shield.Name, Used(Fresh(game, shield)), shield.Sockets);
            if (!socketed)
            {
                game.CleanupCursorItem();
                return;
            }

            await Task.Delay(200);
        }
    }

    /// <summary>
    /// A plain or superior shield with three sockets or more the member can carry, a Large Shield first, bought from
    /// <paramref name="npc"/> when it has one.
    /// </summary>
    public static bool BuyShield(PartyMember member, WorldObject npc, Func<PartyMember, Item, bool> canWear)
    {
        var game = member.Game;
        var offer = game.Items.Values.Where(item => item.IsInMerchantTab() && IsShield(item)
                && item.Sockets >= PerShield && item.Quality <= QualityType.Superior && canWear(member, item))
            .OrderByDescending(item => item.Name == ItemName.LargeShield).FirstOrDefault();
        if (offer == null)
        {
            return false;
        }

        if (game.Inventory.FindFreeSpace(offer) == null)
        {
            Log.Warning("{Character} has no room for the {Sockets}-socket {Shield} {Npc} offers", member.Name, offer.Sockets, offer.Name, npc.NPCCode);
            return false;
        }

        var before = LevelingGear.Gold(game);
        game.BuyItem(npc, offer, buyStack: false);
        var bought = GeneralHelpers.TryWithTimeout(_ => LevelingGear.Gold(game) < before, TimeSpan.FromSeconds(2));
        Log.Information("{Character} {Outcome} a {Sockets}-socket {Shield} from {Npc} for its perfect diamonds", member.Name,
            bought ? "bought" : "could NOT buy", offer.Sockets, offer.Name, npc.NPCCode);
        return bought;
    }
}
