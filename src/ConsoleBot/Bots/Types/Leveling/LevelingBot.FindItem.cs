using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using Serilog;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

public sealed partial class LevelingBot
{
    /// <summary>Bodies within this reach of the barbarian are searched.</summary>
    private const double FindItemReach = 25;

    /// <summary>Nothing alive this close to him or the body: the search waits until the fight around it is over.</summary>
    private const int FindItemSafeRadius = 15;

    /// <summary>
    /// The act bosses, whose bodies cannot be searched: Diablo carries the super unique flag, and the log said his body was
    /// searched though the shared helper skips him.
    /// </summary>
    private static readonly HashSet<NPCCode> ActBosses = [NPCCode.Andarial, NPCCode.Duriel, NPCCode.Mephisto, NPCCode.Diablo];

    /// <summary>Bodies already searched this game, by unit id.</summary>
    private readonly ConcurrentDictionary<uint, bool> _searchedBodies = new();

    /// <summary>
    /// A barbarian with Find Item searches the bodies of super uniques once nothing is near: another roll of their drop, as
    /// the Travincal barbarian does on the council. The act bosses' bodies cannot be searched.
    /// </summary>
    private async Task FindItemOnBodies(Party party)
    {
        foreach (var member in party.Members.Where(member => member.InGame && !member.Game.IsInTown() && member.Game.Me.Life > 0
            && member.Game.Me.HasSkill(Skill.FindItem)))
        {
            var game = member.Game;
            var bodies = game.WorldObjects.Where(pair => pair.Key.Item2 == EntityType.NPC).Select(pair => pair.Value)
                .Where(npc => npc.State == EntityState.Dead && npc.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique) && !ActBosses.Contains(npc.NPCCode)
                    && !npc.Effects.Contains(EntityEffect.CorpseNoDraw) && !_searchedBodies.ContainsKey(npc.Id)
                    && npc.Location.Distance(game.Me.Location) <= FindItemReach)
                .OrderBy(npc => npc.Location.Distance(game.Me.Location))
                .ToList();
            foreach (var body in bodies)
            {
                if (NPCHelpers.GetNearbyNPCs(member.Client, game.Me.Location, 1, FindItemSafeRadius).Any()
                    || NPCHelpers.GetNearbyNPCs(member.Client, body.Location, 1, FindItemSafeRadius).Any())
                {
                    break;
                }

                _searchedBodies[body.Id] = true;
                var mode = member.Mode;
                member.Mode = FollowMode.Off;
                try
                {
                    var found = await ClassHelpers.FindItemOnDeadEnemy(game, pathingService, mapApiService, body);
                    Log.Information("{Character} {Outcome} the body of {Monster} with Find Item", member.Name, found ? "searched" : "could NOT search", body.NPCCode);
                }
                finally
                {
                    member.Mode = mode;
                }
            }
        }
    }
}
