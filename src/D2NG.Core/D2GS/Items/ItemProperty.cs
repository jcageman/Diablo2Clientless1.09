using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;

namespace D2NG.Core.D2GS.Items
{
    public class ItemProperty
    {
        public StatType Type { get; set; }

        public int Value { get; set; }

        public int MonsterReanimate { get; set; }

        public CharacterClass CharacterClass { get; set; }

        public Skill Skill { get; set; }

        public int SkillLevel { get; set; }

        public int SkillChance { get; set; }

        public int SkillTab { get; set; }

        public int Charges { get; set; }

        public int MaximumCharges { get; set; }

        public int PerLevel { get; set; }

        public int MinimumValue { get; set; }

        public int MaximumValue { get; set; }
    }
}
