using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers
{
    public static class ClassHelpers
    {
        public static async Task<bool> CastAllShouts(Client client)
        {
            if (client != null && client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) > 0)
            {
                var nearbyPlayers = client.Game.Players.Where(p => p.Location?.Distance(client.Game.Me.Location) < 10);
                var anyPlayerMissingBc= nearbyPlayers.Any(p => !p.Effects.Contains(EntityEffect.Battlecommand));
                var anyPlayerMissingBo = nearbyPlayers.Any(p => !p.Effects.Contains(EntityEffect.BattleOrders));
                var anyPlayerMissingShout = nearbyPlayers.Any(p => !p.Effects.Contains(EntityEffect.Shout));
                if (anyPlayerMissingBc)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleCommand, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                }

                if (anyPlayerMissingBo)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                }

                if (anyPlayerMissingShout)
                {
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

        public static bool IsMissingShouts(Player p)
        {
            return !p.Effects.Contains(EntityEffect.Battlecommand)
                                            || !p.Effects.Contains(EntityEffect.BattleOrders)
                                            || !p.Effects.Contains(EntityEffect.Shout);
        }
    }
}
