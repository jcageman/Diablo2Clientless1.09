using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Sockets the party opens in its own gear with the cube, and the perfect gem it sets in them: a unique with a Standard of
/// Heroes (#238; Diablo drops one in classic too), a rare with a Stone of Jordan and three perfect skulls (#62, #124). The
/// user's wish on 22 Nov 2026: Twitchthroe with a perfect ruby.
/// </summary>
public sealed partial class LevelingGear
{
    /// <summary>
    /// Only perfect gems go in: a socketed gem never comes out again in 1.09, and the party cubes three flawless into one
    /// itself.
    /// </summary>
    private static readonly HashSet<ItemName> PerfectGems =
    [
        ItemName.PerfectAmethyst, ItemName.PerfectTopaz, ItemName.PerfectSapphire, ItemName.PerfectEmerald, ItemName.PerfectRuby,
        ItemName.PerfectDiamond, ItemName.PerfectSkull
    ];

    /// <summary>A socket is worth its opener only above this: a perfect sapphire's 38 mana is 4 to the barbarian.</summary>
    private const double MinimumSocketGain = 10;

    private const int SkullsPerJordanSocket = 3;

    /// <summary>Items a transmute was refused for, not tried again: a recipe the realm turned down stays turned down.</summary>
    private readonly HashSet<uint> _socketRefused = [];

    /// <param name="Slot">Where the item is worn; null for one the party holds, which the next upgrade round puts on.</param>
    private sealed record SocketPlan(PartyMember Wearer, DirectoryType? Slot, Item Item, Item Gem, double Gain, IReadOnlyList<Item> Openers);

    /// <summary>
    /// In town: the item and perfect gem the party holds that together leave a member best off, if the party holds what opens
    /// that item's socket - a worn item, or one held that the socket and gem make worth wearing. The wearer gathers the
    /// opener, the gem and the item, cubes the item, sets the gem and wears it.
    /// </summary>
    public async Task SocketGear(Party party, IReadOnlyList<PartyMember> present)
    {
        if (PlanSocket(party, present) is not { } plan)
        {
            return;
        }

        var wearer = plan.Wearer;
        Log.Information("{Character} opens a socket in {Where} {Quality} {Item} for a {Gem} ({Gain:F0}) with {Openers}", wearer.Name,
            plan.Slot == null ? "a held" : "its worn", plan.Item.Quality, plan.Item.Name, plan.Gem.Name, plan.Gain,
            string.Join(", ", plan.Openers.Select(opener => opener.Name)));
        var inputs = plan.Openers.Append(plan.Gem).Concat(plan.Slot == null ? [plan.Item] : []);
        foreach (var input in inputs)
        {
            if (!await Gather(present, wearer, input))
            {
                Log.Information("{Character} could NOT gather the {Input} to socket the {Item}", wearer.Name, input.Name, plan.Item.Name);
                return;
            }
        }

        if (await OpenSocket(plan) is not { } opened)
        {
            return;
        }

        await SetGem(wearer, opened, plan.Gem);
        if (plan.Slot is { } slot && !await Equip(wearer, wearer.Game.Inventory.FindItemById(opened.Id) ?? opened, slot))
        {
            Log.Warning("{Character} could NOT wear its socketed {Item} again", wearer.Name, opened.Name);
        }
    }

    private SocketPlan PlanSocket(Party party, IReadOnlyList<PartyMember> present)
    {
        var held = present.Where(member => member.InGame).SelectMany(Held).ToList();
        var standard = held.FirstOrDefault(item => item.Name == ItemName.StandardOfHeroes && item.Classification == ClassificationType.Token);
        var jordan = held.FirstOrDefault(item => IsStoneOfJordan(item) && !WantedByAnyone(party, item) && !WantedLater(party, item));
        var skulls = held.Where(item => item.Name == ItemName.PerfectSkull).ToList();
        var gems = held.Where(item => PerfectGems.Contains(item.Name) && !(LevelingGems.Collecting && LevelingGems.IsDiamond(item)))
            .DistinctBy(item => item.Name).ToList();
        if ((standard == null && jordan == null) || gems.Count == 0)
        {
            return null;
        }

        List<Item> Openers(Item item, Item gem) => item.Quality switch
        {
            QualityType.Unique when standard != null => [standard],
            QualityType.Rare when jordan != null && skulls.Count(skull => skull.Id != gem.Id) >= SkullsPerJordanSocket
                => [jordan, .. skulls.Where(skull => skull.Id != gem.Id).Take(SkullsPerJordanSocket)],
            _ => null
        };

        bool Closed(Item item) => item.Sockets == 0 && !_socketRefused.Contains(item.Id) && GearRules.SocketKindOf(item.Classification) != null
            && item.Quality is QualityType.Unique or QualityType.Rare;

        SocketPlan best = null;
        foreach (var wearer in present.Where(member => member.InGame && member.Game.Inventory.FindItemByName(ItemName.HoradricCube) != null))
        {
            var worn = wearer.Game.Me.Equipment.Where(pair => Closed(pair.Value)).Select(pair => ((DirectoryType?)pair.Key, pair.Value));
            var unworn = held.Where(Closed).Select(item => ((DirectoryType?)null, item));
            foreach (var (slot, item) in worn.Concat(unworn))
            {
                // A held item counts from where it stands now: below what is worn, the gem has to make up the difference.
                var standing = slot == null ? SlotGain(wearer, item, out _) : 0;
                foreach (var gem in gems)
                {
                    var gain = standing + GemGain(wearer, item, gem, worn: slot != null);
                    if (gain >= MinimumSocketGain && gain > (best?.Gain ?? 0) && Openers(item, gem) is { } openers)
                    {
                        best = new SocketPlan(wearer, slot, item, gem, gain, openers);
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// What the gem's properties add to the member's outfit: with everything it wears kept on for a worn item, and without
    /// what the item would take the place of for one it holds.
    /// </summary>
    private double GemGain(PartyMember member, Item item, Item gem, bool worn)
        => GearRules.SocketKindOf(item.Classification) is { } kind && GearRules.GemProperties(gem.Name, kind) is { Count: > 0 } properties
            ? StatScore(member, Synthetic(properties, worn ? gem : item, member.Level)) - 1
            : 0;

    private bool IsStoneOfJordan(Item item) => requirements.Unique(item)?.Name is "The Stone of Jordan" or "Pre The Stone of Jordan";

    private static IEnumerable<Item> Held(PartyMember member) => member.Game.Inventory.Items.Concat(member.Game.Stash.Items).Concat(member.Game.Cube.Items);

    /// <summary>Brings an item the party holds into the wearer's bag: out of its holder's stash or cube, then handed over.</summary>
    private static async Task<bool> Gather(IReadOnlyList<PartyMember> present, PartyMember wearer, Item input)
    {
        var holder = present.FirstOrDefault(member => member.InGame && Held(member).Any(item => item.Id == input.Id));
        if (holder == null)
        {
            return false;
        }

        var game = holder.Game;
        if (game.Stash.FindItemById(input.Id) is { } stashed)
        {
            if (!await WalkToStash(holder) || InventoryHelpers.MoveStashItemsToInventory(game, [stashed]) != MoveItemResult.Succes)
            {
                return false;
            }
        }
        else if (game.Cube.FindItemById(input.Id) is { } cubed && InventoryHelpers.PutCubeItemInInventory(game, cubed) != MoveItemResult.Succes)
        {
            return false;
        }

        if (holder != wearer && game.Inventory.FindItemById(input.Id) is { } carried && !await HandQuestItem(holder, wearer, carried))
        {
            return false;
        }

        return wearer.Game.Inventory.FindItemById(input.Id) != null;
    }

    /// <summary>The item cubed with its openers, in the wearer's bag; null when anything fails, the item worn again then.</summary>
    private async Task<Item> OpenSocket(SocketPlan plan)
    {
        var wearer = plan.Wearer;
        var game = wearer.Game;
        // A transmute with anything else in the cube does nothing.
        foreach (var other in game.Cube.Items.ToList())
        {
            if (InventoryHelpers.PutCubeItemInInventory(game, other) != MoveItemResult.Succes)
            {
                Log.Information("{Character} could NOT empty its cube to socket the {Item}", wearer.Name, plan.Item.Name);
                return null;
            }
        }

        if (plan.Slot is { } slot && !await Unequip(wearer, slot))
        {
            Log.Information("{Character} could NOT take off its {Item} to socket it", wearer.Name, plan.Item.Name);
            return null;
        }

        foreach (var input in plan.Openers.Prepend(plan.Item))
        {
            var carried = game.Inventory.FindItemById(input.Id);
            if (carried == null || game.Cube.FindFreeSpace(carried) is not { } space || InventoryHelpers.PutInventoryItemInCube(game, carried, space) != MoveItemResult.Succes)
            {
                Log.Information("{Character} could NOT put the {Input} in its cube to socket the {Item}", wearer.Name, input.Name, plan.Item.Name);
                await Restore(plan);
                return null;
            }
        }

        // The openers are used up; the item comes back, with its own id or a new one.
        var transmuted = InventoryHelpers.TransmuteItemsInCube(game, _ => game.Cube.Items.Count > 0
            && plan.Openers.All(opener => game.Cube.FindItemById(opener.Id) == null));
        var opened = game.Cube.FindItemById(plan.Item.Id)
            ?? game.Cube.Items.FirstOrDefault(item => item.Name == plan.Item.Name && item.Quality == plan.Item.Quality);
        Log.Information("{Character} {Outcome} a socket in the {Item}: the cube holds {Cube}", wearer.Name,
            transmuted && opened?.Sockets > 0 ? "opened" : "could NOT open", plan.Item.Name,
            string.Join(", ", game.Cube.Items.Select(item => $"{item.Quality} {item.Name} ({item.Sockets} sockets)")));
        if (!transmuted || opened == null || opened.Sockets == 0)
        {
            _socketRefused.Add(plan.Item.Id);
            await Restore(plan);
            return null;
        }

        if (InventoryHelpers.PutCubeItemInInventory(game, opened) != MoveItemResult.Succes)
        {
            await Restore(plan);
            return null;
        }

        return opened;
    }

    /// <summary>Everything in the cube back in the bag, and a worn item worn again.</summary>
    private static async Task Restore(SocketPlan plan)
    {
        var game = plan.Wearer.Game;
        foreach (var cubed in game.Cube.Items.ToList())
        {
            InventoryHelpers.PutCubeItemInInventory(game, cubed);
        }

        var carried = game.Inventory.FindItemById(plan.Item.Id)
            ?? game.Inventory.Items.FirstOrDefault(own => own.Name == plan.Item.Name && own.Quality == plan.Item.Quality);
        if (plan.Slot is { } slot && carried != null && !game.Me.Equipment.ContainsKey(slot))
        {
            await Equip(plan.Wearer, carried, slot);
        }
    }

    private static async Task SetGem(PartyMember member, Item item, Item gem)
    {
        var game = member.Game;
        if (game.Inventory.FindItemById(gem.Id) is not { } carried)
        {
            return;
        }

        game.RemoveItemFromContainer(carried);
        if (!GeneralHelpers.TryWithTimeout(_ => game.CursorItem?.Id == gem.Id, TimeSpan.FromSeconds(2)))
        {
            Log.Warning("{Character} could not lift its {Gem} to socket it", member.Name, gem.Name);
            return;
        }

        game.SocketCursorItem(carried, game.Items.GetValueOrDefault(item.Id) ?? item);
        var socketed = GeneralHelpers.TryWithTimeout(_ => (game.Items.GetValueOrDefault(item.Id) ?? item).SocketedItems.ContainsKey(gem.Id), TimeSpan.FromSeconds(3));
        Log.Information("{Character} {Outcome} a {Gem} in the {Item}", member.Name, socketed ? "put" : "could NOT put", gem.Name, item.Name);
        if (!socketed)
        {
            game.CleanupCursorItem();
        }

        await Task.Delay(200);
    }
}
