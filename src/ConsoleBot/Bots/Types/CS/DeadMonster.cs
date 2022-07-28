using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using System.Collections.Generic;

namespace ConsoleBot.Bots.Types.CS
{
    public class DeadMonster
    {
        public uint Id { get; set; }
        public Point Location { get; set; }
        public HashSet<MonsterEnchantment> MonsterEnchantments { get; set; } = new HashSet<MonsterEnchantment>();
    }
}
