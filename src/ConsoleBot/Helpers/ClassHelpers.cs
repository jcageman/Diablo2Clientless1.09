using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Services.Pathing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers
{
    public static class ClassHelpers
    {
        public static async Task<bool> CastAllShouts(Client client)
        {
            if (client != null && client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) > 0)
            {
                var nearbyPlayers = client.Game.Players.Where(p => p.Area == client.Game.Area && p.Location?.Distance(client.Game.Me.Location) < 10 && IsMissingShouts(p));
                if (nearbyPlayers.Any())
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleCommand, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                    client.Game.UseRightHandSkillOnLocation(Skill.Shout, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                }

                return client.Game.Players.Where(p => p.Area == client.Game.Area && p.Location?.Distance(client.Game.Me.Location) < 10 && IsMissingShouts(p)).Any();
            }

            return true;
        }

        public static bool AnyPlayerIsMissingShouts(Client client)
        {
            return client.Game.Players.Any(p => IsMissingShouts(p));
        }

        public static bool IsMissingShouts(Player p)
        {
            return !p.Effects.ContainsKey(EntityEffect.Battlecommand)
                                            || !p.Effects.ContainsKey(EntityEffect.BattleOrders)
                                            || !p.Effects.ContainsKey(EntityEffect.Shout);
        }

        public static bool CanStaticEntity(Client client, double LifePercentage)
        {
            return !client.Game.ClientCharacter.IsExpansion || LifePercentage > 60;
        }

        public static async Task<bool> FindItemOnDeadEnemy(Game game, IPathingService pathingService, WorldObject monster)
        {
            if(!game.Me.HasSkill(Skill.FindItem))
            {
                return true;
            }

            return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (!game.IsInGame() || monster.State == EntityState.Dieing)
                {
                    return false;
                }

                if (monster.Effects.Contains(EntityEffect.CorpseNoDraw))
                {
                    return true;
                }

                if (monster.Location.Distance(game.Me.Location) > 5)
                {
                    await MovementHelpers.MoveToWorldObject(game, pathingService, monster, MovementMode.Walking);
                }

                await game.MoveToAsync(monster.Location);
                game.UseFindItem(monster);

                return monster.Effects.Contains(EntityEffect.CorpseNoDraw);

            }, TimeSpan.FromSeconds(5));
        }
    }
}
