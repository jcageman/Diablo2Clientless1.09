using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace D2NG.Mule;

/// <summary>The ten places an item is worn, named as the realm's armory names them.</summary>
public static class GearSlots
{
    public const string Head = "head";
    public const string Amulet = "amulet";
    public const string Body = "body";
    public const string RightHand = "right hand";
    public const string LeftHand = "left hand";
    public const string RightRing = "right ring";
    public const string LeftRing = "left ring";
    public const string Belt = "belt";
    public const string Boots = "boots";
    public const string Gloves = "gloves";

    /// <summary>Not a worn slot: the charms carried in the inventory, each swapped for one of the same size.</summary>
    public const string Charms = "charms";

    public static IReadOnlyList<string> All { get; } = [Head, Amulet, Body, RightHand, LeftHand, RightRing, LeftRing, Belt, Boots, Gloves];

    /// <summary>The slot an item on the first weapon set is worn in; null for the switch set and anything not worn.</summary>
    public static string Of(DirectoryType directory) => directory switch
    {
        DirectoryType.Helm => Head,
        DirectoryType.Amulet => Amulet,
        DirectoryType.Armor => Body,
        DirectoryType.RightHand => RightHand,
        DirectoryType.LeftHand => LeftHand,
        DirectoryType.RightHandRing => RightRing,
        DirectoryType.LeftHandRing => LeftRing,
        DirectoryType.Belt => Belt,
        DirectoryType.Boots => Boots,
        DirectoryType.Gloves => Gloves,
        _ => null,
    };

    private static readonly HashSet<ClassificationType> Weapons =
    [
        ClassificationType.AmazonBow, ClassificationType.AmazonJavelin, ClassificationType.AmazonSpear, ClassificationType.AssassinKatar,
        ClassificationType.Axe, ClassificationType.Bow, ClassificationType.Club, ClassificationType.Crossbow, ClassificationType.Dagger,
        ClassificationType.Hammer, ClassificationType.Javelin, ClassificationType.Mace, ClassificationType.Polearm, ClassificationType.Scepter,
        ClassificationType.SorceressOrb, ClassificationType.Spear, ClassificationType.Staff, ClassificationType.Sword,
        ClassificationType.ThrowingAxe, ClassificationType.ThrowingKnife, ClassificationType.Wand,
    ];

    private static readonly HashSet<ClassificationType> Shields =
        [ClassificationType.Shield, ClassificationType.PaladinShield, ClassificationType.NecromancerShrunkenHead];

    /// <summary>Whether an item of this classification can go in the slot at all; class and hand rules come later.</summary>
    public static bool Fits(string slot, ClassificationType type) => slot switch
    {
        Head => type is ClassificationType.Helm or ClassificationType.Circlet or ClassificationType.BarbarianHelm or ClassificationType.DruidPelt,
        Amulet => type == ClassificationType.Amulet,
        Body => type == ClassificationType.Armor,
        RightHand => Weapons.Contains(type),
        LeftHand => Shields.Contains(type) || Weapons.Contains(type) || type is ClassificationType.Arrows or ClassificationType.Bolts,
        RightRing or LeftRing => type == ClassificationType.Ring,
        Belt => type == ClassificationType.Belt,
        Boots => type == ClassificationType.Boots,
        Gloves => type == ClassificationType.Gloves,
        Charms => type is ClassificationType.SmallCharm or ClassificationType.LargeCharm or ClassificationType.GrandCharm,
        _ => false,
    };

    public static bool IsWeapon(ClassificationType type) => Weapons.Contains(type);

    public static bool IsShield(ClassificationType type) => Shields.Contains(type);

    public static bool IsQuiver(ClassificationType type) => type is ClassificationType.Arrows or ClassificationType.Bolts;
}
