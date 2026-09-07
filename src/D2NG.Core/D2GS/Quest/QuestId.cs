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
///
/// <see cref="EveOfDestruction"/> is measured too, and it broke the block layout. Killing Baal moved
/// word <b>40</b> from 0x0000 to 0x1401 on the rushee, and a character that had already finished act 5
/// read 0x1451 in the same word; word 38, where the layout puts the quest, never moved on either. So
/// act 5 begins two words later than the pattern predicts. Acts 1 to 4 were re-checked against the
/// same capture and are where this enum says they are.
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

    // Act 5 does not sit where the eight word block layout predicts: it starts two words later.
    // EveOfDestruction is the measured one - see the remarks above - and the rest of the block is
    // placed by following the act 5 quest order back from it, so they still need their own capture.
    Act5Intro = 34,
    SiegeOnHarrogath = 35,
    RescueOnMountArreat = 36,
    PrisonOfIce = 37,
    BetrayalOfHarrogath = 38,
    RiteOfPassage = 39,
    EveOfDestruction = 40,
    Act5Outro = 41
}
