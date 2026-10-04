namespace D2NG.Core.ObjectData;

/// <summary>
/// What a shrine object turns into when the level is populated. Read from the Parm0 column of the
/// object's row in objects.txt, which the game's shrine init function switches on.
/// </summary>
/// <remarks>
/// The object name is not a reliable guide: rows called plain "Shrine" include fixed health shrines
/// (for example 84 in the Act 1 wilderness and 206 in Kurast) and fixed mana shrines (164 to 168 in
/// the Act 2 tombs). Only the Parm0 value tells them apart.
/// </remarks>
public enum ShrineKind
{
    /// <summary>Always a health shrine.</summary>
    Health = 1,

    /// <summary>Always a mana shrine.</summary>
    Mana = 2,

    /// <summary>
    /// Rolled from shrines.txt by rarity when the level is populated, so the kind is only known once
    /// the object is in view. See <see cref="ShrineType"/>.
    /// </summary>
    Random = 3
}
