using D2NG.Core.D2GS.Players;
using D2NG.Core.SkillData;

namespace D2NG.Leveling.Plans;

public sealed class SkillCatalog
{
    public const int MaxHardPoints = 20;

    private readonly Dictionary<Skill, SkillEntry> _skills;

    public SkillCatalog(SkillTable table)
        => _skills = table.Skills.ToDictionary(skill => (Skill)skill.Id);

    public static SkillCatalog Load(string path) => new(SkillTable.Load(path));

    public bool TryGet(Skill skill, out SkillEntry entry) => _skills.TryGetValue(skill, out entry!);

    public SkillEntry this[Skill skill] => _skills[skill];

    /// <summary>Each hard point past the first needs one more character level than the skill's requirement.</summary>
    public int MaxHardPointsAt(Skill skill, int characterLevel)
        => Math.Clamp(characterLevel - this[skill].RequiredLevel + 1, 0, MaxHardPoints);

    public IEnumerable<Skill> Prerequisites(Skill skill) => this[skill].Prerequisites.Select(id => (Skill)id);
}
