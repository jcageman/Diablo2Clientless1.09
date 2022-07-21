using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
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
                var nearbyPlayers = client.Game.Players.Where(p => p.Area == client.Game.Me.Area && p.Location?.Distance(client.Game.Me.Location) < 10);
                var anyPlayerMissingShouts = AnyPlayerIsMissingShouts(client);
                if (anyPlayerMissingShouts)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleCommand, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                    client.Game.UseRightHandSkillOnLocation(Skill.Shout, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                }

                return !AnyPlayerIsMissingShouts(client);
            }

            return true;
        }

        public static bool AnyPlayerIsMissingShouts(Client client)
        {
            return client.Game.Players.Any(p => IsMissingShouts(p));
        }

        public static bool AnyClientIsMissingShouts(List<Client> clients)
        {
            return clients.Any(c => c.Game == null || c.Game.Me == null || IsMissingShouts(c.Game.Me));
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
    }
}
