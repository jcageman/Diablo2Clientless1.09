using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

public static class ClassHelpers
{
    public static async Task<bool> CastAllShouts(Client client)
    {
        if (client != null && client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) > 0)
        {
            if (client.Game.Players.Any(p => NeedsShoutFrom(client.Game.Me, p)))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.BattleCommand, client.Game.Me.Location);
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                client.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, client.Game.Me.Location);
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                client.Game.UseRightHandSkillOnLocation(Skill.Shout, client.Game.Me.Location);
                await Task.Delay(TimeSpan.FromSeconds(0.5));
            }

            return client.Game.Players.Any(p => NeedsShoutFrom(client.Game.Me, p));
        }

        return true;
    }

    /// <summary>
    /// A player close enough for this barbarian's shouts to reach who is still missing them. Judged by
    /// position only: a player's area lags its transitions, and a stale one kept a player five units
    /// away from ever being shouted while the barbarian waited for him.
    /// </summary>
    public static bool NeedsShoutFrom(Player barbarian, Player player) =>
        player.Location != null
        && player.Location.Distance(barbarian.Location) < ShoutReach
        && IsMissingShouts(player);

    private const double ShoutReach = 10;

    /// <summary>
    /// The server holds both states on a barbarian for exactly the length of a whirl; it ignores a potion
    /// drunk while they are on (stock unchanged, life still falling) and takes one the moment they clear.
    /// </summary>
    public static bool IsWhirling(Player player) =>
        player.Effects.ContainsKey(EntityEffect.Skillmove) && player.Effects.ContainsKey(EntityEffect.Uninterruptable);

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

    private static readonly TimeSpan FindItemRecastInterval = TimeSpan.FromMilliseconds(700);

    public static async Task<bool> FindItemOnDeadEnemy(Game game, IPathingService pathingService, IMapApiService mapApiService, WorldObject monster)
    {
        if(!game.Me.HasSkill(Skill.FindItem) || monster.NPCCode == NPCCode.Diablo)
        {
            return true;
        }

        var lastCast = DateTime.MinValue;
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
                await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, monster, MovementMode.Walking);
            }

            if (DateTime.Now.Subtract(lastCast) < FindItemRecastInterval)
            {
                return false;
            }

            await game.MoveToAsync(monster.Location);
            game.UseFindItem(monster);
            lastCast = DateTime.Now;

            return monster.Effects.Contains(EntityEffect.CorpseNoDraw);

        }, TimeSpan.FromSeconds(5));
    }
}
