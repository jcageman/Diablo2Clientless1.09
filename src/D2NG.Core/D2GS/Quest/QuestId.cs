namespace D2NG.Core.D2GS.Quest;

/// <summary>
/// Index into the 48 word quest array carried by the quest packets (0x9C for this character, 0x9D
/// for the game). The array is laid out as six blocks of eight words, one block per act, and each
/// block runs [act intro, quest 1 .. quest 6, act outro].
/// </summary>
/// <remarks>
/// Derived from a live 1.09 capture rather than from documentation. Empirically confirmed:
/// <see cref="Act1Intro"/>, <see cref="DenOfEvil"/>, <see cref="SistersToTheSlaughter"/> and
/// <see cref="Act1Outro"/> - killing Andariel moved word 6 from 0x201A to 0x2019 and word 7 to
/// 0x2001 when act 2 travel opened. The remaining entries follow the same block layout and were
/// cross checked against a second character whose words 8-15 read as a consistent act 2
/// progression, but each one still needs its own capture before code depends on it.
/// </remarks>
public enum QuestId
{
    Act1Intro = 0,
    DenOfEvil = 1,
    SistersBurialGrounds = 2,
    ToolsOfTheTrade = 3,
    TheSearchForCain = 4,
    TheForgottenTower = 5,
    SistersToTheSlaughter = 6,
    Act1Outro = 7,

    Act2Intro = 8,
    RadamentsLair = 9,
    TheHoradricStaff = 10,
    TaintedSun = 11,
    ArcaneSanctuary = 12,
    TheSummoner = 13,
    TheSevenTombs = 14,
    Act2Outro = 15,

    Act3Intro = 16,
    LamEsensTome = 17,
    KhalimsWill = 18,
    BladeOfTheOldReligion = 19,
    TheGoldenBird = 20,
    TheBlackenedTemple = 21,
    TheGuardian = 22,
    Act3Outro = 23,

    Act4Intro = 24,
    TheFallenAngel = 25,
    TerrorsEnd = 26,
    HellsForge = 27,
    Act4Outro = 28,

    Act5Intro = 32,
    SiegeOnHarrogath = 33,
    RescueOnMountArreat = 34,
    PrisonOfIce = 35,
    BetrayalOfHarrogath = 36,
    RiteOfPassage = 37,
    EveOfDestruction = 38,
    Act5Outro = 39
}
