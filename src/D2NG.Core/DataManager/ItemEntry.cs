using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.DataManager;

internal class ItemEntry
{
    public ItemName Name;
    public string Type;
    public ClassificationType Classification;
    public ushort Width, Height;
    public bool Stackable, Usable, Throwable;

    public ItemEntry(ItemName name, string type, ClassificationType classification, ushort width, ushort height, bool stackable, bool usable, bool throwable)
    {
        Name = name;
        Type = type;
        Classification = classification;
        Width = width;
        Height = height;
        Stackable = stackable;
        Usable = usable;
        Throwable = throwable;
    }

    public bool IsArmor()
    {
        switch (Classification)
        {
            case ClassificationType.Helm:
            case ClassificationType.Armor:
            case ClassificationType.Shield:
            case ClassificationType.Gloves:
            case ClassificationType.Boots:
            case ClassificationType.Belt:
            case ClassificationType.DruidPelt:
            case ClassificationType.BarbarianHelm:
            case ClassificationType.PaladinShield:
            case ClassificationType.NecromancerShrunkenHead:
            case ClassificationType.Circlet:
                return true;
            default:
                return false;
        }
    }

    public bool IsWeapon()
    {
        switch (Classification)
        {
            case ClassificationType.AmazonBow:
            case ClassificationType.AmazonJavelin:
            case ClassificationType.AmazonSpear:
            case ClassificationType.AssassinKatar:
            case ClassificationType.Axe:
            case ClassificationType.Bow:
            case ClassificationType.Club:
            case ClassificationType.Crossbow:
            case ClassificationType.Dagger:
            case ClassificationType.Hammer:
            case ClassificationType.Javelin:
            case ClassificationType.Mace:
            case ClassificationType.Polearm:
            case ClassificationType.Scepter:
            case ClassificationType.SorceressOrb:
            case ClassificationType.Spear:
            case ClassificationType.Sword:
            case ClassificationType.Staff:
            case ClassificationType.ThrowingAxe:
            case ClassificationType.ThrowingKnife:
            case ClassificationType.ThrowingPotion:
            case ClassificationType.Wand:
                return true;
            default:
                return false;
        }
    }

    public uint GetBeltRows()
    {
        if(Classification != ClassificationType.Belt)
        {
            return 0;
        }

        switch (Name)
        {
            case ItemName.Sash:
            case ItemName.LightBelt:
                return 2;
            case ItemName.Belt:
            case ItemName.HeavyBelt:
                return 3;
            default:
                return 4;
        }
    }
}