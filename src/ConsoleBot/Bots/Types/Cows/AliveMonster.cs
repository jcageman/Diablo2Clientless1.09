using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;

namespace ConsoleBot.Bots.Types.Cows;

public class AliveMonster
{
    public uint Id { get; set; }
    public Point Location { get; set; }
    public double LifePercentage { get; set; } = 100;

    public NPCCode NPCCode { get; set; }
    public HashSet<MonsterEnchantment> MonsterEnchantments { get; set; } = [];

    /// <summary>Whether this is one of the monsters the party hunts in active mode.</summary>
    public bool IsHunted { get; set; }

    /// <summary>The cluster this monster was first seen in. It keeps that cluster open until it dies.</summary>
    public uint ClusterId { get; set; }
}
