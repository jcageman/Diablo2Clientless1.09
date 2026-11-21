using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Generic;

namespace D2NG.Core.MonsterData;

/// <summary>
/// Units that are not monsters to fight: town folk, hirelings, summons, traps and the placeholder
/// classes monstats.bin names "dummy". The bot's own list (ConsoleBot NPCHelpers) decides what to
/// attack; this one only separates them from monsters when measuring.
/// </summary>
public static class FriendlyUnits
{
    public static IReadOnlySet<NPCCode> Classes { get; } = new HashSet<NPCCode>
    {
        NPCCode.DeckardCainQuest, NPCCode.Gheed, NPCCode.Akara, NPCCode.Kashya, NPCCode.Act1Npc, NPCCode.Charsi, NPCCode.WarrivAct1,
        NPCCode.WarrivAct2, NPCCode.Atma, NPCCode.Drognan, NPCCode.Fara, NPCCode.Greiz, NPCCode.Elzix, NPCCode.Geglash, NPCCode.Jerhyn,
        NPCCode.Lysander, NPCCode.Meshif, NPCCode.DeckardCainAct2, NPCCode.DeckardCainAct3, NPCCode.DeckardCainAct4, NPCCode.TyraelAct3,
        NPCCode.Asheara, NPCCode.Hratli, NPCCode.Alkor, NPCCode.Ormus, NPCCode.Halbu, NPCCode.Meshif2, NPCCode.DeckardCainAct1, NPCCode.Navi,
        NPCCode.RogueScout, NPCCode.Natalya, NPCCode.Guard, NPCCode.IronWolf, NPCCode.TyraelAct4, NPCCode.JamellaAct4, NPCCode.IzualAct4,
        NPCCode.Larzuk, NPCCode.Anya, NPCCode.Malah, NPCCode.Nihlathak, NPCCode.QualKehk, NPCCode.DeckardCainAct5, NPCCode.TyraelAct5,
        NPCCode.Hireling1, NPCCode.Hireling2,
        NPCCode.ClayGolem, NPCCode.BloodGolem, NPCCode.FireGolem, NPCCode.IronGolem, NPCCode.Valkyrie, NPCCode.Hydra1, NPCCode.Hydra2,
        NPCCode.Hydra3, NPCCode.CompellingOrb, NPCCode.MephistoGhost,
        NPCCode.ATrap1, NPCCode.ATrap2, NPCCode.ATrap3, NPCCode.ATrap4, NPCCode.ATrap5, NPCCode.ATrap6, NPCCode.ATrap7,
    };

    /// <summary><paramref name="fileClass"/> is the class name in the monster table, for the classes the enum does not name.</summary>
    public static bool IsFriendly(NPCCode code, string fileClass = null)
        => Classes.Contains(code) || string.Equals(fileClass, "dummy", StringComparison.OrdinalIgnoreCase)
            || (fileClass != null && (fileClass.Contains("Hireling", StringComparison.OrdinalIgnoreCase) || fileClass.Contains("Townguard", StringComparison.OrdinalIgnoreCase)));

    public static bool IsFriendly(NPCCode code, MonsterSpeedTable table)
        => IsFriendly(code, table != null && table.TryGetFileValue(code, out var speed) ? speed.Class : null);
}
