using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.ItemData;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Puts found gear on. A leveling character starts with nearly every slot empty, so the first rule is the
/// simplest: an empty slot takes the best item for it the character can wear. Choosing between two items
/// for a filled slot is the item score's work.
/// </summary>
public sealed partial class LevelingGear(ItemRequirements requirements)
{
    private static readonly Dictionary<ClassificationType, DirectoryType[]> Slots = new()
    {
        [ClassificationType.Helm] = [DirectoryType.Helm],
        [ClassificationType.Circlet] = [DirectoryType.Helm],
        [ClassificationType.Armor] = [DirectoryType.Armor],
        [ClassificationType.Belt] = [DirectoryType.Belt],
        [ClassificationType.Boots] = [DirectoryType.Boots],
        [ClassificationType.Gloves] = [DirectoryType.Gloves],
        [ClassificationType.Amulet] = [DirectoryType.Amulet],
        [ClassificationType.Ring] = [DirectoryType.RightHandRing, DirectoryType.LeftHandRing],
    };

    /// <summary>Quest items that happen to fit a slot: the leader wore the Amulet of the Viper as an upgrade.</summary>
    private static readonly HashSet<ItemName> QuestItemsNeverWorn =
        [ItemName.TopoftheHoradricStaff, ItemName.ShaftoftheHoradricStaff, ItemName.HoradricStaff, ItemName.KhalimsFlail, ItemName.KhalimsWill,
         ItemName.KhalimsEye, ItemName.KhalimsBrain, ItemName.KhalimsHeart];

    /// <summary>
    /// A quest item, whatever the item table calls it: Khalim's Will is a mace there, and in Hell it went out as loot from
    /// a full bag after the transmute - six attempts at the Compelling Orb followed with a Gladius in hand.
    /// </summary>
    public static bool IsQuestItem(Item item) => QuestItemsNeverWorn.Contains(item.Name) || item.Classification == ClassificationType.QuestItem;

    /// <param name="slot">The slot it would go in, for a ring: it takes one ring's place, not both.</param>
    /// <param name="later">Judged with the stats the member's plan gives it later and some from gear (<see cref="LaterGearStats"/>).</param>
    public bool CanWear(PartyMember member, Item item, DirectoryType? slot = null, bool later = false)
    {
        if (QuestItemsNeverWorn.Contains(item.Name) || item.Classification == ClassificationType.QuestItem || ReservedForAnotherClass(member, item))
        {
            return false;
        }

        // The attributes the server sends are the hard points; what the rest of the gear adds counts too. Counting only the
        // hard points kept the amazon, with 35 of her own and 72 in all, out of every bow above 35 strength - Vidala's Barb
        // rode in her bag unworn.
        var reduced = 1 + Stat(item, StatType.ReducedRequirements) / 100.0;
        if ((int)(requirements.RequiredStrength(item) * reduced) > Effective(member, item, Attribute.Strength, StatType.Strength, slot, later)
            || (int)(requirements.RequiredDexterity(item) * reduced) > Effective(member, item, Attribute.Dexterity, StatType.Dexterity, slot, later))
        {
            return false;
        }

        if (!later && !LeavesTheRestWearable(member, item, slot))
        {
            return false;
        }

        // Unknown for magic and rare items until the realm's affix numbering is known; the server says no if it is too high.
        return later || (requirements.RequiredLevel(item) ?? 0) <= member.Level;
    }

    /// <summary>
    /// Sets that belong to one class whatever the score says (the user's rule, 22 Nov 2026). Death's Disguise is the amazon's:
    /// its 30% attack speed is her damage, while the whirling barbarian scored its 8% life leech higher and would have
    /// collected it for a speed Whirlwind does not use on classic.
    /// </summary>
    private static readonly Dictionary<string, CharacterClass> ClassSets = new(StringComparer.Ordinal)
    {
        ["Death's Disguise"] = CharacterClass.Amazon,
    };

    private bool ReservedForAnotherClass(PartyMember member, Item item)
        => requirements.SetItem(item) is { } setItem && ClassSets.TryGetValue(setItem.SetName, out var owner) && owner != member.Plan.Class;

    /// <summary>
    /// Whether everything else the member wears still meets its strength and dexterity with <paramref name="item"/> on in
    /// place of what it replaces. Hard points are spent only up to what the gear asks for, net of what the rest of the gear
    /// adds, so a ring with less strength in place of one with more could leave the weapon unusable. A worn item that does
    /// not meet them now is not held against it.
    /// </summary>
    private bool LeavesTheRestWearable(PartyMember member, Item item, DirectoryType? slot)
    {
        var worn = member.Game.Me.Equipment.Values.ToList();
        var staying = worn.Where(other => other.Id != item.Id && !Replaces(member, item, other, slot)).ToList();
        foreach (var piece in staying)
        {
            var reduced = 1 + Stat(piece, StatType.ReducedRequirements) / 100.0;
            foreach (var (attribute, stat, required) in new[]
                     {
                         (Attribute.Strength, StatType.Strength, requirements.RequiredStrength(piece)),
                         (Attribute.Dexterity, StatType.Dexterity, requirements.RequiredDexterity(piece))
                     })
            {
                var need = (int)(required * reduced);
                var hard = member.Game.Me.Attributes.GetValueOrDefault(attribute);
                var now = hard + worn.Where(other => other.Id != piece.Id).Sum(other => Stat(other, stat));
                var after = hard + Stat(item, stat) + staying.Where(other => other.Id != piece.Id).Sum(other => Stat(other, stat));
                if (need <= now && need > after)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// What gear can be counted on to add to a stat later: belts roll 20 strength, so 20 to 30 more than the plan's own is
    /// within reach. A sorceress with her plan's 40 can carry a 60 or 70 strength item that way, not an 80 one.
    /// </summary>
    private const int LaterGearStats = 30;

    /// <summary>
    /// The member's hard points in a stat plus what its worn gear adds, leaving out what <paramref name="item"/> would replace;
    /// <paramref name="later"/>, the plan's points for it and what gear can add.
    /// </summary>
    private int Effective(PartyMember member, Item item, Attribute attribute, StatType stat, DirectoryType? slot = null, bool later = false)
    {
        var hard = member.Game.Me.Attributes.GetValueOrDefault(attribute);
        var gear = GearStat(member, item, stat, slot);
        if (!later)
        {
            return hard + gear;
        }

        var planned = member.Plan.Stats.FirstOrDefault(target => target.Stat == attribute)?.Base ?? 0;
        return Math.Max(hard, planned) + Math.Max(gear, LaterGearStats);
    }

    /// <summary>What the member's worn gear adds to a stat, leaving out what <paramref name="item"/> would replace.</summary>
    private int GearStat(PartyMember member, Item item, StatType stat, DirectoryType? slot)
        => member.Game.Me.Equipment.Values.Where(worn => worn.Id != item.Id && !Replaces(member, item, worn, slot)).Sum(worn => Stat(worn, stat));

    /// <summary>
    /// The hard strength or dexterity an upgrade the party holds for <paramref name="member"/> asks for, when that is all that
    /// keeps it off and the plan's cap for the stat reaches it. Without this the points went by the plan's floors alone, and
    /// the better items rode in the bags and stash unworn.
    /// </summary>
    public List<(Attribute Stat, int Need, Item Item)> StatsWanted(Party party, PartyMember member)
    {
        var wanted = new List<(Attribute Stat, int Need, Item Item)>();
        if (!member.InGame)
        {
            return wanted;
        }

        var held = party.Members.Where(holder => holder.InGame)
            .SelectMany(holder => holder.Game.Inventory.Items.Concat(holder.Game.Stash.Items).Concat(holder.Game.Cube.Items))
            .Where(item => (item.IsIdentified || item.Quality < QualityType.Magical) && !IsQuestItem(item));
        foreach (var item in held)
        {
            if ((requirements.RequiredLevel(item) ?? 0) > member.Level || CanWear(member, item) || SlotGain(member, item, out var slot, later: true) <= 0)
            {
                continue;
            }

            var reduced = 1 + Stat(item, StatType.ReducedRequirements) / 100.0;
            foreach (var (attribute, stat, required) in new[]
                     {
                         (Attribute.Strength, StatType.Strength, requirements.RequiredStrength(item)),
                         (Attribute.Dexterity, StatType.Dexterity, requirements.RequiredDexterity(item))
                     })
            {
                var need = (int)(required * reduced) - GearStat(member, item, stat, slot);
                if (need > member.Game.Me.Attributes.GetValueOrDefault(attribute)
                    && member.Plan.Stats.FirstOrDefault(target => target.Stat == attribute)?.Cap is { } cap && need <= cap)
                {
                    wanted.Add((attribute, need, item));
                }
            }
        }

        return wanted;
    }

    /// <summary>
    /// Whether wearing <paramref name="item"/> in <paramref name="slot"/> takes <paramref name="worn"/> off: the same slot, the
    /// weapon for a weapon, and the shield too for a two-handed one. A ring takes the place of the ring in its slot only;
    /// with both left out, each of the barbarian's 7% life leech rings counted as his only leech, and a rare ring never beat them.
    /// </summary>
    private bool Replaces(PartyMember member, Item item, Item worn, DirectoryType? slot = null)
    {
        if (WeaponKinds.Contains(item.Classification))
        {
            return WeaponKinds.Contains(worn.Classification) || (IsTwoHanded(item) && IsWeaponOrShield(worn) && worn.Classification != ClassificationType.Arrows);
        }

        if (IsWeaponOrShield(item))
        {
            return IsWeaponOrShield(worn) && !WeaponKinds.Contains(worn.Classification);
        }

        if (!Slots.TryGetValue(item.Classification, out var slots))
        {
            return false;
        }

        if (slots.Length > 1)
        {
            return slot is { } into && member.Game.Me.Equipment.GetValueOrDefault(into)?.Id == worn.Id;
        }

        return Slots.TryGetValue(worn.Classification, out var wornSlots) && slots.Intersect(wornSlots).Any();
    }

    /// <summary>
    /// The server refuses to put on an unidentified magic item: identified first with a scroll, one
    /// activation per item. Only items that could fill an empty slot - scrolls cost gold the party lacks.
    /// </summary>
    private static async Task IdentifyCandidates(PartyMember member)
    {
        var game = member.Game;
        var wanted = game.Inventory.Items
            .Where(item => !item.IsIdentified && item.Quality >= QualityType.Magical
                && (IsWeaponOrShield(item) || (Slots.TryGetValue(item.Classification, out var slots) && slots.Any(slot => !game.Me.Equipment.ContainsKey(slot)))))
            .ToList();
        foreach (var item in wanted)
        {
            var scroll = game.Inventory.Items.FirstOrDefault(candidate => candidate.Name == ItemName.ScrollofIdentify);
            if (scroll == null)
            {
                return;
            }

            game.ActivateInventoryItem(scroll);
            await Task.Delay(300);
            game.IdentifyItem(scroll, item);
            var identified = GeneralHelpers.TryWithTimeout(_ => game.Inventory.FindItemById(item.Id)?.IsIdentified == true, TimeSpan.FromSeconds(2));
            Log.Information("{Character} {Outcome} {Item} with a scroll", member.Name, identified ? "identified" : "could NOT identify", item.Name);
        }
    }

    public static async Task IdentifyWithScroll(PartyMember member, Item item)
    {
        var game = member.Game;
        var scroll = game.Inventory.Items.FirstOrDefault(candidate => candidate.Name == ItemName.ScrollofIdentify);
        if (scroll == null || item.IsIdentified)
        {
            return;
        }

        game.ActivateInventoryItem(scroll);
        await Task.Delay(300);
        game.IdentifyItem(scroll, item);
        GeneralHelpers.TryWithTimeout(_ => game.Inventory.FindItemById(item.Id)?.IsIdentified == true, TimeSpan.FromSeconds(2));
    }

    public async Task FillEmptySlots(PartyMember member)
    {
        var game = member.Game;
        await IdentifyCandidates(member);
        foreach (var (classification, slots) in Slots)
        {
            foreach (var slot in slots.Where(slot => !game.Me.Equipment.ContainsKey(slot)))
            {
                var candidate = game.Inventory.Items
                    .Where(item => item.Classification == classification && (item.IsIdentified || item.Quality < QualityType.Magical) && CanWear(member, item))
                    .OrderByDescending(item => item.Quality)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    await Equip(member, candidate, slot);
                }
            }
        }

        if (WantsBow(member))
        {
            var bow = game.Inventory.Items
                .Where(item => item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow && CanWear(member, item))
                .OrderByDescending(item => item.Quality)
                .FirstOrDefault();
            // Not without arrows: a bow with an empty quiver does no damage at all, a javelin at least does some.
            if (bow != null && HasArrows(member) && await Unequip(member, DirectoryType.RightHand) && await Unequip(member, DirectoryType.LeftHand))
            {
                await Equip(member, bow, DirectoryType.RightHand);
            }
        }

        if (HoldsBow(member) && !game.Me.Equipment.Values.Any(item => item.Classification == ClassificationType.Arrows)
            && game.Inventory.Items.FirstOrDefault(item => item.Classification == ClassificationType.Arrows) is { } arrows)
        {
            await Equip(member, arrows, DirectoryType.LeftHand);
        }
    }

    public static bool WantsBow(PartyMember member) => IsArcher(member) && !HoldsBow(member);

    public static bool HasArrows(PartyMember member)
        => member.Game.Me.Equipment.Values.Any(item => item.Classification == ClassificationType.Arrows)
            || member.Game.Inventory.Items.Any(item => item.Classification == ClassificationType.Arrows);

    public static bool HoldsBow(PartyMember member)
        => member.Game.Me.Equipment.Values.Any(item => item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow);

    /// <summary>
    /// Items in town go to the member who can use them: a bow from anyone's inventory to an amazon that has
    /// none. Dropped at the giver's feet next to the receiver, picked up, and the giver checks the ground.
    /// </summary>
    public async Task ShareInTown(Party party)
    {
        foreach (var archer in party.Members.Where(member => member.InGame && member.Game.IsInTown() && HoldsBow(member) && !HasArrows(member)))
        {
            foreach (var giver in party.Members.Where(member => member != archer && member.InGame && member.Game.IsInTown()))
            {
                if (giver.Game.Inventory.Items.FirstOrDefault(item => item.Classification == ClassificationType.Arrows) is { } arrows
                    && await Hand(giver, archer, arrows))
                {
                    await FillEmptySlots(archer);
                    break;
                }
            }
        }

        foreach (var receiver in party.Members.Where(member => member.InGame && member.Game.IsInTown() && WantsBow(member)))
        {
            foreach (var giver in party.Members.Where(member => member != receiver && member.InGame && member.Game.IsInTown()))
            {
                var bow = giver.Game.Inventory.Items
                    .Where(item => item.Classification is ClassificationType.Bow or ClassificationType.AmazonBow && CanWear(receiver, item))
                    .OrderByDescending(item => item.Quality)
                    .FirstOrDefault();
                if (bow == null)
                {
                    continue;
                }

                if (await Hand(giver, receiver, bow))
                {
                    await FillEmptySlots(receiver);
                    break;
                }
            }
        }
    }

    /// <summary>What a member should carry for a town visit: a full belt and a few portal scrolls at any level.</summary>
    private const int GoldWanted = 400;

    /// <summary>Gold goes from the richest member to anyone short of a shopping trip, a pile dropped at their feet.</summary>
    public static async Task ShareGold(Party party)
    {
        var inTown = party.Members.Where(member => member.InGame && member.Game.IsInTown()).ToList();
        foreach (var poor in inTown.Where(member => Gold(member) < GoldWanted))
        {
            var rich = inTown.Where(member => member != poor).OrderByDescending(Gold).FirstOrDefault();
            if (rich == null || Gold(rich) <= GoldWanted)
            {
                return;
            }

            await GiveGold(rich, poor, (uint)Math.Min(GoldWanted - Gold(poor), Gold(rich) - GoldWanted));
        }
    }

    /// <summary>
    /// Gold above what each keeps goes to <paramref name="receiver"/>, up to what it can carry: one member gambles with the
    /// party's spare gold rather than each in turn at a listing they all share.
    /// </summary>
    public static async Task PoolGold(IEnumerable<PartyMember> members, PartyMember receiver, Func<PartyMember, int> keep, int cap)
    {
        foreach (var giver in members.Where(member => member != receiver && member.InGame))
        {
            var amount = Math.Min(Gold(giver) - keep(giver), cap - Gold(receiver));
            if (amount > 0)
            {
                await GiveGold(giver, receiver, (uint)amount);
            }
        }
    }

    private static async Task<bool> GiveGold(PartyMember giver, PartyMember receiver, uint amount)
    {
        // Close first, as for items: the gold pooled for gambling was dropped across Lut Gholein from its receiver three
        // times in one night, 216 thousand gold left on the ground.
        for (var attempt = 0; attempt < 4 && giver.Game.Me.Location.Distance(receiver.Game.Me.Location) > HandDistance; attempt++)
        {
            if (WalkTo != null)
            {
                await WalkTo(giver, receiver.Game.Me.Location);
            }
            else
            {
                await giver.Game.MoveToAsync(receiver.Game.Me.Location);
            }

            await Task.Delay(300);
        }

        if (giver.Game.Me.Location.Distance(receiver.Game.Me.Location) > HandDistance)
        {
            Log.Warning("{Giver} could not get to {Receiver} to give {Amount} gold; keeping it", giver.Name, receiver.Name, amount);
            return false;
        }

        var before = Gold(receiver);
        // The new pile, wherever it landed: the server puts it on a free tile nearby, and one exactly five away was not
        // found by a "closer than five" search, so 44,800 gold went back to its giver.
        var piles = receiver.Game.Items.Values.Where(item => item.Ground && item.Name == ItemName.Gold).Select(item => item.Id).ToHashSet();
        giver.Game.DropGold(amount);
        Item pile = null;
        GeneralHelpers.TryWithTimeout(_ => (pile = receiver.Game.Items.Values.FirstOrDefault(item => item.Ground && item.Name == ItemName.Gold
            && !piles.Contains(item.Id) && item.Location.Distance(giver.Game.Me.Location) <= GoldPileReach)) != null, TimeSpan.FromSeconds(3));
        if (pile == null)
        {
            Log.Warning("{Giver} dropped {Amount} gold but {Receiver} cannot see the pile; picking it back up", giver.Name, amount, receiver.Name);
            if (giver.Game.Items.Values.Where(item => item.Ground && item.Name == ItemName.Gold)
                .OrderBy(item => item.Location.Distance(giver.Game.Me.Location)).FirstOrDefault() is { } own)
            {
                giver.Game.PickupItem(own);
            }

            return false;
        }

        await receiver.Game.MoveToAsync(pile);
        receiver.Game.PickupItem(pile);
        var taken = GeneralHelpers.TryWithTimeout(_ => Gold(receiver) > before, TimeSpan.FromSeconds(3));
        Log.Information("{Giver} gave {Amount} gold to {Receiver}, who now has {Gold}", giver.Name, amount, receiver.Name, Gold(receiver));
        return taken;
    }

    private static int Gold(PartyMember member) => member.Game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson);

    public static Task<bool> HandQuestItem(PartyMember giver, PartyMember receiver, Item item) => Hand(giver, receiver, item);

    /// <summary>Puts a weapon in the right hand whatever its score, for a quest item that has to be wielded.</summary>
    public static async Task<bool> WearInHand(PartyMember member, Item item)
    {
        var leftHand = member.Game.Me.Equipment.GetValueOrDefault(DirectoryType.LeftHand);
        if (leftHand != null && leftHand.Classification != ClassificationType.Arrows && !await Unequip(member, DirectoryType.LeftHand))
        {
            return false;
        }

        return await Unequip(member, DirectoryType.RightHand) && await Equip(member, item, DirectoryType.RightHand);
    }

    private static async Task<bool> Hand(PartyMember giver, PartyMember receiver, Item item)
    {
        // Close first, or not at all: the paladin gambled at Elzix and dropped three rings there for the amazon, who stood
        // across Lut Gholein and never saw them.
        for (var attempt = 0; attempt < 4 && giver.Game.Me.Location.Distance(receiver.Game.Me.Location) > HandDistance; attempt++)
        {
            // A path, not a straight line: across Lut Gholein the direct move stopped at the first wall every time.
            if (WalkTo != null)
            {
                await WalkTo(giver, receiver.Game.Me.Location);
            }
            else
            {
                await giver.Game.MoveToAsync(receiver.Game.Me.Location);
            }

            await Task.Delay(300);
        }

        if (giver.Game.Me.Location.Distance(receiver.Game.Me.Location) > HandDistance)
        {
            Log.Warning("{Giver} could not get to {Receiver} to hand over {Item}; keeping it", giver.Name, receiver.Name, item.Name);
            return false;
        }

        // Dropped for a full bag it stayed on the town floor: six gems a visit went to the paladin with no cell free, every
        // one "NOT taken" and left behind, four seconds each.
        if (receiver.Game.Inventory.FindFreeSpace(item) == null && !IsBeltPotion(item))
        {
            Log.Information("{Giver} keeps {Item}: {Receiver} has no room for it", giver.Name, item.Name, receiver.Name);
            return false;
        }

        giver.Game.RemoveItemFromContainer(item);
        if (!GeneralHelpers.TryWithTimeout(_ => giver.Game.CursorItem?.Id == item.Id, TimeSpan.FromSeconds(3)))
        {
            return false;
        }

        giver.Game.DropItem(item);
        await Task.Delay(500);
        var onGround = receiver.Game.Items.Values.FirstOrDefault(ground => ground.Id == item.Id && ground.Ground);
        if (onGround == null)
        {
            Log.Warning("{Giver} dropped {Item} but {Receiver} cannot see it; picking it back up", giver.Name, item.Name, receiver.Name);
            if (giver.Game.Items.Values.FirstOrDefault(ground => ground.Id == item.Id && ground.Ground) is { } dropped)
            {
                giver.Game.PickupItem(dropped);
            }

            return false;
        }

        await receiver.Game.MoveToAsync(onGround);
        receiver.Game.PickupItem(onGround);
        // The receiver's own loops may put the item on or in the belt the moment it lands, so anywhere it holds it counts.
        var taken = GeneralHelpers.TryWithTimeout(_ => receiver.Game.Items.TryGetValue(item.Id, out var held) && !held.Ground
            && receiver.Game.Me.Equipment.Values.Concat(receiver.Game.Inventory.Items).Concat(receiver.Game.Belt.Items).Any(own => own.Id == item.Id),
            TimeSpan.FromSeconds(3));
        Log.Information("{Giver} handed {Quality} {Item} to {Receiver}: {Outcome}", giver.Name, item.Quality, item.Name, receiver.Name, taken ? "taken" : "NOT taken");
        if (!taken && giver.Game.Items.Values.FirstOrDefault(ground => ground.Id == item.Id && ground.Ground) is { } left
            && giver.Game.Inventory.FindFreeSpace(left) != null)
        {
            giver.Game.PickupItem(left);
        }

        return taken;
    }

    private static bool IsBeltPotion(Item item)
        => item.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion;

    private const double HandDistance = 5;

    private const double GoldPileReach = 12;

    /// <summary>Walks a member along a path to a point; set by the bot, which owns the pathing.</summary>
    public static Func<PartyMember, D2NG.Core.D2GS.Point, Task> WalkTo { get; set; }

    private static async Task<bool> Unequip(PartyMember member, DirectoryType slot)
    {
        var game = member.Game;
        if (!game.Me.Equipment.TryGetValue(slot, out var worn))
        {
            return true;
        }

        if (game.Inventory.FindFreeSpace(worn) == null)
        {
            return false;
        }

        game.RemoveBodyItem(slot);
        if (!GeneralHelpers.TryWithTimeout(_ => game.CursorItem?.Id == worn.Id, TimeSpan.FromSeconds(3)))
        {
            return false;
        }

        // The cell is chosen once the item is on the cursor: a belt taken off moves the potions of its upper rows into the
        // bag in the same moment, and one of the amazon's twelve landed on the cell picked for her sash before - the server
        // ignored the put and the sash stayed on her cursor.
        await Task.Delay(150);
        var space = game.Inventory.FindFreeSpace(worn);
        if (space == null)
        {
            return false;
        }

        game.InsertItemIntoContainer(worn, space, ItemContainer.Inventory);
        await Task.Delay(300);
        return GeneralHelpers.TryWithTimeout(_ => game.CursorItem == null, TimeSpan.FromSeconds(3));
    }

    private static async Task<bool> Equip(PartyMember member, Item item, DirectoryType slot)
    {
        var game = member.Game;
        game.RemoveItemFromContainer(item);
        if (!GeneralHelpers.TryWithTimeout(_ => game.CursorItem?.Id == item.Id, TimeSpan.FromSeconds(3)))
        {
            return false;
        }

        game.EquipItem(item, slot);
        var worn = GeneralHelpers.TryWithTimeout(_ => game.Me.Equipment.TryGetValue(slot, out var now) && now.Id == item.Id, TimeSpan.FromSeconds(3));
        if (!worn && game.CursorItem?.Id == item.Id && game.Inventory.FindFreeSpace(item) is { } space)
        {
            game.InsertItemIntoContainer(item, space, ItemContainer.Inventory);
        }

        await Task.Delay(200);
        Log.Information("{Character} {Outcome} {Quality} {Item} in {Slot}", member.Name, worn ? "now wears" : "could NOT wear", item.Quality, item.Name, slot);
        return worn;
    }
}
