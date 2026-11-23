using System;
using System.Collections.Generic;

namespace D2NG.Mule.Models;

/// <summary>
/// What a character the bots play wears and is made of, the last time a bot or the mule manager
/// was in a game with it. The upgrade finder judges mule items against it.
/// </summary>
public class GearCharacterDb
{
    public string Id { get; set; }
    public string AccountName { get; set; }
    public string CharacterName { get; set; }

    /// <summary>The <c>CharacterClass</c> name.</summary>
    public string Class { get; set; }

    public int Level { get; set; }

    /// <summary>The game mode the character reported at login.</summary>
    public bool Expansion { get; set; }

    public DateTimeOffset SeenAt { get; set; }

    public int Strength { get; set; }
    public int Dexterity { get; set; }
    public int Vitality { get; set; }
    public int Energy { get; set; }

    /// <summary>Maximum life from level, vitality and quests, without items or buffs.</summary>
    public int BaseLife { get; set; }

    public int BaseMana { get; set; }

    /// <summary>
    /// Stats the character has on its own, keyed by <c>StatType</c> name: the resists Anya's
    /// scrolls grant live here. Empty when the server sent none.
    /// </summary>
    public Dictionary<string, int> BaseStats { get; set; } = [];

    /// <summary>Points put into each skill, keyed by <c>Skill</c> name, without item bonuses.</summary>
    public Dictionary<string, int> HardSkills { get; set; } = [];

    /// <summary>Worn items keyed by <see cref="GearSlots"/> name.</summary>
    public Dictionary<string, MuleItemDb> Equipped { get; set; } = [];

    /// <summary>Charms carried in the inventory, which count like worn items.</summary>
    public List<MuleItemDb> Charms { get; set; } = [];
}
