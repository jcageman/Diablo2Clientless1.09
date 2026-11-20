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
    /// <summary>Under Iron Maiden now, or seen under it in the last few seconds.</summary>
    bool IsUnderIronMaiden(Client client);

    /// <param name="focus">Fight around this point instead of the player, for closing on a boss the pack has left alone.</param>
    /// <param name="wallAwareWhirl">The barbarian whirls only along walkable lines and walks around walls, kills the target on its side of a wall first and drinks one mana potion at a time. Travincal always asks for it; the other bots never were measured with it, and it skips the curse-caster and boss order the Chaos Sanctuary relies on.</param>
    /// <param name="targetId">With <paramref name="wallAwareWhirl"/>, the monster the caller is going for, so both act on the same one.</param>
    /// <param name="keepDistance">
    /// For a walker that must not close in: no move of its own - an approach, a reposition - ends nearer a
    /// monster than this. Null leaves the class's own behaviour as it was.
    /// </param>
    /// <param name="whirlOut">The barbarian's approach to a target out of whirl reach goes through <see cref="WhirlOutOfPack"/>. Only the cow bot asks for it.</param>
    /// <param name="cancellation">Ends the assist's walks, however deep: a bot whose game is over has no use for them.</param>
    Task<bool> AssistPlayer(Client client, Player player, IReadOnlyCollection<NPCCode> priorityCodes = null, Point focus = null, Point standAt = null, bool wallAwareWhirl = false, uint? targetId = null, double? keepDistance = null, bool whirlOut = false, System.Threading.CancellationToken cancellation = default);

    /// <summary>
    /// For a whirlwind barbarian about to walk to <paramref name="destination"/>: whirls the first
    /// <see cref="WhirlOut.WhirlLength"/> of the way instead when a pack stands in it or the server held his
    /// last walk. True when he whirled; otherwise nothing was done and the caller walks.
    /// </summary>
    Task<bool> WhirlOutOfPack(Client client, Point destination, bool forAttacks = false);

    /// <summary>Remembers the walk that just ended from <paramref name="from"/>, so <see cref="WhirlOutOfPack"/> sees it when the server held it.</summary>
    void RecordWalk(Client client, Point from);

    /// <summary>A straight walkable line from him, or a walking path at most half again as long; the pathed answer is kept for a second.</summary>
    Task<bool> IsOnMySide(Client client, WorldObject monster);

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

    /// <summary>The spot <see cref="MoveToNearbySafeSpot"/> would go to, without going: null when there is none.</summary>
    Task<Point> FindNearbySafeSpot(Client client, List<Point> enemies, Point toLocation, double minDistance = 0, double maxdistance = 30);
}
