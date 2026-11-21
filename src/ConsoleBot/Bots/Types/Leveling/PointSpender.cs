using ConsoleBot.Helpers;
using D2NG.Leveling.Plans;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>Spends a member's free skill and stat points the way its plan says, one confirmed point at a time.</summary>
public static class PointSpender
{
    private static readonly TimeSpan Confirmation = TimeSpan.FromSeconds(2);

    /// <summary>The stats the gear the party holds for a member asks for (<see cref="LevelingGear.StatsWanted"/>); set by the bot.</summary>
    public static Func<PartyMember, List<(Attribute Stat, int Need, D2NG.Core.D2GS.Items.Item Item)>> Wanted { get; set; }

    public static async Task Spend(PartyMember member)
    {
        if (!member.InGame)
        {
            return;
        }

        var me = member.Game.Me;
        while (me.Attributes.GetValueOrDefault(Attribute.SkillRemaining) > 0)
        {
            var skill = SkillAllocator.Next(member.Plan, member.Catalog, member.Level, member.HardSkills());
            if (skill == null)
            {
                break;
            }

            var before = me.Skills.GetValueOrDefault(skill.Value);
            var left = me.Attributes.GetValueOrDefault(Attribute.SkillRemaining);
            member.Game.AddSkillPoint(skill.Value);
            if (!await GeneralHelpers.TryWithTimeout(async _ =>
                {
                    await Task.Delay(50);
                    return me.Skills.GetValueOrDefault(skill.Value) > before;
                }, Confirmation))
            {
                Log.Warning("{Character} spent a point on {Skill} but its level stayed {Before} ({Left} points were left)", member.Name, skill, before, left);
                return;
            }

            Log.Information("{Character} (level {Level}) put a point into {Skill}: now {Points}, {Left} skill points left",
                member.Name, member.Level, skill, me.Skills.GetValueOrDefault(skill.Value), me.Attributes.GetValueOrDefault(Attribute.SkillRemaining));
        }

        var wishes = me.Attributes.GetValueOrDefault(Attribute.StatRemaining) > 0 ? Wanted?.Invoke(member) ?? [] : [];
        var wanted = wishes.GroupBy(wish => wish.Stat).ToDictionary(group => group.Key, group => group.Max(wish => wish.Need));
        while (me.Attributes.GetValueOrDefault(Attribute.StatRemaining) > 0)
        {
            var stat = StatAllocator.Next(member.Plan, member.HardStats(), wanted);
            if (stat == null)
            {
                break;
            }

            if (wanted.TryGetValue(stat.Value, out var need) && me.Attributes.GetValueOrDefault(stat.Value) < need
                && wishes.FirstOrDefault(wish => wish.Stat == stat.Value && wish.Need == need) is { Item: { } item })
            {
                Log.Information("{Character} puts a point into {Stat} for {Quality} {Item}, which needs {Need}", member.Name, stat, item.Quality, item.Name, need);
            }

            var before = me.Attributes.GetValueOrDefault(stat.Value);
            member.Game.AddStatPoint(stat.Value);
            if (!await GeneralHelpers.TryWithTimeout(async _ =>
                {
                    await Task.Delay(50);
                    return me.Attributes.GetValueOrDefault(stat.Value) > before;
                }, Confirmation))
            {
                Log.Warning("{Character} spent a point on {Stat} but it stayed {Before}", member.Name, stat, before);
                return;
            }
        }
    }

    /// <summary>What the character holds that its plan never gives, which 1.09 cannot undo.</summary>
    public static IReadOnlyList<string> Deviations(PartyMember member)
        => PlanConformance.Deviations(member.Plan, member.Catalog, member.HardSkills(), member.HardStats());

    public static string Describe(PartyMember member)
    {
        var stats = member.HardStats();
        return $"{member.Name} level {member.Level}: "
            + string.Join(", ", member.HardSkills().OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}"))
            + $" | str {stats[Attribute.Strength]} dex {stats[Attribute.Dexterity]} vit {stats[Attribute.Vitality]} ene {stats[Attribute.Energy]}"
            + $" | wears {string.Join(", ", member.Game.Me.Equipment.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value.Quality} {pair.Value.Name}"))}";
    }
}
