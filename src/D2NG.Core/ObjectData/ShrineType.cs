namespace D2NG.Core.ObjectData;

/// <summary>
/// The shrine functions of shrines.txt, by their Code column. A <see cref="ShrineKind.Random"/>
/// shrine object becomes one of these when the level is populated, weighted by the table's rarity
/// column (1 common, 3 rare). Fixed health and mana shrine objects are always <see cref="Refill"/>
/// respectively <see cref="ManaBoost"/> flavoured, without a roll.
/// </summary>
/// <remarks>
/// Identical in 1.09d and 1.13c. Kept as an enum rather than generated data because the codes are
/// what the game hardcodes its behaviour on; a realm patch that changed them would need code changes
/// anyway.
/// </remarks>
public enum ShrineType : byte
{
    None = 0,
    Refill = 1,
    HealthBoost = 2,
    ManaBoost = 3,
    HealthExchange = 4,
    ManaExchange = 5,
    ArmorBoost = 6,
    CombatBoost = 7,
    ResistFireBoost = 8,
    ResistColdBoost = 9,
    ResistLightningBoost = 10,
    ResistPoisonBoost = 11,
    SkillBoost = 12,
    RechargeBoost = 13,
    StaminaBoost = 14,
    ExperienceBoost = 15,
    Enirhs = 16,
    PortalToUnknown = 17,
    GemUpgrade = 18,
    Storm = 19,
    Warping = 20,
    Exploding = 21,
    Poison = 22
}
