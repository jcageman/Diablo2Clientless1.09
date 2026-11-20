using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.ItemData;

/// <summary>
/// The gold a vendor pays for an item, worked out the way the server does: D2MOO's
/// ITEMS_CalculateTransactionCost for a sale, which reproduced every sale captured on the realm.
///
/// An item is worth its base cost (an armor's scaled to its defense roll), plus, once identified,
/// what each of its stats adds through itemstatcost's Add and Multiply, plus what its affixes and
/// staff mods add. A unique replaces the stat pricing with its own multiplier. Socketed items add
/// half their own cost; ethereal and class-specific items are worth a quarter. The vendor pays
/// BuyMultiply/1024 of that (half, on this realm), capped per difficulty. Durability does not
/// matter.
///
/// The integer arithmetic is the game's: products wrap at 32 bits, and above 65535 the game
/// divides by 1024 before multiplying, which is why a Hell sale of an expensive item lands on
/// multiples of 512 (32768, 34816) below the 35000 cap.
/// </summary>
public static class ItemPrice
{
    private const int BuySkillDivisor = 4096;
    private const int RepairSkillDivisor = 1024;

    /// <summary>
    /// Gold paid for <paramref name="item"/> by <paramref name="vendor"/> (an NPCCode name such as
    /// "JamellaAct4"; any vendor when it is not found, as they all pay the same share). Null when
    /// the table has no base for the item.
    /// </summary>
    public static int? SellValue(Item item, ItemPriceTable table, Difficulty difficulty, string vendor = null)
    {
        var basePrice = table.Base(item.Type);
        var npc = table.Vendor(vendor) ?? table.Vendors.FirstOrDefault();
        if (basePrice == null || npc == null)
        {
            return null;
        }

        if (item.IsStartItem)
        {
            return 1;
        }

        var type = table.ItemType(basePrice.Type);
        var quantity = item.Stackable && item.Amount > 0 ? (int)item.Amount : 1;
        var paid = Scale(Worth(item, basePrice, type, table, quantity, forRepair: false), npc.BuyMultiply);

        if (Book(item, table) == null && type?.Quiver != true)
        {
            if (basePrice.Stackable && IsRepairable(item, basePrice, type))
            {
                // A stack is paid as if full, less what refilling it would cost.
                var maxStack = Math.Min(basePrice.MaxStack, 511);
                var repair = Scale(Worth(item, basePrice, type, table, quantity, forRepair: true), npc.RepairMultiply);
                repair = quantity < maxStack && !item.Properties.ContainsKey(StatType.ReplenishesQuantity) ? repair * (maxStack - quantity) : 0;
                paid = paid * maxStack - repair;
            }
            else
            {
                paid *= quantity;
            }
        }

        var cap = npc.MaxBuy.ElementAtOrDefault((int)difficulty);
        if (cap > 0)
        {
            paid = Math.Min(paid, cap);
        }

        return Math.Max(paid, 1);
    }

    /// <summary>
    /// Gold <paramref name="vendor"/> charges for <paramref name="item"/> from its stock: the same worth the vendor
    /// pays a share of, times its SellMultiply/1024 instead. Null when the table has no base for the item.
    /// </summary>
    public static int? BuyCost(Item item, ItemPriceTable table, string vendor = null)
    {
        var basePrice = table.Base(item.Type);
        var npc = table.Vendor(vendor) ?? table.Vendors.FirstOrDefault();
        if (basePrice == null || npc == null)
        {
            return null;
        }

        var type = table.ItemType(basePrice.Type);
        var quantity = item.Stackable && item.Amount > 0 ? (int)item.Amount : 1;
        return Math.Max(Scale(Worth(item, basePrice, type, table, quantity, forRepair: false), npc.SellMultiply), 1);
    }

    private static int Worth(Item item, BasePrice basePrice, ItemTypePrice type, ItemPriceTable table, int quantity, bool forRepair)
    {
        var skillDivisor = forRepair ? RepairSkillDivisor : BuySkillDivisor;
        var divisor = 1;
        int cost;
        if (item.IsEar)
        {
            cost = basePrice.Cost * (int)item.EarLevel;
        }
        else if (Book(item, table) is { } book)
        {
            cost = basePrice.Cost + quantity * book.CostPerCharge;
        }
        else if (type?.Quiver == true)
        {
            cost = (forRepair ? Math.Min(basePrice.MaxStack, 511) : quantity) * basePrice.Cost / 1024;
        }
        else
        {
            cost = basePrice.Cost;
            if (basePrice.Stackable)
            {
                divisor = Math.Clamp(basePrice.MaxStack, 1, 511);
            }
        }

        if (basePrice.MaxDefense != 0 && table.IsA(basePrice.Type, "armo"))
        {
            cost = (int)item.Defense * basePrice.Cost / basePrice.MaxDefense;
        }

        var quality = item.Quality;
        if (quality < QualityType.Magical)
        {
            cost += StaffModCost(item, table, type, cost, skillDivisor) / divisor;
        }

        if (item.IsIdentified)
        {
            var extra = 0;
            switch (quality)
            {
                case QualityType.Rare:
                case QualityType.Crafted:
                    extra += item.Prefixes.Sum(id => AffixCost(table.Prefix((int)id), cost))
                        + item.Suffixes.Sum(id => AffixCost(table.Suffix((int)id), cost));
                    cost += StatCost(item, table, cost, skillDivisor) / divisor;
                    break;
                case QualityType.Unique when table.Unique((int)item.UniqueCode) is { } unique:
                    extra += Term(unique.CostAdd, unique.CostMultiply, 1, cost, 1024);
                    break;
                case QualityType.Unique:
                case QualityType.Magical:
                    extra += AffixCost(table.Prefix((int)item.Prefix), cost) + AffixCost(table.Suffix((int)item.Suffix), cost);
                    cost += StatCost(item, table, cost, skillDivisor) / divisor;
                    break;
                case QualityType.Set when table.SetItem((int)item.SetCode, item.Type) is { } setItem:
                    extra += Term(setItem.CostAdd, setItem.CostMultiply, 1, cost, 1024);
                    break;
                case QualityType.Superior:
                    cost += StatCost(item, table, cost, skillDivisor) / divisor;
                    break;
                case QualityType.Inferior:
                    extra = cost / -2;
                    break;
            }

            cost += extra / divisor;
            if (quality >= QualityType.Magical)
            {
                cost += StaffModCost(item, table, type, cost, skillDivisor) / divisor;
            }
        }

        cost += item.SocketedItems.Values.Sum(socketed => (table.Base(socketed.Type)?.Cost ?? 0) / 2);

        if (!forRepair)
        {
            if (item.Ethereal)
            {
                cost /= 4;
            }

            if (type?.Class != null)
            {
                cost /= 4;
            }

            if (item.Ethereal && !basePrice.NoDurability && basePrice.Durability > 0 && item.MaximumDurability > 0
                && !item.IsIndestructible && item.Durability == 0)
            {
                cost = 0;
            }
        }

        return cost;
    }

    /// <summary>What a prefix or suffix row adds by itself; zero for all but a handful of rows.</summary>
    private static int AffixCost(AffixPrice affix, int cost)
        => affix == null || (affix.Add == 0 && affix.Multiply == 0) ? 0 : Term(affix.Add, affix.Multiply, 1, cost, 1024);

    private static int StatCost(Item item, ItemPriceTable table, int cost, int skillDivisor)
    {
        var total = 0;
        foreach (var (statId, value, property) in PricedStats(item))
        {
            if (value == 0 || table.Stat(statId) is not { } stat)
            {
                continue;
            }

            switch (stat.Encode)
            {
                case 1 when table.Skill((int)property.Skill) is { } skill:
                    total += Term(skill.CostAdd, skill.CostMultiply, value, cost, skillDivisor);
                    break;
                case 2 or 3 when table.Skill((int)property.Skill) is { } skill:
                    total += Term(skill.CostAdd, skill.CostMultiply, property.SkillLevel, cost, skillDivisor);
                    break;
                case 1 or 2 or 3 or 4:
                    break;
                default:
                    total += Term(stat.Add, stat.Multiply, value, cost, 1024);
                    break;
            }
        }

        return total;
    }

    /// <summary>
    /// Skills an item carries as staff mods count once more on top of their stat price, weighted by
    /// twice the skill level less one, but only on the types that can roll staff mods.
    /// </summary>
    private static int StaffModCost(Item item, ItemPriceTable table, ItemTypePrice type, int cost, int skillDivisor)
    {
        if (type?.StaffMods == null)
        {
            return 0;
        }

        var total = 0;
        foreach (var property in item.Properties.Values)
        {
            if (table.Stat(property.StatId)?.Encode != 1 || table.Skill((int)property.Skill) is not { } skill)
            {
                continue;
            }

            var weight = 2 * property.Value - 1;
            total += cost > 65535 && skill.CostMultiply != 0
                ? weight * (skill.CostAdd + cost * (skill.CostMultiply / skillDivisor))
                : weight * (skill.CostAdd + skill.CostMultiply * cost / skillDivisor);
        }

        return total;
    }

    /// <summary>
    /// Every stat the item's packet carried, with the ones the packet packs together split back into
    /// their own itemstatcost rows: enhanced damage is two stats, and cold and poison damage three
    /// each with their length.
    /// </summary>
    private static IEnumerable<(int StatId, int Value, ItemProperty Property)> PricedStats(Item item)
    {
        foreach (var property in item.Properties.Values)
        {
            switch (property.Type)
            {
                case StatType.AllSkills:
                    // The parser adds it for convenience when all class skills are present; it is not a stat of its own.
                    break;
                case StatType.EnhancedMaximumDamage:
                case StatType.EnhancedMinimumDamage:
                case StatType.MinimumFireDamage:
                case StatType.MinimumLightningDamage:
                case StatType.MinimumMagicalDamage:
                    yield return (property.StatId, property.MinimumValue, property);
                    yield return (property.StatId + 1, property.MaximumValue, property);
                    break;
                case StatType.MinimumColdDamage:
                case StatType.MinimumPoisonDamage:
                    yield return (property.StatId, property.MinimumValue, property);
                    yield return (property.StatId + 1, property.MaximumValue, property);
                    yield return (property.StatId + 2, property.Length, property);
                    break;
                default:
                    yield return (property.StatId, property.PerLevel != 0 ? property.PerLevel : property.Value, property);
                    break;
            }
        }
    }

    private static int Term(int add, int multiply, int value, int cost, int divisor)
        => value * cost > 65535 && multiply != 0
            ? add + multiply * (value * cost / divisor)
            : add + value * cost * multiply / divisor;

    private static int Scale(int value, int multiply)
        => value <= 65535 ? value * multiply / 1024 : value / 1024 * multiply;

    private static BookPrice Book(Item item, ItemPriceTable table)
        => table.Books.FirstOrDefault(book => book.BookCode == item.Type);

    private static bool IsRepairable(Item item, BasePrice basePrice, ItemTypePrice type)
        => item.IsIdentified && !item.Ethereal && type?.Repair == true
            && ((type.Throwable && basePrice.Stackable)
                || (!basePrice.NoDurability && basePrice.Durability > 0 && item.MaximumDurability > 0 && !item.IsIndestructible));
}
