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

/// <summary>How an item is weighed against what else the member wears.</summary>
public enum Judged
{
    /// <summary>Against the rest of the outfit: whether it is an upgrade now.</summary>
    Outfit,

    /// <summary>On its own: its resistances and leech as if nothing else gave any.</summary>
    Alone,

    /// <summary>
    /// With every resistance covered by the rest of the outfit: what it is worth once the resistances come from elsewhere, so a
    /// high damage or attack speed item is kept while resist gear holds its slot.
    /// </summary>
    Covered
}

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
    /// What this set item switches on by joining the pieces of its set the member holds, worn, carried or stashed: its own
    /// set properties for each other piece, the next property of every piece already held, the set's bonus for the new
    /// count and its full bonus once complete, all from setitems.bin. Judged against the outfit without what the item takes
    /// the place of: Death's Hand scored its 8% life leech against the 3% of the very gloves it replaced, and Death's Guard
    /// never saw the attack speed it switches on in the gloves.
    /// </summary>
    /// <param name="later">With no other piece held, as if one more turns up: a lone Death's Hand was sold while the party
    /// waited for its sash.</param>
    private double SetBonus(PartyMember member, Item item, DirectoryType? slot = null, bool later = false, Judged judged = Judged.Outfit)
    {
        if (requirements.SetItem(item) is not { } setItem || requirements.Set(setItem.SetIndex) is not { } set)
        {
            return 0;
        }

        var held = HeldSetPieces(member, item).Select(requirements.SetItem).OfType<D2NG.Core.ItemData.SetItemEntry>().ToList();
        var added = GearRules.SetPropertiesAdded(setItem, held, set, later);
        return added.Count == 0 ? 0 : StatScore(member, Synthetic(added, item, member.Level), slot, judged) - 1;
    }

    /// <summary>The other pieces of the item's set a member holds, worn, carried or stashed, one of each.</summary>
    private static IEnumerable<Item> HeldSetPieces(PartyMember member, Item item)
        => member.Game.Inventory.Items.Concat(member.Game.Stash.Items).Concat(member.Game.Me.Equipment.Values)
            .Where(owned => owned.Quality == QualityType.Set && owned.SetCode == item.SetCode && owned.Type != item.Type)
            .DistinctBy(owned => owned.Type);

    /// <summary>The set properties switched on among pieces worn together, by how many of their set they are.</summary>
    private IEnumerable<D2NG.Core.ItemData.PropertyRange> ActiveSetProperties(IEnumerable<Item> worn)
        => worn.Where(IsSetPiece).GroupBy(piece => piece.SetCode).SelectMany(group =>
        {
            var pieces = group.DistinctBy(piece => piece.Type).Select(requirements.SetItem).OfType<D2NG.Core.ItemData.SetItemEntry>().ToList();
            return pieces.Count > 0 && requirements.Set(pieces[0].SetIndex) is { } set ? GearRules.ActiveSetProperties(pieces, set) : [];
        });

    /// <summary>What a piece is worth to a member in the slot it goes in.</summary>
    /// <param name="judged">Against the rest of the outfit, on its own, or with resistances covered (<see cref="Judged"/>).</param>
    private double PieceScore(PartyMember member, Item item, bool later = false, Judged judged = Judged.Outfit)
        => WeaponKinds.Contains(item.Classification) ? WeaponScore(member, item, later, judged)
            : IsWeaponOrShield(item) ? ShieldScore(member, item, later, judged)
            : ArmorScore(member, item, later: later, judged: judged);

    /// <summary>The other pieces of the item's set a member holds, worn, carried or stashed.</summary>
    private static int SetPiecesHeld(PartyMember member, Item item) => HeldSetPieces(member, item).Count();

    /// <summary>
    /// An item carrying set properties, to score them like any item's own; with <paramref name="piece"/>'s id and kind, so it
    /// is judged in that piece's slot, against the outfit without what the piece would replace.
    /// </summary>
    private static Item Synthetic(IEnumerable<D2NG.Core.ItemData.PropertyRange> properties, Item piece, int level)
    {
        var stats = new Dictionary<StatType, ItemProperty>();
        var defense = 0;
        foreach (var property in properties)
        {
            var code = GearRules.BaseProperty(property.Property);
            var amount = GearRules.PropertyAmount(property, level);
            if (code == "ac")
            {
                defense += amount;
                continue;
            }

            foreach (var stat in PropertyStats.GetValueOrDefault(code) ?? [])
            {
                var value = (stats.GetValueOrDefault(stat)?.Value ?? 0) + amount;
                stats[stat] = new ItemProperty { Type = stat, Value = value };
            }
        }

        return new Item { Properties = stats, Defense = (uint)defense, Id = piece.Id, Classification = piece.Classification };
    }

    /// <summary>What the set properties switched on among <paramref name="worn"/> add to a stat at the member's level.</summary>
    private int SetStat(PartyMember member, IEnumerable<Item> worn, StatType type)
        => ActiveSetProperties(worn).Where(property => PropertyStats.GetValueOrDefault(GearRules.BaseProperty(property.Property))?.Contains(type) == true)
            .Sum(property => GearRules.PropertyAmount(property, member.Level));

    /// <summary>The properties.bin codes the scores care about, and the stats they raise.</summary>
    private static readonly Dictionary<string, StatType[]> PropertyStats = new(StringComparer.Ordinal)
    {
        ["str"] = [StatType.Strength], ["dex"] = [StatType.Dexterity], ["vit"] = [StatType.Vitality], ["enr"] = [StatType.Energy],
        ["hp"] = [StatType.Life], ["mana"] = [StatType.Mana],
        ["lifesteal"] = [StatType.MinimumLifeStolenPerHit], ["manasteal"] = [StatType.MinimumManaStolenPerHit],
        ["res-fire"] = [StatType.FireResistance], ["res-cold"] = [StatType.ColdResistance], ["res-ltng"] = [StatType.LightningResistance],
        ["res-pois"] = [StatType.PoisonResistance],
        ["res-fire-max"] = [StatType.MaximumFireResistance], ["res-cold-max"] = [StatType.MaximumColdResistance],
        ["res-ltng-max"] = [StatType.MaximumLightningResistance], ["res-pois-max"] = [StatType.MaximumPoisonResistance],
        ["res-all"] = [StatType.FireResistance, StatType.ColdResistance, StatType.LightningResistance, StatType.PoisonResistance],
        ["swing1"] = [StatType.IncreasedAttackSpeed], ["swing2"] = [StatType.IncreasedAttackSpeed], ["swing3"] = [StatType.IncreasedAttackSpeed],
        ["cast1"] = [StatType.FasterCastRate], ["cast2"] = [StatType.FasterCastRate], ["cast3"] = [StatType.FasterCastRate],
        ["balance1"] = [StatType.FasterHitRecovery], ["balance2"] = [StatType.FasterHitRecovery], ["balance3"] = [StatType.FasterHitRecovery],
        ["move1"] = [StatType.FasterRunWalk], ["move2"] = [StatType.FasterRunWalk], ["move3"] = [StatType.FasterRunWalk],
        ["block"] = [StatType.IncreasedBlocking], ["att"] = [StatType.AttackRating], ["att%"] = [StatType.ToHitPercent],
        ["nofreeze"] = [StatType.CannotBeFrozen],
        ["dmg-min"] = [StatType.MinimumDamage], ["dmg-max"] = [StatType.MaximumDamage],
        ["fire-min"] = [StatType.MinimumFireDamage], ["fire-max"] = [StatType.MaximumFireDamage],
        ["ltng-min"] = [StatType.MinimumLightningDamage], ["ltng-max"] = [StatType.MaximumLightningDamage],
        ["cold-min"] = [StatType.MinimumColdDamage], ["cold-max"] = [StatType.MaximumColdDamage],
    };


    /// <param name="slot">The slot it would go in: a ring is judged beside the ring in the other one.</param>
    /// <param name="later">Whether it fits the stats the member's plan gives it later, not now.</param>
    /// <param name="judged">Against the rest of the outfit, on its own, or with resistances covered (<see cref="Judged"/>).</param>
    public double ArmorScore(PartyMember member, Item item, DirectoryType? slot = null, bool later = false, Judged judged = Judged.Outfit)
    {
        if (item == null || !Slots.ContainsKey(item.Classification) || !CanWear(member, item, slot, later))
        {
            return -1;
        }

        return StatScore(member, item, slot, judged) + SetBonus(member, item, slot, later, judged);
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

    private static int ResistPenalty(PartyMember member)
        => GearRules.ResistPenalty(member.Game.Difficulty, member.Game.ClientCharacter?.IsExpansion == true);

    /// <summary>
    /// An item's resistance against the member's whole outfit, the difficulty's penalty taken off: a point that lifts the
    /// resistance out of the negative counts double - one very low resistance is what a fight finds - a point up to 75 once,
    /// a point beyond a tenth. Worn items it would take the place of are left out, so a worn item that holds a resistance
    /// up counts for what losing it would cost.
    /// </summary>
    private double ResistValue(PartyMember member, Item item, StatType type, DirectoryType? slot, Judged judged)
    {
        var add = Stat(item, type);
        if (add <= 0)
        {
            return add;
        }

        if (judged == Judged.Covered)
        {
            return add * 0.1;
        }

        var have = Have(member, item, type, slot, judged) - ResistPenalty(member);
        var cap = MaxResist + Have(member, item, MaximumResistance(type), slot, judged) + Stat(item, MaximumResistance(type));
        var below = Math.Clamp(-have, 0, add);
        var upToCap = Math.Clamp(cap - Math.Max(have, 0), 0, add - below);
        return below * 2 + upToCap + (add - below - upToCap) * 0.1;
    }

    /// <summary>The expansion's raise of a resistance's cap above 75.</summary>
    private static StatType MaximumResistance(StatType resistance) => resistance switch
    {
        StatType.FireResistance => StatType.MaximumFireResistance,
        StatType.ColdResistance => StatType.MaximumColdResistance,
        StatType.LightningResistance => StatType.MaximumLightningResistance,
        _ => StatType.MaximumPoisonResistance
    };

    /// <summary>
    /// What the rest of the member's outfit adds to a stat, without the item and what it would take the place of, the set
    /// bonuses of the pieces that stay included, and Natural Resistance; nothing when the item is judged alone.
    /// </summary>
    private int Have(PartyMember member, Item item, StatType type, DirectoryType? slot, Judged judged = Judged.Outfit)
    {
        if (judged == Judged.Alone)
        {
            return 0;
        }

        var staying = member.Game.Me.Equipment.Values.Where(worn => worn.Id != item.Id && !Replaces(member, item, worn, slot)).ToList();
        return staying.Concat(Charms(member)).Sum(worn => Stat(worn, type)) + SetStat(member, staying, type) + SkillResistance(member, type);
    }

    /// <summary>Charms work from the bag, in the expansion; classic has none.</summary>
    private static IEnumerable<Item> Charms(PartyMember member)
        => member.Game.Inventory.Items.Where(item => item.Classification is ClassificationType.SmallCharm or ClassificationType.LargeCharm or ClassificationType.GrandCharm);

    /// <summary>What the member's skills add to a resistance: the barbarian's Natural Resistance.</summary>
    private static int SkillResistance(PartyMember member, StatType type)
        => type is StatType.FireResistance or StatType.ColdResistance or StatType.LightningResistance or StatType.PoisonResistance
            ? GearRules.NaturalResistance(member.SkillLevel(D2NG.Core.D2GS.Players.Skill.NaturalResistance))
            : 0;

    /// <summary>
    /// What <paramref name="item"/> adds to a stat that only counts up to <paramref name="target"/>, summed over the rest of
    /// what the member wears: in full up to it, a tenth beyond. Worn items it would take the place of are left out.
    /// </summary>
    private double UpToTarget(PartyMember member, Item item, StatType type, int target, DirectoryType? slot = null, Judged judged = Judged.Outfit)
    {
        var add = Stat(item, type);
        if (add <= 0)
        {
            return add;
        }

        var gap = Math.Max(0, target - Have(member, item, type, slot, judged));
        return Math.Min(add, gap) + Math.Max(0, add - gap) * 0.1;
    }

    /// <summary>Resistances, up to 75 after the difficulty's penalty, weighted for the difficulty.</summary>
    private double ResistScore(PartyMember member, Item item, DirectoryType? slot = null, Judged judged = Judged.Outfit)
    {
        // Lightning and fire are what monsters hit with in every act - the Burning Souls took 140-390 life in a blink from
        // members with next to none - cold some, poison little. Cold a little more than it was: in the Chaos Sanctuary chill
        // was the harmful state put on every member most often, and the party's lowest resistances were cold ones.
        var resists = (ResistValue(member, item, StatType.FireResistance, slot, judged) + ResistValue(member, item, StatType.LightningResistance, slot, judged)) * 1.4
            + ResistValue(member, item, StatType.ColdResistance, slot, judged) * 1.2 + ResistValue(member, item, StatType.PoisonResistance, slot, judged) / 2.0;
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

    private double LeechScore(PartyMember member, Item item, DirectoryType? slot = null, Judged judged = Judged.Outfit)
        => (UpToTarget(member, item, StatType.MinimumLifeStolenPerHit, LifeLeechTargetFor(member), slot, judged)
            + UpToTarget(member, item, StatType.MinimumManaStolenPerHit, ManaLeechTarget, slot, judged))
            * LeechToResist * ResistWeight(member);

    /// <summary>
    /// Strength and dexterity as the member's damage uses them: a melee build hits harder with strength and needs it for
    /// its heavy maces, while dexterity gives it only attack rating.
    /// </summary>
    private static (double Strength, double Dexterity) AttributeWeights(PartyMember member, bool caster)
        => !caster && member.Role == PartyRole.Melee ? (2, 0.5) : (1, 1);

    /// <summary>What an item's properties and defense are worth to the member, whatever slot it goes in.</summary>
    private double StatScore(PartyMember member, Item item, DirectoryType? slot = null, Judged judged = Judged.Outfit)
    {
        var caster = IsCaster(member);
        var (strength, dexterity) = AttributeWeights(member, caster);
        var attributes = Stat(item, StatType.Strength) * strength + Stat(item, StatType.Dexterity) * dexterity + Stat(item, StatType.Vitality) * 1.5
            + Stat(item, StatType.Energy) * (caster ? 0.5 : 0);
        var leech = caster ? 0 : LeechScore(member, item, slot, judged);
        var damage = caster ? 0 : OffWeaponOffense(member, item);
        return 1 + item.Defense * ClassWeights(member).Defense + ResistScore(member, item, slot, judged) + Stat(item, StatType.Life) + Stat(item, StatType.Mana) * (caster ? 0.5 : 0.1)
            + attributes + SkillValue(member, item) * SkillWeight(item, 15) + SpeedAndBlock(member, item, slot, judged)
            + Stat(item, StatType.FasterRunWalk) * 0.5 + leech + damage + FreezeValue(member, item, slot, judged);
    }

    /// <summary>
    /// Chance to hit is 2 x AR / (AR + defense) x alvl / (alvl + dlvl): at 2500 attack rating against a Hell monster's 2000
    /// defense, 100 attack rating lands some 1.8% more hits, and a percent of it is 25 points. Typical figures, not one
    /// target's.
    /// </summary>
    private const double HitsPerAttackRating = 0.018;

    private const double HitsPerAttackRatingPercent = 0.45;

    /// <summary>Below this a weapon's damage is not a fair base for what added damage is worth: early bows hit for a few points.</summary>
    private const double MinimumWeaponDamage = 10;

    /// <summary>
    /// Damage and attack rating on armor and jewellery for a physical attacker, as the percent more damage they bring per
    /// hit or the percent more hits, each percent worth what a percent more shots is (<see cref="AttackRatePercentValue"/>).
    /// Counted as points of weapon damage, 150 attack rating on a ring was worth three points, less than one point of fire
    /// resistance in Hell, and the barbarian's attack rating was never worth a slot.
    /// </summary>
    private double OffWeaponOffense(PartyMember member, Item item)
    {
        var weapon = member.Game.Me.Equipment.GetValueOrDefault(DirectoryType.RightHand);
        var perHit = weapon != null && WeaponKinds.Contains(weapon.Classification) ? AverageDamage(weapon) * MasteryFactor(member, weapon) : 0;
        var added = (Stat(item, StatType.MinimumDamage) + Stat(item, StatType.MaximumDamage)) / 2.0 + ElementalDamage(item);
        var damage = added / Math.Max(perHit, MinimumWeaponDamage) * 100;
        var hits = AttackRating(member, item) * HitsPerAttackRating + Stat(item, StatType.ToHitPercent) * HitsPerAttackRatingPercent;
        return (damage + hits) * AttackRatePercentValue;
    }

    /// <summary>
    /// Cannot be frozen, worth this many points of cold resistance below the cap. Chill was the harmful state put on the
    /// party most often in the Chaos Sanctuary; Death's Guard carries it and scored nothing for it.
    /// </summary>
    private const int CannotBeFrozenResist = 15;

    private double FreezeValue(PartyMember member, Item item, DirectoryType? slot, Judged judged)
        => Stat(item, StatType.CannotBeFrozen) > 0 && Have(member, item, StatType.CannotBeFrozen, slot, judged) == 0
            ? CannotBeFrozenResist * 1.2 * ResistWeight(member)
            : 0;

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
                var collector = present.Where(member => !ReservedForAnotherClass(member, piece))
                    .OrderByDescending(member => SetPiecesHeld(member, piece)).ThenByDescending(member => PieceScore(member, piece)).FirstOrDefault();
                if (collector == null)
                {
                    continue;
                }

                var better = ReservedForAnotherClass(holder, piece) || SetPiecesHeld(collector, piece) > SetPiecesHeld(holder, piece)
                    || (SetPiecesHeld(collector, piece) == SetPiecesHeld(holder, piece) && PieceScore(collector, piece) > PieceScore(holder, piece));
                if (collector != holder && better && await Hand(holder, collector, piece))
                {
                    Log.Information("{Holder} handed set {Item} to {Collector}, who holds {Pieces} of its set", holder.Name, piece.Name, collector.Name, SetPiecesHeld(collector, piece));
                    await WearUpgrades(collector);
                }
            }
        }

        await ShareWornGear(present);

        for (var pass = 0; pass < SharePasses; pass++)
        {
            if (!await ShareBags(present))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Bag passes per sharing: what a member takes off for an upgrade lands in its bag after that member's bag was gone
    /// through, and waited a whole town visit to be offered to the others.
    /// </summary>
    private const int SharePasses = 3;

    /// <summary>Every wearable item in anyone's bag goes to the member it improves most and is put on; whether anything moved.</summary>
    private async Task<bool> ShareBags(List<PartyMember> present)
    {
        var moved = false;
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
                moved |= holder.Game.Inventory.FindItemById(item.Id) == null;
            }
        }

        return moved;
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
