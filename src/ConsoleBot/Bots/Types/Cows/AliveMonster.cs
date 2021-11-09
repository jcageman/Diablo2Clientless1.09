using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;

namespace ConsoleBot.Bots.Types.Cows
{
    public class AliveMonster
    {
        public uint Id { get; set; }
        public Point Location { get; set; }
        public double LifePercentage { get; set; } = 100;

        public NPCCode NPCCode { get; set; }
        public HashSet<MonsterEnchantment> MonsterEnchantments { get; set; } = new HashSet<MonsterEnchantment>();
}
}
