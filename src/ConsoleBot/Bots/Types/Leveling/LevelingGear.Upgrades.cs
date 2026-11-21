using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Armor and jewellery beyond the empty slot: a score per build, upgrades worn as they come, passed to the
/// member they improve most, and bought from the merchants with the party's gold - the spending order's
/// vendor step, which is where most of a leveling character's gear comes from.
/// </summary>
public sealed partial class LevelingGear
{
    /// <summary>Gold a member keeps for potions and scrolls; only what is above it goes on gear.</summary>
    private const int GearReserve = 600;

    /// <summary>A purchase has to improve the score by at least this much; smaller gains are not worth the gold a leveling party has.</summary>
    private const double MinimumPurchaseGain = 3;

    /// <summary>Above this a member buys any upgrade at all: the amazon carried 11,866 gold and nothing beat the bar.</summary>
    private const int RichGold = 5000;

    /// <summary>Merchant items a purchase failed on, mostly for want of gold; a merchant's ids last the game.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _refusedOffers = new();

    public void ForgetGame() => _refusedOffers.Clear();

    /// <summary>
    /// Set items are sold like the rest unless they improve someone, their set's bonus with the pieces held counted in:
    /// keeping every one filled the barbarian's bag with two Berserker's Hauberks, two Milabrega's Orbs, Isenhart's
    /// Lightbrand and Vidala's Fetlock that nobody wore, and Cathan's Seal sat in the paladin's.
    /// </summary>
    public static bool IsSetPiece(Item item) => item.Quality == QualityType.Set;

    /// <summary>
    /// What wearing this set item adds through its set, given the pieces of that set the member already has: the item's
    /// own set properties for each piece past the first, the set's bonuses up to that count and its full bonus once
    /// complete, all from setitems.bin and scored with the same weights as an item's own properties.
    /// </summary>
    private double SetBonus(PartyMember member, Item item)
    {
        if (requirements.SetItem(item) is not { } setItem || requirements.Set(setItem.SetIndex) is not { } set)
        {
            return 0;
        }

        var worn = member.Game.Inventory.Items.Concat(member.Game.Me.Equipment.Values).Append(item)
            .Where(owned => owned.Quality == QualityType.Set && owned.SetCode == item.SetCode).Select(owned => owned.Type).Distinct().Count();
        if (worn < 2)
        {
            return 0;
        }

        var properties = setItem.SetProperties.Take(worn - 1)
            .Concat(set.Partial.Where(pair => pair.Key <= worn).SelectMany(pair => pair.Value))
            .Concat(worn >= set.ItemCount ? set.Full : []);
        return StatScore(member, Synthetic(properties)) - 1;
    }

    /// <summary>What a piece is worth to a member in the slot it goes in.</summary>
    /// <param name="alone">Judged on its own, not against the rest of the outfit (<see cref="Have"/>).</param>
    private double PieceScore(PartyMember member, Item item, bool later = false, bool alone = false)
        => WeaponKinds.Contains(item.Classification) ? WeaponScore(member, item, later, alone)
            : IsWeaponOrShield(item) ? ShieldScore(member, item, later, alone)
            : ArmorScore(member, item, later: later, alone: alone);

    /// <summary>The other pieces of the item's set a member holds, worn or carried.</summary>
    private static int SetPiecesHeld(PartyMember member, Item item)
        => member.Game.Inventory.Items.Concat(member.Game.Me.Equipment.Values)
            .Count(owned => owned.Quality == QualityType.Set && owned.SetCode == item.SetCode && owned.Id != item.Id);

    /// <summary>An item carrying set properties, to score them like any item's own.</summary>
    private static Item Synthetic(IEnumerable<D2NG.Core.ItemData.PropertyRange> properties)
    {
        var stats = new Dictionary<StatType, ItemProperty>();
        var defense = 0;
        foreach (var property in properties)
        {
            if (property.Property == "ac")
            {
                defense += property.Max;
                continue;
            }

            foreach (var stat in PropertyStats.GetValueOrDefault(property.Property) ?? [])
            {
                var value = (stats.GetValueOrDefault(stat)?.Value ?? 0) + property.Max;
                stats[stat] = new ItemProperty { Type = stat, Value = value };
            }
        }

        return new Item { Properties = stats, Defense = (uint)defense };
    }

    /// <summary>The properties.bin codes the scores care about, and the stats they raise.</summary>
    private static readonly Dictionary<string, StatType[]> PropertyStats = new(StringComparer.Ordinal)
    {
        ["str"] = [StatType.Strength], ["dex"] = [StatType.Dexterity], ["vit"] = [StatType.Vitality], ["enr"] = [StatType.Energy],
        ["hp"] = [StatType.Life], ["mana"] = [StatType.Mana],
        ["lifesteal"] = [StatType.MinimumLifeStolenPerHit], ["manasteal"] = [StatType.MinimumManaStolenPerHit],
        ["res-fire"] = [StatType.FireResistance], ["res-cold"] = [StatType.ColdResistance], ["res-ltng"] = [StatType.LightningResistance],
        ["res-pois"] = [StatType.PoisonResistance],
        ["res-all"] = [StatType.FireResistance, StatType.ColdResistance, StatType.LightningResistance, StatType.PoisonResistance],
        ["swing1"] = [StatType.IncreasedAttackSpeed], ["swing2"] = [StatType.IncreasedAttackSpeed], ["swing3"] = [StatType.IncreasedAttackSpeed],
        ["cast1"] = [StatType.FasterCastRate], ["cast2"] = [StatType.FasterCastRate], ["cast3"] = [StatType.FasterCastRate],
        ["balance1"] = [StatType.FasterHitRecovery], ["balance2"] = [StatType.FasterHitRecovery], ["balance3"] = [StatType.FasterHitRecovery],
        ["move1"] = [StatType.FasterRunWalk], ["move2"] = [StatType.FasterRunWalk], ["move3"] = [StatType.FasterRunWalk],
        ["block"] = [StatType.IncreasedBlocking], ["att"] = [StatType.AttackRating], ["att%"] = [StatType.ToHitPercent],
        ["dmg-min"] = [StatType.MinimumDamage], ["dmg-max"] = [StatType.MaximumDamage],
        ["fire-min"] = [StatType.MinimumFireDamage], ["fire-max"] = [StatType.MaximumFireDamage],
        ["ltng-min"] = [StatType.MinimumLightningDamage], ["ltng-max"] = [StatType.MaximumLightningDamage],
        ["cold-min"] = [StatType.MinimumColdDamage], ["cold-max"] = [StatType.MaximumColdDamage],
    };


    /// <param name="slot">The slot it would go in: a ring is judged beside the ring in the other one.</param>
    /// <param name="later">Whether it fits the stats the member's plan gives it later, not now.</param>
    /// <param name="alone">Judged on its own: its resistances and leech as if nothing else gave any.</param>
    public double ArmorScore(PartyMember member, Item item, DirectoryType? slot = null, bool later = false, bool alone = false)
    {
        if (item == null || !Slots.ContainsKey(item.Classification) || !CanWear(member, item, slot, later))
        {
            return -1;
        }

        return StatScore(member, item, slot, alone) + SetBonus(member, item);
    }

    private const int MaxResist = 75;

    /// <summary>
    /// Resistances from gear, not from an aura: a resist aura in place of Fanaticism or Concentration costs the whole
    /// party damage. They count for more the harder the difficulty, whose penalty they have to make up.
    /// </summary>
    private static double ResistWeight(PartyMember member) => member.Game.Difficulty switch
    {
        D2NG.Core.D2GS.Enums.Difficulty.Nightmare => 4,
        D2NG.Core.D2GS.Enums.Difficulty.Hell => 5,
        _ => 2.5
    };

    private static int ResistPenalty(PartyMember member) => member.Game.Difficulty switch
    {
        D2NG.Core.D2GS.Enums.Difficulty.Nightmare => 40,
        D2NG.Core.D2GS.Enums.Difficulty.Hell => 100,
        _ => 0
    };

    /// <summary>
    /// An item's resistance against the member's whole outfit, the difficulty's penalty taken off: a point that lifts the
    /// resistance out of the negative counts double - one very low resistance is what a fight finds - a point up to 75 once,
    /// a point beyond a tenth. Worn items it would take the place of are left out, so a worn item that holds a resistance
    /// up counts for what losing it would cost.
    /// </summary>
    private double ResistValue(PartyMember member, Item item, StatType type, DirectoryType? slot, bool alone)
    {
        var add = Stat(item, type);
        if (add <= 0)
        {
            return add;
        }

        var have = Have(member, item, type, slot, alone) - ResistPenalty(member);
        var below = Math.Clamp(-have, 0, add);
        var upToCap = Math.Clamp(MaxResist - Math.Max(have, 0), 0, add - below);
        return below * 2 + upToCap + (add - below - upToCap) * 0.1;
    }

    /// <summary>
    /// What the rest of the member's outfit adds to a stat, without the item and what it would take the place of; nothing
    /// when the item is judged <paramref name="alone"/>.
    /// </summary>
    private int Have(PartyMember member, Item item, StatType type, DirectoryType? slot, bool alone = false)
        => alone ? 0 : member.Game.Me.Equipment.Values.Where(worn => worn.Id != item.Id && !Replaces(member, item, worn, slot)).Sum(worn => Stat(worn, type));

    /// <summary>
    /// What <paramref name="item"/> adds to a stat that only counts up to <paramref name="target"/>, summed over the rest of
    /// what the member wears: in full up to it, a tenth beyond. Worn items it would take the place of are left out.
    /// </summary>
    private double UpToTarget(PartyMember member, Item item, StatType type, int target, DirectoryType? slot = null, bool alone = false)
    {
        var add = Stat(item, type);
        if (add <= 0)
        {
            return add;
        }

        var gap = Math.Max(0, target - Have(member, item, type, slot, alone));
        return Math.Min(add, gap) + Math.Max(0, add - gap) * 0.1;
    }

    /// <summary>Resistances, up to 75 after the difficulty's penalty, weighted for the difficulty.</summary>
    private double ResistScore(PartyMember member, Item item, DirectoryType? slot = null, bool alone = false)
    {
        // Lightning and fire are what monsters hit with in every act - the Burning Souls took 140-390 life in a blink from
        // members with next to none - cold some, poison little. Cold a little more than it was: in the Chaos Sanctuary chill
        // was the harmful state put on every member most often, and the party's lowest resistances were cold ones.
        var resists = (ResistValue(member, item, StatType.FireResistance, slot, alone) + ResistValue(member, item, StatType.LightningResistance, slot, alone)) * 1.4
            + ResistValue(member, item, StatType.ColdResistance, slot, alone) * 1.2 + ResistValue(member, item, StatType.PoisonResistance, slot, alone) / 2.0;
        return resists * ResistWeight(member);
    }

    /// <summary>
    /// Life and mana leech a physical attacker wants in all, summed over what it wears. Below it leech heals what the
    /// fight takes, above it a little more does little: the party sold every leech ring in Hell for resist rings while
    /// the amazon and the barbarian stood with one leech item each.
    /// </summary>
    private const int LifeLeechTarget = 12;

    /// <summary>
    /// The whirlwind barbarian's: he lives off his leech while he keeps whirling, and in the Chaos Sanctuary that is what
    /// carries him through a pack - standing still or running is what kills him. 10% fell short there, 20% counts in full.
    /// </summary>
    private const int WhirlerLifeLeechTarget = 20;

    private static int LifeLeechTargetFor(PartyMember member)
        => member.Stage.Attack == D2NG.Core.D2GS.Players.Skill.Whirlwind ? WhirlerLifeLeechTarget : LifeLeechTarget;

    /// <summary>
    /// Mana leech every physical attacker needs: Multiple Shot and Guided Arrow cost the amazon mana on every shot, and a
    /// whirl costs the barbarian 30 of his 180. With 3% from his gloves he was under 15% mana for a third of his fighting in
    /// the Chaos Sanctuary, though nine whirls in ten hit, and swung plain attacks for a fifth of it.
    /// </summary>
    private const int ManaLeechTarget = 8;

    /// <summary>
    /// A percent of leech below the target is worth this many percent of resistance below the cap: 6% on a ring beats a
    /// ring of 15 to every resistance for a member with no leech, not for one that has enough.
    /// </summary>
    private const double LeechToResist = 12;

    private double LeechScore(PartyMember member, Item item, DirectoryType? slot = null, bool alone = false)
        => (UpToTarget(member, item, StatType.MinimumLifeStolenPerHit, LifeLeechTargetFor(member), slot, alone)
            + UpToTarget(member, item, StatType.MinimumManaStolenPerHit, ManaLeechTarget, slot, alone))
            * LeechToResist * ResistWeight(member);

    /// <summary>
    /// Strength and dexterity as the member's damage uses them: a melee build hits harder with strength and needs it for
    /// its heavy maces, while dexterity gives it only attack rating.
    /// </summary>
    private static (double Strength, double Dexterity) AttributeWeights(PartyMember member, bool caster)
        => !caster && member.Role == PartyRole.Melee ? (2, 0.5) : (1, 1);

    /// <summary>What an item's properties and defense are worth to the member, whatever slot it goes in.</summary>
    private double StatScore(PartyMember member, Item item, DirectoryType? slot = null, bool alone = false)
    {
        var caster = IsCaster(member);
        var (strength, dexterity) = AttributeWeights(member, caster);
        var attributes = Stat(item, StatType.Strength) * strength + Stat(item, StatType.Dexterity) * dexterity + Stat(item, StatType.Vitality) * 1.5
            + Stat(item, StatType.Energy) * (caster ? 0.5 : 0);
        var leech = caster ? 0 : LeechScore(member, item, slot, alone);
        // Damage on a ring, an amulet or gloves goes on every hit of a physical attacker; nothing scored it before.
        var damage = caster ? 0 : (Stat(item, StatType.MinimumDamage) + Stat(item, StatType.MaximumDamage)) / 2.0 + ElementalDamage(item) + AttackRatingValue(item);
        return 1 + item.Defense * 0.5 + ResistScore(member, item, slot, alone) + Stat(item, StatType.Life) + Stat(item, StatType.Mana) * (caster ? 0.5 : 0.1)
            + attributes + SkillValue(member, item) * SkillWeight(item, 15) + SpeedAndBlock(member, item, slot, alone)
            + Stat(item, StatType.FasterRunWalk) * 0.5 + leech + damage;
    }

    /// <summary>How much better off the member is wearing this item in place of what it wears now; negative when not at all.</summary>
    /// <param name="later">With the stats its plan gives it later, for an item it cannot wear yet.</param>
    public double SlotGain(PartyMember member, Item item, out DirectoryType slot, bool later = false)
    {
        slot = DirectoryType.NotApplicable;
        if (IsWeaponOrShield(item))
        {
            var gain = Gain(member, item, later);
            slot = WeaponKinds.Contains(item.Classification) ? DirectoryType.RightHand : DirectoryType.LeftHand;
            return gain;
        }

        if (!Slots.TryGetValue(item.Classification, out var slots))
        {
            return -1;
        }

        var best = -1.0;
        foreach (var candidate in slots)
        {
            var score = ArmorScore(member, item, candidate, later);
            if (score < 0)
            {
                continue;
            }

            var worn = member.Game.Me.Equipment.GetValueOrDefault(candidate);
            var current = worn == null ? 0 : Math.Max(0, ArmorScore(member, worn));
            var gain = score > current * UpgradeMargin ? score - current : -1;
            if (gain > best)
            {
                best = gain;
                slot = candidate;
            }
        }

        return best;
    }

    /// <summary>Whether any member, wherever it is, wears this better than what it has now - every member is weighed against every item.</summary>
    public bool WantedByAnyone(Party party, Item item)
        => party.Members.Where(member => member.InGame).Any(member => SlotGain(member, item, out _) > 0);

    /// <summary>Puts on every item in the member's own inventory that beats what it wears.</summary>
    public async Task WearUpgrades(PartyMember member)
    {
        if (AwaitsCorpse(member))
        {
            return;
        }

        foreach (var item in member.Game.Inventory.Items.Where(item => item.IsIdentified || item.Quality < QualityType.Magical).ToList())
        {
            if (SlotGain(member, item, out var slot) <= 0)
            {
                continue;
            }

            if (IsWeaponOrShield(item))
            {
                await WearWeapon(member, item);
            }
            else if (await Unequip(member, slot) && await Equip(member, item, slot))
            {
                Log.Information("{Character} upgraded {Slot} to {Quality} {Item} (score {Score:0})", member.Name, slot, item.Quality, item.Name, ArmorScore(member, item));
            }
        }
    }

    /// <summary>Any piece of gear in anyone's inventory goes to the member it improves most, and is put on.</summary>
    public async Task ShareGear(Party party)
    {
        var present = party.Members.Where(member => member.InGame && member.Game.IsInTown() && !AwaitsCorpse(member)).ToList();
        // A set item goes to the member already holding most of its set, else to the one it suits best, so the pieces
        // come together on one character and their bonus can count.
        foreach (var holder in present)
        {
            foreach (var piece in holder.Game.Inventory.Items.Where(IsSetPiece).ToList())
            {
                // Most pieces of its set first, then whoever it suits best in its own slot; only handed when that beats
                // the holder - a set broad sword nobody held a piece of went to whoever came first.
                var collector = present.OrderByDescending(member => SetPiecesHeld(member, piece)).ThenByDescending(member => PieceScore(member, piece)).First();
                var better = SetPiecesHeld(collector, piece) > SetPiecesHeld(holder, piece)
                    || (SetPiecesHeld(collector, piece) == SetPiecesHeld(holder, piece) && PieceScore(collector, piece) > PieceScore(holder, piece));
                if (collector != holder && better && await Hand(holder, collector, piece))
                {
                    Log.Information("{Holder} handed set {Item} to {Collector}, who holds {Pieces} of its set", holder.Name, piece.Name, collector.Name, SetPiecesHeld(collector, piece));
                    await WearUpgrades(collector);
                }
            }
        }

        await ShareWornGear(present);

        foreach (var holder in present)
        {
            foreach (var item in holder.Game.Inventory.Items.Where(item => !LevelingGems.ReservedForDiamonds(item) && (item.IsIdentified || item.Quality < QualityType.Magical)
                && (IsWeaponOrShield(item) || Slots.ContainsKey(item.Classification))).ToList())
            {
                var best = present.Select(member => (Member: member, Gain: SlotGain(member, item, out _))).Where(pair => pair.Gain > 0)
                    .OrderByDescending(pair => pair.Gain).FirstOrDefault();
                if (best.Member == null || (best.Member != holder && !await Hand(holder, best.Member, item)))
                {
                    continue;
                }

                await WearUpgrades(best.Member);
            }
        }
    }

    /// <summary>A worn item changes hands only for at least this much more than the wearer loses.</summary>
    private const double MinimumSwapGain = 10;

    /// <summary>
    /// Worn gear goes to another member when it helps that one more than it helps its wearer: the barbarian wore two 7% life
    /// leech rings on top of his weapon's 5% while the amazon had no life leech at all. The wearer puts on the best it has
    /// spare, and what the other one takes off goes round like any other item.
    /// </summary>
    private async Task ShareWornGear(List<PartyMember> present)
    {
        foreach (var holder in present)
        {
            foreach (var (slot, worn) in holder.Game.Me.Equipment.Where(pair => Slots.ContainsKey(pair.Value.Classification) && !IsSetPiece(pair.Value)).ToList())
            {
                var loses = Math.Max(0, Math.Max(0, ArmorScore(holder, worn)) - SpareScore(holder, slot));
                var taker = present.Where(member => member != holder && member.InGame)
                    .Select(member => (Member: member, Gain: SlotGain(member, worn, out _)))
                    .Where(pair => pair.Gain > loses * UpgradeMargin + MinimumSwapGain)
                    .OrderByDescending(pair => pair.Gain).FirstOrDefault();
                if (taker.Member == null || !await Unequip(holder, slot))
                {
                    continue;
                }

                Log.Information("{Holder} gives its worn {Quality} {Item} to {Taker}, worth {Gain:0} more to it and {Loses:0} less to the wearer", holder.Name,
                    worn.Quality, worn.Name, taker.Member.Name, taker.Gain, loses);
                if (holder.Game.Inventory.FindItemById(worn.Id) is { } item && await Hand(holder, taker.Member, item))
                {
                    await WearUpgrades(taker.Member);
                }

                await WearUpgrades(holder);
            }
        }
    }

    /// <summary>The best the member carries spare for the slot, scored as it would be there.</summary>
    private double SpareScore(PartyMember member, DirectoryType slot)
        => member.Game.Inventory.Items
            .Where(item => (item.IsIdentified || item.Quality < QualityType.Magical) && Slots.TryGetValue(item.Classification, out var slots) && slots.Contains(slot))
            .Select(item => ArmorScore(member, item, slot))
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>
    /// Buys, from the open merchant, the best upgrade per slot the member can afford above its reserve. A
    /// merchant's stock is identified, so the score sees every property.
    /// </summary>
    public bool IsTwoHandedWeaponWorn(PartyMember member)
        => member.Game.Me.Equipment.GetValueOrDefault(DirectoryType.RightHand) is { } weapon && IsTwoHanded(weapon);

    /// <summary>
    /// A body to recover is picked up with nothing on: gear put on before it makes the game refuse the corpse, and its
    /// items are lost with it.
    /// </summary>
    private static bool AwaitsCorpse(PartyMember member)
        => member.Game.Me.CorpseId is { } corpse && member.Game.Players.Any(player => player.Id == corpse);

    public static int Gold(Game game) => game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson);

    private static readonly Lazy<D2NG.Core.ItemData.ItemPriceTable> Prices = new(() =>
    {
        try
        {
            return D2NG.Core.ItemData.ItemPriceTable.Load(GameDataLocation.Resolve(GameDataLocation.ItemPricesFile));
        }
        catch (Exception exception) when (exception is System.IO.IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Warning("No item prices; purchases are tried without knowing their cost: {Reason}", exception.Message);
            return null;
        }
    });

    /// <summary>The gold a merchant pays for the item, or null without the price table.</summary>
    internal static int? SellValue(Item item, Game game)
        => Prices.Value is { } table ? D2NG.Core.ItemData.ItemPrice.SellValue(item, table, game.Difficulty) : null;

    /// <summary>What <paramref name="npc"/> charges for the item, from its sell multiplier in the vendor table.</summary>
    private static int? Cost(Item item, WorldObject npc)
        => Prices.Value is { } table ? D2NG.Core.ItemData.ItemPrice.BuyCost(item, table, npc.NPCCode.ToString()) : null;

    public async Task BuyUpgrades(PartyMember member, WorldObject npc)
    {
        var game = member.Game;
        // An upgrade already in the bag counts: a scepter bought at one merchant is not to be beaten by a mace at the next.
        var held = game.Inventory.Items.Where(item => item.IsIdentified || item.Quality < QualityType.Magical)
            .Select(item => (Gain: SlotGain(member, item, out var slot), Slot: slot)).Where(pair => pair.Gain > 0)
            .GroupBy(pair => pair.Slot).ToDictionary(group => group.Key, group => group.Max(pair => pair.Gain));
        var twoHanded = member.Plan.Weapons.Count > 0 && !member.Plan.Shield;
        var offers = game.Items.Values.Where(item => item.IsInMerchantTab())
            .Select(item => (Item: item, Gain: SlotGain(member, item, out var slot), Slot: slot))
            .Where(offer => !_refusedOffers.ContainsKey(offer.Item.Id) && offer.Gain > held.GetValueOrDefault(offer.Slot) * UpgradeMargin && offer.Gain >= (Gold(game) > RichGold ? 1 : MinimumPurchaseGain)
                && !(offer.Slot == DirectoryType.LeftHand && (twoHanded || IsTwoHandedWeaponWorn(member))))
            .GroupBy(offer => offer.Slot)
            .Select(group => group.OrderByDescending(offer => offer.Gain).First())
            .OrderByDescending(offer => offer.Gain)
            .ToList();
        foreach (var offer in offers)
        {
            if (Gold(game) <= GearReserve || game.Inventory.FindFreeSpace(offer.Item) == null)
            {
                return;
            }

            // A purchase needs the gold: what the merchant charges is known from its sell multiplier, so an item the
            // member cannot afford above its reserve is passed over rather than tried and refused.
            if (Cost(offer.Item, npc) is { } cost && cost > Gold(game) - GearReserve)
            {
                continue;
            }

            var before = Gold(game);
            game.BuyItem(npc, offer.Item, buyStack: false);
            var bought = GeneralHelpers.TryWithTimeout(_ => Gold(game) < before, TimeSpan.FromSeconds(2));
            if (!bought)
            {
                _refusedOffers[offer.Item.Id] = true;
            }

            Log.Information("{Character} {Outcome} {Quality} {Item} from {Npc} for {Slot} (gain {Gain:0}, {Cost} gold, computed {Expected})", member.Name,
                bought ? "bought" : "could NOT buy", offer.Item.Quality, offer.Item.Name, npc.NPCCode, offer.Slot, offer.Gain, before - Gold(game), Cost(offer.Item, npc));
            await Task.Delay(300);
        }
    }
}
