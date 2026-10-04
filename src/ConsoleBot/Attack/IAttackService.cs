using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.MonsterData;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Services.Pathing;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleBot.Attack;

public interface IAttackService
{
    /// <param name="focus">Fight around this point instead of the player, for closing on a boss the pack has left alone.</param>
    Task<bool> AssistPlayer(Client client, Player player, IReadOnlyCollection<NPCCode> priorityCodes = null, Point focus = null, Point standAt = null);

    /// <summary>
    /// Whether this character can actually hurt this monster: it deals at least one kind of damage
    /// the monster is not immune to. Asking beforehand is what keeps a killer from claiming a pack
    /// it can only stand next to - on 1.09 an immunity is absolute, so nothing breaks it.
    /// </summary>
    bool CanHarm(Client client, NPCCode code, IEnumerable<MonsterEnchantment> enchantments);

    bool IsImmuneTo(Client client, WorldObject monster, ResistType type);
    Task<bool> IsInLineOfSight(Client client, Point fromLocation, Point toLocation);
    Task<bool> IsInLineOfSight(Client client, Point toLocation);
    Task<bool> IsVisitable(Client client, Point point);
    Task<bool> MoveToNearbySafeSpot(Client client, List<Point> enemies, Point toLocation, MovementMode movementMode, double minDistance = 0, double maxDistance = 30);
}
