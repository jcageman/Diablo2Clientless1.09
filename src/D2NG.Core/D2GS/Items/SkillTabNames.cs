namespace D2NG.Core.D2GS.Items;

/// <summary>
/// The skill tab names the game shows on an item, with the class the tab belongs to.
/// </summary>
public static class SkillTabNames
{
    public static string Name(SkillTab tab) => tab switch
    {
        SkillTab.AmazonBowAndCrossbowSkills => "Bow and Crossbow Skills (Amazon)",
        SkillTab.AmazonPassiveAndMagicSkills => "Passive and Magic Skills (Amazon)",
        SkillTab.AmazonJavelinAndSpearSkills => "Javelin and Spear Skills (Amazon)",
        SkillTab.SorceressFireSpells => "Fire Skills (Sorceress)",
        SkillTab.SorceressLightningSpells => "Lightning Skills (Sorceress)",
        SkillTab.SorceressColdSpells => "Cold Skills (Sorceress)",
        SkillTab.NecromancerCurses => "Curses (Necromancer)",
        SkillTab.NecromancerPoisonAndBoneSpells => "Poison and Bone Skills (Necromancer)",
        SkillTab.NecromancerSummoningSpells => "Summoning Skills (Necromancer)",
        SkillTab.PaladinCombatSkills => "Combat Skills (Paladin)",
        SkillTab.PaladinOffensiveAuras => "Offensive Auras (Paladin)",
        SkillTab.PaladinDefensiveAuras => "Defensive Auras (Paladin)",
        SkillTab.BarbarianCombatSkills => "Combat Skills (Barbarian)",
        SkillTab.BarbarianCombatMasteries => "Masteries (Barbarian)",
        SkillTab.BarbarianWarcries => "Warcries (Barbarian)",
        SkillTab.DruidSummoning => "Summoning Skills (Druid)",
        SkillTab.DruidShapeShifting => "Shape Shifting Skills (Druid)",
        SkillTab.DruidElemental => "Elemental Skills (Druid)",
        SkillTab.AssasinTraps => "Traps (Assassin)",
        SkillTab.AssasinShadowDisciplines => "Shadow Disciplines (Assassin)",
        SkillTab.AssasinMartialArts => "Martial Arts (Assassin)",
        _ => tab.ToString()
    };

    public static bool IsSkillTab(StatType type)
        => type is StatType.SkillTab1 or StatType.SkillTab2 or StatType.SkillTab3
            or StatType.SkillTab4 or StatType.SkillTab5 or StatType.SkillTab6;
}
