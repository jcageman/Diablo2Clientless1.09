namespace D2NG.Core.D2GS.Objects;

public enum EntityCode : ushort
{
    TownPortal = 59,
    RedTownPortal = 60,
    TristamPortal = 61,
    WaypointAct1 = 119,
    WaypointAct1JailAndUp = 157,
    WaypointAct2 = 156,
    WaypointAct2Sewer = 323,
    WaypointAct2Cellar = 288,
    WaypointAct2Arcane = 402,
    WaypointAct3 = 237,
    WaypointAct3Durance = 324,
    WaypointAct4Levels = 238,
    DurielsLairPortal = 100,
    ViperAmuletAltar = 149,
    HoradricOrifice = 152,
    DiabloStar = 255,
    Stash = 267,
    WirtsBody = 268,
    ArcanePortal = 298,
    /// <summary>Horazon's journal in the Arcane Sanctuary; reading it opens the red portal to the Canyon of the Magi.</summary>
    HorazonsJournal = 357,

    /// <summary>
    /// Portal from the durance of hate to the pandemonium fortress, the act 3 to 4 transition. Taken
    /// as a plain object interaction: unlike Warriv and Meshif there is no dialog involved.
    /// </summary>
    PandemoniumFortressPortal = 342,

    /// <summary>The Horadric Malus on its stand in the Barracks; operated, it drops the malus.</summary>
    HoradricMalusStand = 108,

    /// <summary>Lam Esen's tome on its stand in the Ruined Temple; operated, it drops the tome.</summary>
    LamEsensTomeStand = 193,

    HoradricCubeChest = 354,

    /// <summary>The Horadric scroll chest in the Sewers level 3, where Radament stands guard.</summary>
    HoradricScrollChest = 355,

    /// <summary>The preset spot in the Frozen River where frozen Anya is put once the quest is under way.</summary>
    AnyaStartOutsideTown = 460,

    /// <summary>Frozen Anya; operated by a player carrying Malah's potion, she is freed.</summary>
    FrozenAnya = 558,

    /// <summary>The altar on Arreat Summit that wakes the Ancients.</summary>
    AncientsAltar = 546,
    StaffOfKingsChest = 356,
    Hratli = 379,
    RightSeal1 = 392,
    RightSeal2 = 393,
    TopSeal = 394,
    LeftSeal1 = 395,
    LeftSeal2 = 396,
    WaypointAct4 = 398,
    CompellingOrb = 404,
    WaypointAct5 = 429,
    WaypointAct5Glacial = 511,
    WaypointAct5Baal = 494,
    /// <summary>The waypoint of act 5's outdoor levels: Frigid Highlands, Arreat Plateau, Frozen Tundra, Halls of Pain.</summary>
    WaypointAct5Wilderness = 496,
    BaalPortal = 563,
    LastLastPortal = 566

}
