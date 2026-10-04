using D2NG.Core.D2GS.Enums;

namespace D2NG.Core.ItemData;

/// <summary>
/// One row of itemtypes. Types form a tree through the two equivalence columns, and an affix that
/// names a type rolls on everything below it: "armo" covers helms, boots and body armour alike.
/// </summary>
public sealed class ItemTypeEntry
{
    public int Index { get; set; }
    public string Code { get; set; }
    public string Equivalent1 { get; set; }
    public string Equivalent2 { get; set; }
    public bool AlwaysMagic { get; set; }
    public bool CanBeRare { get; set; }
    public bool AlwaysNormal { get; set; }
    public bool Charm { get; set; }
    public int MaxSockets { get; set; }

    /// <summary>The class whose skills a magic or rare item of this type can get as staff mods.</summary>
    public CharacterClass? StaffMods { get; set; }

    /// <summary>The class a class-specific type belongs to, such as orbs for the sorceress.</summary>
    public CharacterClass? Class { get; set; }
}
