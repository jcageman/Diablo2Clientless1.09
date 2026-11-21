using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Leveling.Exploration;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Moves the party as one: the leader walks the route in short stretches and the party fights whatever
/// comes near between them, while every other member keeps up on its own loop.
/// </summary>
public sealed class Party(
    List<PartyMember> members,
    LevelingCombat combat,
    AreaTraveler traveler,
    IPathingService pathingService,
    IMapApiService mapApiService) : IDisposable
{
    public const double EngageRadius = 15;
    private const double FollowDistance = 5;
    private const double RangedFollowDistance = 9;
    private const double SelfDefenceRadius = 6;
    private const double GatherDistance = 12;
    private const double StrandedDistance = 80;
    private const int StretchPoints = 3;
    private const int MaxStretches = 400;
    private const double ArrivedDistance = 6;
    private const double StretchMissed = 10;

    /// <summary>How near a stop has to be passed to count as seen. Walls cut sight short in caves, so it is modest.</summary>
    private const double SightRadius = 12;

    /// <summary>Every monster any member has seen alive in this area, by id, with where it was last seen.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Point> _aliveSeen = new();

    /// <summary>Notes what the members can see: monsters alive, and monsters that died.</summary>
    public void TrackMonsters()
    {
        foreach (var member in members.Where(member => member.InGame))
        {
            foreach (var npc in member.Game.WorldObjects.Where(entry => entry.Key.Item2 == EntityType.NPC && !NPCHelpers.IsFriendly(entry.Value.NPCCode) && !LevelingCombat.Untargetable.Contains(entry.Value.NPCCode)).Select(entry => entry.Value))
            {
                if (npc.State is EntityState.Dead or EntityState.Dieing)
                {
                    _aliveSeen.TryRemove(npc.Id, out _);
                }
                else
                {
                    _aliveSeen[npc.Id] = npc.Location;
                }
            }
        }
    }

    /// <summary>
    /// Goes back for the monsters seen alive that nobody saw die - they walked out of view, or the party
    /// passed them by - nearest first, until none is left or <paramref name="done"/>.
    /// </summary>
    public async Task HuntRemaining(Func<Task<bool>> done, TimeSpan limit)
    {
        var started = DateTime.UtcNow;
        TrackMonsters();
        while (!_aliveSeen.IsEmpty && DateTime.UtcNow - started < limit && await LeaderPresent() && !RegroupRequested && !StopRequested() && !await done())
        {
            var here = Leader.Game.Me.Location;
            var (id, at) = _aliveSeen.MinBy(entry => entry.Value.Distance(here));
            Log.Information("Hunting a monster last seen at {Location}, {Left} unaccounted for", at, _aliveSeen.Count);
            if (!await WalkTo(at) && Leader.Game.Me.Location.Distance(at) > 10)
            {
                // Out of reach from here - another area, an island the map cannot path to: not worth the step.
                _aliveSeen.TryRemove(id, out _);
                continue;
            }

            await FightHere(EngageRadius);
            TrackMonsters();
            // Standing where it was and not seeing it: it is gone from there, and chasing a ghost costs the step.
            if (Leader.Game.Me.Location.Distance(at) < 10 && !Leader.Game.WorldObjects.Values.Any(entity => entity.Id == id))
            {
                _aliveSeen.TryRemove(id, out _);
            }
        }
    }

    public void ForgetMonsters()
    {
        _aliveSeen.Clear();
        _clearTried.Clear();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _clearTried = new();

    /// <summary>
    /// Goes back for the monsters seen alive within <paramref name="radius"/> of the leader, nearest first, so a
    /// room is empty before the party walks on. Fighting only what stood within reach left the room before
    /// Andariel half full behind the party.
    /// </summary>
    private async Task ClearSeenNear(double radius)
    {
        var keepAway = _keepAwayFrom;
        var started = DateTime.UtcNow;
        // Each monster is gone after once: one standing behind a wall is not fought, and going back for it again and
        // again spun the party on the spot in Catacombs 4.
        while (DateTime.UtcNow - started < ClearLimit && await LeaderPresent() && !RegroupRequested && !StopRequested())
        {
            TrackMonsters();
            var here = Leader.Game.Me.Location;
            var left = _aliveSeen.Where(entry => entry.Value.Distance(here) <= radius && !IsHuntedBoss(entry.Key) && !_clearTried.ContainsKey(entry.Key)
                && (keepAway == null || entry.Value.Distance(keepAway) > _keepAwayRadius) && StillTracked(entry.Key)).ToList();
            if (left.Count == 0)
            {
                return;
            }

            var (id, at) = left.MinBy(entry => entry.Value.Distance(here));
            _clearTried[id] = true;
            Log.Information("Clearing a monster last seen at {Location} before moving on, {Left} left nearby", at, left.Count);
            if (at.Distance(here) > 8 && !await WalkTo(at, BossTooClose) && Leader.Game.Me.Location.Distance(at) > 10)
            {
                _aliveSeen.TryRemove(id, out _);
                continue;
            }

            if (BossTooClose())
            {
                return;
            }

            await FightHere(EngageRadius);
            TrackMonsters();
            if (Leader.Game.Me.Location.Distance(at) < 10 && !members.Where(member => member.InGame).Any(member => member.Game.WorldObjects.Values.Any(entity => entity.Id == id)))
            {
                _aliveSeen.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// A monster some member's client still knows of. One that died out of view or wandered off is forgotten by the
    /// clients, and walking to where it last stood had the party running round Andariel's chamber hitting nothing.
    /// </summary>
    private bool StillTracked(uint id)
        => members.Where(member => member.InGame).Any(member => member.Game.WorldObjects.Values.Any(entity => entity.Id == id
            && entity.State is not (EntityState.Dead or EntityState.Dieing)));

    private bool IsHuntedBoss(uint id)
        => _huntedBoss is { } boss && members.Where(member => member.InGame)
            .Any(member => member.Game.WorldObjects.Values.Any(entity => entity.Id == id && entity.NPCCode == boss));

    private bool BossTooClose()
        => _huntedBoss is { } boss && FindBoss(boss) is { } found && found.State is not (EntityState.Dead or EntityState.Dieing)
            && found.Location.Distance(Leader.Game.Me.Location) <= LureDistance;

    private static readonly TimeSpan FightLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GatherLimit = TimeSpan.FromSeconds(10);

    private CancellationTokenSource _following;
    private List<Task> _followers = [];

    public IReadOnlyList<PartyMember> Members => members;

    public PartyMember Leader => members.First(member => member.IsLeader);

    public IEnumerable<PartyMember> Followers => members.Where(member => !member.IsLeader);

    /// <summary>The area the party is in. Game.Area lags behind transitions, so the party keeps its own.</summary>
    public Area Area { get; set; }

    /// <summary>
    /// Set when a member dropped out (chicken, death) and is back in town: the party stops what it is doing
    /// and goes home to it, rather than letting it walk out alone into whatever sent it home.
    /// </summary>
    public volatile bool RegroupRequested;

    /// <summary>
    /// The leader chickened or dropped while the others are still out: they hold where they are and it comes back
    /// through a portal one of them opens. Calling everyone home for it threw away each Diablo attempt.
    /// </summary>
    public volatile bool LeaderAway;

    private static readonly TimeSpan LeaderAwayLimit = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Whether the leader is with the party. A leader that has just dropped out while followers are still in the field
    /// is waited for instead of ending what the party is doing; one that does not come back in time calls the regroup.
    /// </summary>
    public async Task<bool> LeaderPresent()
    {
        if (Leader.InGame && !LeaderAway)
        {
            return true;
        }

        if (!LeaderAway)
        {
            if (RegroupRequested || Area == Leader.Game.Act.MapTownArea() || !Followers.Any(member => member.InGame && !member.Game.IsInTown()))
            {
                return false;
            }

            LeaderAway = true;
            Log.Information("{Leader} dropped out in {Area}; the others hold there for its return", Leader.Name, Area);
        }

        var since = DateTime.UtcNow;
        // Not while nobody holds anything: when the game server dropped all four, the party "held" ninety seconds for a
        // leader who could not rejoin a game that no longer existed.
        while (LeaderAway && DateTime.UtcNow - since < LeaderAwayLimit && !StopRequested()
            && Followers.Any(member => member.InGame && !member.Game.IsInTown()))
        {
            // The rejoin itself happens between stretches: waiting without them, the leader never came back into the
            // game and the party stood out the whole limit.
            await BetweenStretches();
            await Task.Delay(300);
        }

        if (LeaderAway)
        {
            LeaderAway = false;
            Log.Information("{Leader} did not come back to {Area} in time; the party regroups", Leader.Name, Area);
            RegroupRequested = true;
        }

        return Leader.InGame && !RegroupRequested;
    }

    /// <summary>A follower still out opens a portal and the leader, back in town after a chicken, steps through it.</summary>
    public async Task<bool> BringLeaderBack()
    {
        var opener = Followers.FirstOrDefault(member => member.InGame && !member.Game.IsInTown() && member.Game.Me.Location.Distance(Leader.Game.Me.Location) > 0
            && member.Game.Inventory.Items.Any(item => item.Name == ItemName.TomeOfTownPortal && item.Amount > 0));
        if (opener == null || !await CastPortal(opener))
        {
            return false;
        }

        for (var look = 0; look < 3; look++)
        {
            if (await ThroughPortalInto(Leader, Area) is { } landed)
            {
                Area = landed;
                return true;
            }

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>A stop file beside the log is the clean way out; killing the process leaves the characters in the game.</summary>
    public Func<bool> StopRequested { get; set; } = () => false;

    /// <summary>Called between stretches, for whatever the bot wants done often, such as spending points.</summary>
    public Func<Task> BetweenStretches { get; set; } = () => Task.CompletedTask;

    /// <summary>Set by the bot: the loot round run before the party goes home.</summary>
    public Func<Task> LootBeforeLeaving { get; set; } = () => Task.CompletedTask;

    /// <summary>
    /// The upkeep a fight keeps doing - rejoins, restocks, the trip to town together - without the loot and buffs of
    /// <see cref="BetweenStretches"/>, which would walk members into the pack.
    /// </summary>
    public Func<Task> DuringFight { get; set; } = () => Task.CompletedTask;

    private static readonly TimeSpan DuringFightEvery = TimeSpan.FromSeconds(5);

    /// <summary>Restocks every member in town; set by the bot, which knows the merchants.</summary>
    public Func<Task> RestockInTown { get; set; }

    /// <summary>Takes one member home through a portal of its own; set by the bot.</summary>
    public Func<PartyMember, Task> GoHomeAlone { get; set; }

    /// <summary>
    /// Set while the party goes home to come back together: its area still names the field until the last member is home,
    /// and the sorceress, back from a chicken, took a portal out to it alone meanwhile.
    /// </summary>
    public volatile bool HomeBound;

    /// <summary>
    /// Everyone still out goes home through a portal of its own, all at once - walking to the leader's portal through the
    /// pack that is the reason for leaving is the risk - then <paramref name="inTown"/>, and back together through a portal
    /// in town that leads to where the party was.
    /// </summary>
    public async Task<bool> FleeToTown(Func<Task> inTown)
    {
        var field = Area;
        var town = TownWaypoints[field.MapToAct()].ToArea();
        var out_ = members.Where(member => member.InGame && member.Game.Me != null && !member.Game.IsInTown()).ToList();
        foreach (var member in out_)
        {
            member.Mode = FollowMode.Off;
        }

        HomeBound = true;
        try
        {
            await Task.WhenAll(out_.Select(member => GoHomeAlone?.Invoke(member) ?? Task.CompletedTask));
        }
        finally
        {
            HomeBound = false;
            foreach (var member in Followers)
            {
                member.Mode = FollowMode.Follow;
            }
        }

        if (!Leader.Game.IsInTown())
        {
            Log.Warning("{Leader} did not get home to flee with the party", Leader.Name);
            return false;
        }

        Area = town;
        await inTown();
        var returned = await ReturnTo(field);
        Log.Information("The party {Outcome} to {Area} after fleeing to town", returned ? "went back" : "did NOT get back", field);
        return returned;
    }

    /// <summary>
    /// Back from town to <paramref name="field"/>: each member through any portal into it, looked for where portals stand
    /// in this town - the leader had shopped out of sight of them, found none in two seconds, and the Diablo step then ran
    /// its seals from the Pandemonium Fortress for eight minutes. Without a portal left (their casters chickened out of the
    /// game) the party goes by waypoint and on foot.
    /// </summary>
    private async Task<bool> ReturnTo(Area field)
    {
        await WaitForParty(8);
        foreach (var member in Followers)
        {
            member.Mode = FollowMode.Off;
        }

        Area? leaderLanded = null;
        try
        {
            // The leader last: it takes its own portal when it finds one, and a town portal closes once its owner goes back
            // through it - the amazon, a second behind the paladin into his, clicked at nothing until she left the game.
            // Not for long: the others would stand in the field without it.
            var followers = Followers.Where(member => member.InGame && member.Game.IsInTown()).Select(member => ThroughPortalInto(member, field)).ToList();
            await Task.WhenAny(Task.WhenAll(followers), Task.Delay(OwnerWaits));
            if (Leader.InGame && Leader.Game.IsInTown())
            {
                leaderLanded = await ThroughPortalInto(Leader, field);
            }

            await Task.WhenAll(followers);
        }
        finally
        {
            foreach (var member in Followers)
            {
                member.Mode = FollowMode.Follow;
            }
        }

        if (leaderLanded is { } landedIn)
        {
            ForgetMonsters();
            Area = landedIn;
            return landedIn == field || await EnterArea(field);
        }

        Log.Information("No portal took {Leader} back to {Area}; the party goes by waypoint", Leader.Name, field);
        return await GoTo(field);
    }

    /// <summary>Asked for when a boss fight is down to two members: everyone goes to town and comes back together.</summary>
    public volatile bool TownTripRequested;

    public void StartFollowing()
    {
        _following = new CancellationTokenSource();
        _followers = Followers.Select(member => Task.Run(() => FollowLoop(member, _following.Token))).ToList();
    }

    public async Task StopFollowing()
    {
        if (_following == null)
        {
            return;
        }

        await _following.CancelAsync();
        await Task.WhenAll(_followers);
        _following.Dispose();
        _following = null;
    }

    public void Dispose()
    {
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
    }

    /// <summary>Walks <paramref name="member"/> along a path towards <paramref name="to"/> for at most <paramref name="limit"/>.</summary>
    public async Task WalkTo(PartyMember member, Point to, TimeSpan limit)
    {
        using var walk = new CancellationTokenSource(limit);
        await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, to, MovementMode.Walking, walk.Token);
    }

    private async Task FollowLoop(PartyMember member, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // The barbarian keeps the party's Battle Orders up between his own attacks: cast from the fight's upkeep, it
                // came in the middle of a whirl and the server dropped it. A member back from a chicken has it within a
                // second of him, not at the next upkeep five seconds on.
                if (member.InGame && member.Mode != FollowMode.Off && member.SkillLevel(D2NG.Core.D2GS.Players.Skill.BattleOrders) > 0)
                {
                    await LevelingBuffs.KeepUpForParty(member, this);
                }

                if (member.InGame && member.Mode != FollowMode.Off && !member.Game.IsInTown() && (LeaderAway || !Leader.InGame))
                {
                    // The leader is away: this member holds its ground rather than following it to town.
                    if (!await combat.FightTick(member, member.Game.Me.Location, EngageRadius))
                    {
                        await Task.Delay(200, token);
                    }

                    continue;
                }

                if (!member.InGame || !Leader.InGame || member.Mode == FollowMode.Off)
                {
                    await Task.Delay(200, token);
                    continue;
                }

                // The party is drawing a big pack back to cleared ground: fighting where it stands kept this member in
                // the middle of the pack while the leader left.
                if (PullBackTo is { } pullBack && PullBackHolds(pullBack) && member.Game.Me.Location.Distance(pullBack) > PullBackArrived)
                {
                    using var back = CancellationTokenSource.CreateLinkedTokenSource(token);
                    back.CancelAfter(TimeSpan.FromSeconds(2));
                    await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, pullBack, MovementMode.Walking, back.Token);
                    continue;
                }

                // Something hitting this member comes first, wherever the leader is.
                if (await combat.FightTick(member, member.Game.Me.Location, SelfDefenceRadius))
                {
                    continue;
                }

                var leaderAt = Leader.Game.Me.Location;
                // Far from the leader means another area: a crossing this member did not make. Walking at the
                // leader's coordinates never gets through the warp, so it travels there itself.
                if (member.Game.Me.Location.Distance(leaderAt) > StrandedDistance)
                {
                    await CatchUp(member, leaderAt, token);
                    continue;
                }

                // The amazon and the sorceress shoot from behind the paladin and the barbarian: in Maggot Lair's halls
                // there was no room to step back, and walking up to the front put them in the pack.
                if (member.Role == PartyRole.Ranged && BehindTheLeader(member, leaderAt) is { } behind && member.Game.Me.Location.Distance(behind) > BehindArrived)
                {
                    using var back = CancellationTokenSource.CreateLinkedTokenSource(token);
                    back.CancelAfter(TimeSpan.FromSeconds(1.5));
                    await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, behind, MovementMode.Walking, back.Token);
                }

                if (await combat.FightTick(member, leaderAt, EngageRadius))
                {
                    continue;
                }

                var distance = member.Game.Me.Location.Distance(leaderAt);
                var keep = member.Role == PartyRole.Ranged ? RangedFollowDistance : FollowDistance;
                if (member.Mode == FollowMode.Follow && distance > keep && distance < 150)
                {
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cancel.CancelAfter(TimeSpan.FromSeconds(2));
                    var goal = member.Game.Me.Location.GetPointBeforePointInSameDirection(leaderAt, keep - 2);
                    await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, goal, MovementMode.Walking, cancel.Token);
                }
                else
                {
                    await Task.Delay(150, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Debug("{Character} follow loop: {Reason}", member.Name, exception.Message);
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastCatchUpLog = new();

    /// <summary>
    /// A member far behind: in the leader's area it walks the path to the leader, and in another area - a
    /// crossing it did not make - it travels there first. Bounded, so the loop gets back to fighting.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Point At, DateTime Since)> _stuckSince = new();

    /// <summary>objects.txt rows with IsDoor set. The map is pathed as if they stood open, so a closed one stops a walk dead.</summary>
    private static HashSet<int> DoorCodes => AreaMapExtensions.DoorObjectCodes;

    /// <summary>Opens every closed door within <paramref name="radius"/>; true when it opened one. An open door is left alone, a click would shut it.</summary>
    /// <summary>Doors this game's party opened. Per party, not per map: a realm map seed repeats across games, door ids with it.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _openedDoors = new();

    private async Task<bool> OpenDoorsNear(PartyMember member, double radius, Func<WorldObject, bool> wanted = null)
    {
        var game = member.Game;
        var nearby = game.WorldObjects
            .Where(entry => entry.Key.Item2 == EntityType.Object).Select(entry => entry.Value)
            .Where(entity => DoorCodes.Contains((int)entity.Code) && entity.Location.Distance(game.Me.Location) <= radius * 2)
            .ToList();
        if (nearby.Count > 0)
        {
            Log.Debug("{Character} at {Location} sees doors {Doors}", member.Name, game.Me.Location,
                string.Join(", ", nearby.Select(door => $"{door.Id}:{door.Code}@{door.Location} {door.State} {door.Location.Distance(game.Me.Location):0}{(_openedDoors.ContainsKey(door.Id) ? " opened" : "")}")));
        }

        var doors = nearby
            .Where(entity => entity.State == EntityState.Alive && entity.Location.Distance(game.Me.Location) <= radius && !_openedDoors.ContainsKey(entity.Id)
                && (wanted == null || wanted(entity)))
            .ToList();
        foreach (var door in doors)
        {
            // Only a door still shown closed is clicked, so a retry cannot shut one the last click opened.
            for (var attempt = 0; attempt < 3 && door.State == EntityState.Alive; attempt++)
            {
                if (door.Location.Distance(game.Me.Location) > 3)
                {
                    await game.MoveToAsync(door.Location);
                    await Task.Delay(300);
                }

                game.InteractWithEntity(door);
                GeneralHelpers.TryWithTimeout(_ => door.State != EntityState.Alive, TimeSpan.FromSeconds(3));
            }

            if (door.State != EntityState.Alive)
            {
                _openedDoors[door.Id] = true;
            }

            Log.Information("{Character} {Outcome} the door {Code} at {Location}", member.Name,
                door.State != EntityState.Alive ? "opened" : "could NOT open", door.Code, door.Location);
        }

        return doors.Count > 0;
    }

    /// <summary>A member that left and came back starts with a clean record: it arrived by waypoint on the very spot it had been stuck on four minutes earlier, and was sent away again at once.</summary>
    public void ForgetStuck(PartyMember member)
    {
        _stuckSince.TryRemove(member.Name, out _);
        _stuckChecked.TryRemove(member.Name, out _);
        _failedCrossings.TryRemove(member.Name, out _);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _stuckChecked = new();

    private static readonly TimeSpan StuckRecordGap = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan StuckBeforeSteps = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StuckBeforeLeaving = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A member the server keeps putting back on the same spot (ReassignPlayer after every Run) is stuck in
    /// collision no path leads out of. Short direct steps get it out of most; when they do not, it leaves the
    /// game, and the rejoin puts it in town, where the party regroups.
    /// </summary>
    private async Task<bool> Unstick(PartyMember member)
    {
        var here = member.Game.Me.Location;
        // A record not looked at for a while is from an earlier stretch: the amazon waited ninety seconds in town beside the
        // portal spot she had stood on before, and came out of the wait "stuck for 182s".
        if (DateTime.UtcNow - _stuckChecked.GetValueOrDefault(member.Name) > StuckRecordGap)
        {
            _stuckSince[member.Name] = (here, DateTime.UtcNow);
        }

        _stuckChecked[member.Name] = DateTime.UtcNow;
        var seen = _stuckSince.GetOrAdd(member.Name, (here, DateTime.UtcNow));
        if (seen.At.Distance(here) > 8)
        {
            _stuckSince[member.Name] = (here, DateTime.UtcNow);
            return false;
        }

        var stuckFor = DateTime.UtcNow - seen.Since;
        if (stuckFor > TimeSpan.FromSeconds(8) && await OpenDoorsNear(member, 15))
        {
            _stuckSince[member.Name] = (here, DateTime.UtcNow);
            return true;
        }

        if (stuckFor < StuckBeforeSteps)
        {
            return false;
        }

        // Leaving puts it in town: from town that gets it nowhere.
        if (stuckFor > StuckBeforeLeaving && !member.Game.IsInTown())
        {
            Log.Warning("{Character} has been stuck at {Location} for {Seconds:0}s; leaving the game to rejoin in town", member.Name, here, stuckFor.TotalSeconds);
            _stuckSince.TryRemove(member.Name, out _);
            await member.Game.LeaveGame();
            return true;
        }

        foreach (var radius in new[] { 3, 6, 10 })
        {
            for (var step = 0; step < 8; step++)
            {
                var angle = step * Math.PI / 4;
                await member.Game.MoveToAsync(new Point((ushort)(here.X + radius * Math.Cos(angle)), (ushort)(here.Y + radius * Math.Sin(angle))));
                await Task.Delay(350);
                // A shuffle along the wall it is stuck against is not out; only real distance from the spot counts.
                if (member.Game.Me.Location.Distance(seen.At) > 8)
                {
                    Log.Information("{Character} got unstuck from {From} to {To}", member.Name, seen.At, member.Game.Me.Location);
                    _stuckSince[member.Name] = (member.Game.Me.Location, DateTime.UtcNow);
                    return true;
                }
            }
        }

        return true;
    }

    private async Task CatchUp(PartyMember member, Point leaderAt, CancellationToken token)
    {
        if (await Unstick(member))
        {
            return;
        }

        // Each character starts a game in the furthest act it has opened: the barbarian started in the Pandemonium Fortress
        // while the others stood in Kurast Docks, and tried to walk there for the whole game - the party's waypoint then
        // went without him. Its own town's waypoint takes it to the party's town. Checked before the area: the act towns'
        // coordinates overlap, and his position counted as walkable Kurast Docks.
        if (member.Game.IsInTown() && member.Game.Act != Area.MapToAct() && await WaypointToPartyTown(member))
        {
            return;
        }

        // In town while the party is on its way home, or with the leader there, it does not go out to the field alone: catching
        // up, the sorceress went back into the Chaos Sanctuary while the others came home to go back in together.
        if (member.Game.IsInTown() && Area != TownWaypoints[Area.MapToAct()].ToArea() && (HomeBound || Leader.Game.IsInTown()))
        {
            await Task.Delay(500, token);
            return;
        }

        // Standing on the waypoint it came by - no map counts that tile as walkable - a member is in the area all the same.
        var inPartyArea = await pathingService.IsNavigatablePointInArea(member.Game.MapId, AreaTraveler.MapDifficulty, Area, member.Game.Me.Location)
            || await traveler.CurrentArea(member.Client) == Area;
        if (DateTime.UtcNow - _lastCatchUpLog.GetValueOrDefault(member.Name) > TimeSpan.FromSeconds(10))
        {
            _lastCatchUpLog[member.Name] = DateTime.UtcNow;
            Log.Information("{Character} is {Distance:0} from the leader, {Where}; catching up", member.Name, member.Game.Me.Location.Distance(leaderAt),
                inPartyArea ? $"in {Area} too" : $"outside {Area}");
        }

        if (inPartyArea)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancel.CancelAfter(TimeSpan.FromSeconds(6));
            var path = await pathingService.GetPathToLocation(member.Game.MapId, AreaTraveler.MapDifficulty, Area, member.Game.Me.Location, leaderAt, MovementMode.Walking);
            if (path.Count > 0)
            {
                await MovementHelpers.TakePathOfLocations(member.Game, path, MovementMode.Walking, cancel.Token);
            }
            else
            {
                await Task.Delay(500, token);
            }

            return;
        }

        // The party went home while this member was still out: walking up from Catacombs 4 takes minutes, a rejoin
        // lands it in town in seconds.
        if (Area == TownWaypoints[Area.MapToAct()].ToArea() && !member.Game.IsInTown())
        {
            Log.Information("{Character} is out in the field while the party is in {Area}; leaving the game to rejoin in town", member.Name, Area);
            await member.Game.LeaveGame();
            return;
        }

        // From town a portal into the party's area beats any waypoint and walk.
        if (member.Game.IsInTown() && !Area.Equals(TownWaypoints[Area.MapToAct()].ToArea()) && await TakePortalInto(member, Area))
        {
            Log.Information("{Character} took a town portal to the party in {Area}", member.Name, Area);
            return;
        }

        // The Canyon of the Magi is entered only through the journal's portal or its waypoint: a member back in town
        // without that waypoint has no way there on foot, and leaving and rejoining over and over gets it no closer.
        // It waits in town for the party.
        if (member.Game.IsInTown() && (await traveler.Route(member.Client, Area)).Count == 0)
        {
            D2NG.Core.Tracing.TraceEvents.Wait(member.Game, "no route from town", true);
            // A portal into the party's area is the way there when one stands in town.
            if (await TakePortalInto(member, Area))
            {
                Log.Information("{Character} took a town portal to the party in {Area}", member.Name, Area);
                return;
            }

            await Task.Delay(2000, token);
            return;
        }

        if (await traveler.TraverseTo(member.Client, Area))
        {
            _failedCrossings.TryRemove(member.Name, out _);
            return;
        }

        // The traversal steps aside between attempts, which resets the stuck timer, so a member pinned at a
        // border would bounce there for ever: in Jail 2 two followers did. Three failed crossings in a row
        // send it to town, and it comes back by waypoint.
        if (_failedCrossings.AddOrUpdate(member.Name, 1, (_, count) => count + 1) >= 3)
        {
            _failedCrossings.TryRemove(member.Name, out _);
            Log.Warning("{Character} failed to cross into {Area} three times; leaving the game to rejoin in town", member.Name, Area);
            await member.Game.LeaveGame();
            return;
        }

        await Task.Delay(2000, token);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failedCrossings = new();

    /// <summary>
    /// A game starts in the town most members stand in, the higher act on a tie; a member elsewhere takes its own town's
    /// waypoint there. A game in another difficulty first - the gem mules, the shield check - puts that member in act 1 of
    /// the next one, and with the paladin leading from there all four walked acts 1 to 3: a minute and a half.
    /// </summary>
    public async Task GatherWhereMostAre()
    {
        var placed = members.Where(member => member.InGame && member.Game.Me != null && member.Game.IsInTown()).ToList();
        var most = placed.GroupBy(member => member.Game.Act).OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key).FirstOrDefault();
        if (most == null || most.Key == Area.MapToAct() || !TownWaypoints.TryGetValue(most.Key, out var town))
        {
            return;
        }

        var before = Area;
        Area = town.ToArea();
        Log.Information("The party starts in {Town}, where {Count} of {Members} stand, not in the leader's {Before}", Area, most.Count(), placed.Count, before);
        foreach (var member in placed.Where(member => member.Game.Act != most.Key))
        {
            if (!await WaypointToPartyTown(member) && member == Leader)
            {
                Area = before;
                return;
            }
        }
    }

    private async Task<bool> WaypointToPartyTown(PartyMember member)
    {
        var destination = TownWaypoints[Area.MapToAct()];
        if (!TownWaypoints.TryGetValue(member.Game.Act, out var own)
            || (!member.Game.Me.AllowedWaypoints.IsEmpty && !member.Game.Me.AllowedWaypoints.Contains(destination)))
        {
            return false;
        }

        var waypoint = member.Game.GetEntityByCode(own.ToEntityCode()).OrderBy(entity => entity.Location.Distance(member.Game.Me.Location)).FirstOrDefault();
        if (waypoint == null)
        {
            var path = await pathingService.GetPathToObject(member.Game.MapId, AreaTraveler.MapDifficulty, own.ToArea(), member.Game.Me.Location, own.ToEntityCode(), MovementMode.Walking);
            if (path.Count > 0)
            {
                await MovementHelpers.TakePathOfLocations(member.Game, path, MovementMode.Walking);
            }

            waypoint = member.Game.GetEntityByCode(own.ToEntityCode()).OrderBy(entity => entity.Location.Distance(member.Game.Me.Location)).FirstOrDefault();
        }

        if (waypoint == null)
        {
            return false;
        }

        if (waypoint.Location.Distance(member.Game.Me.Location) > 4)
        {
            await MovementHelpers.MoveToWorldObject(member.Game, pathingService, mapApiService, waypoint, MovementMode.Walking);
        }

        bool took;
        try
        {
            took = member.Game.TakeWaypoint(waypoint, destination) && await traveler.ConfirmInArea(member.Client, destination.ToArea());
        }
        catch (InvalidOperationException exception)
        {
            Log.Warning("{Character} cannot take the waypoint to {Destination}: {Reason}", member.Name, destination, exception.Message);
            return false;
        }

        Log.Information("{Character} {Outcome} the waypoint from {From} to the party's town {Town}", member.Name, took ? "took" : "could NOT take", own, destination);
        return took;
    }

    /// <summary>The leader fights until nothing is left within <paramref name="radius"/>; the others fight on their own loops.</summary>
    public async Task FightHere(double radius)
    {
        // After a fight given up the party moves on rather than standing in the next one: at the Catacombs 3
        // stairs it fought spiders it could not hurt for minute after minute until the barbarian ran dry.
        if (DateTime.UtcNow < _pushOnUntil && !ClearRoomByRoom)
        {
            return;
        }

        // Pulling: on a boss level the monsters are fought back at the last cleared spot rather than in their own
        // room, so a few come at a time instead of the whole lair.
        if (ClearRoomByRoom && LastQuietSpot is { } quiet && Leader.Game.Me.Location.Distance(quiet) > 8
            && NPCHelpers.GetNearbyNPCs(Leader.Client, Leader.Game.Me.Location, 10, (int)radius).Any(npc => !LevelingCombat.Untargetable.Contains(npc.NPCCode)))
        {
            using var back = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await MovementHelpers.MoveToLocation(Leader.Game, pathingService, mapApiService, quiet, MovementMode.Walking, back.Token);
            await Task.Delay(800);
        }

        await FallBackOnDrop();
        await PullBackFromBigPack();
        await OfferPortalToRejoiners();
        var started = DateTime.UtcNow;
        var limit = ClearRoomByRoom ? ClearLimit : FightLimit;
        var upkeep = DateTime.UtcNow;
        while (DateTime.UtcNow - started < limit && await LeaderPresent() && !RegroupRequested)
        {
            // Two members chickened at the Spider Cavern chest and never came back: the fight never asked anyone to rejoin.
            if (DateTime.UtcNow - upkeep > DuringFightEvery)
            {
                upkeep = DateTime.UtcNow;
                await DuringFight();
                if (Leader.Game.IsInTown())
                {
                    return;
                }

                // Here too, not only as a fight starts: the barbarian stood in town at the portal spot through one long
                // fight of the leader's, found no portal and walked the River of Flame back, seventy seconds out.
                await OfferPortalToRejoiners();
            }

            // The boss being hunted has come close: the fight here gives way to drawing it back. Fighting on, the
            // party stood in Andariel's room and tanked her until all four had chickened.
            if (_huntedBoss is { } hunted && !NotLured.Contains(hunted) && FindBoss(hunted) is { } boss && boss.Location.Distance(Leader.Game.Me.Location) <= LureDistance
                && LureSpotFor(boss.Location) is { } quietSpot && Leader.Game.Me.Location.Distance(quietSpot) > 6)
            {
                return;
            }

            UpdateFinishingChicken();
            await FallBackOnDrop();
            await PullBackFromBigPack();
            if (!await combat.FightTick(Leader, Leader.Game.Me.Location, radius))
            {
                // Nothing left here: the spot a boss is drawn back to.
                await RecordQuietSpot(Leader.Game.Me.Location);
                return;
            }
        }

        if (ClearRoomByRoom)
        {
            return;
        }

        _pushOnUntil = DateTime.UtcNow + PushOnAfterGivingUp;
        Log.Information("{Character} gave up a fight after {Seconds}s at {Location}; pushing on", Leader.Name, FightLimit.TotalSeconds, Leader.Game.Me.Location);
    }

    private DateTime _pushOnUntil = DateTime.MinValue;

    /// <summary>
    /// A big pack is fought back on cleared ground, where it arrives strung out: in the Chaos Sanctuary the party stood
    /// among eighteen Storm Casters, Doom and Oblivion Knights and three of four chickened within fifteen seconds.
    /// </summary>
    private async Task PullBackFromBigPack()
    {
        // Once per stretch of fighting: the pack follows to the cleared spot, and pulling back again from there would
        // walk the party all the way down the path it came.
        if (DateTime.UtcNow - _pulledBackAt < PullBackCooldown)
        {
            return;
        }

        var leaderAt = Leader.Game.Me.Location;
        var pack = PackAround(leaderAt);
        if (pack.Count < BigPack)
        {
            return;
        }

        var centre = Centre(pack);
        // In a hall there is nowhere to fall back to: the party holds its line, melee in front, ranged behind.
        if (await InCorridor(leaderAt))
        {
            return;
        }

        // A pull-back that drew nothing out is not repeated: in Maggot Lair's tunnels the party pulled back from the same
        // pack every twenty seconds for seven minutes and moved forty yards. Where the last pack stood, it fights instead.
        if (_lastPackCentre is { } last && last.Distance(centre) <= SamePackDistance && DateTime.UtcNow - _pulledBackAt < PullBackGiveUp)
        {
            if (!_pullBackGivenUp)
            {
                _pullBackGivenUp = true;
                Log.Information("Pulling back did not draw the pack at {Centre} out; the party fights where it stands", centre);
            }

            return;
        }

        _pullBackGivenUp = false;
        if (FallBackSpot(centre, leaderAt) is not { } spot)
        {
            return;
        }

        Log.Information("{Count} monsters around the leader at {Location}; the party pulls back to {Spot}", pack.Count, leaderAt, spot);
        await PullBack(spot, centre);
    }

    private List<WorldObject> PackAround(Point at)
        => NPCHelpers.GetNearbyNPCs(Leader.Client, at, 30, (int)BigPackRadius).Where(npc => !LevelingCombat.Untargetable.Contains(npc.NPCCode)).ToList();

    private static Point Centre(List<WorldObject> pack)
        => new((ushort)pack.Average(npc => npc.Location.X), (ushort)pack.Average(npc => npc.Location.Y));

    /// <summary>The cleared spot to fall back to from the pack at <paramref name="centre"/>; null when there is none or the leader stands on it.</summary>
    private Point FallBackSpot(Point centre, Point leaderAt)
        => _quietSpotsArea == Area ? AwayFromPack(_quietSpots, centre, leaderAt) : null;

    /// <summary>How much closer to the pack than the leader stands the way back may pass, for the bends of a path.</summary>
    private const double TowardsPackSlack = 2;

    /// <summary>
    /// The latest cleared spot far enough from the pack and near enough to walk back to, whose way from the leader does not
    /// lead towards the pack: after one retreat the latest spot 25 from the pack could lie on its far side, and the party
    /// "fell back" through it. Null when there is none or the leader stands on it.
    /// </summary>
    public static Point AwayFromPack(IReadOnlyList<Point> quietSpots, Point centre, Point leaderAt)
    {
        var spot = quietSpots.LastOrDefault(quiet => quiet.Distance(centre) >= PullBackDistance && quiet.Distance(leaderAt) <= PullBackReach
            && ClosestOnTheWay(leaderAt, quiet, centre) >= leaderAt.Distance(centre) - TowardsPackSlack);
        return spot == null || spot.Distance(leaderAt) <= PullBackArrived ? null : spot;
    }

    /// <summary>How close the straight way from <paramref name="from"/> to <paramref name="to"/> comes to <paramref name="point"/>.</summary>
    private static double ClosestOnTheWay(Point from, Point to, Point point)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);
        double x = from.X + t * dx - point.X, y = from.Y + t * dy - point.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private async Task PullBack(Point spot, Point centre)
    {
        PullBackTo = spot;
        _lastPackCentre = centre;
        _pulledBackAt = DateTime.UtcNow;
        _pullBackUntil = DateTime.UtcNow + PullBackHold;
        _walkingBack = true;
        try
        {
            using var back = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await MovementHelpers.MoveToLocation(Leader.Game, pathingService, mapApiService, spot, MovementMode.Walking, back.Token);
        }
        finally
        {
            _walkingBack = false;
        }
    }

    private volatile bool _walkingBack;

    private const double PullBackLeft = 10;

    /// <summary>
    /// The others make for the pull-back spot while the leader walks there or stands on it. Once he stepped out to fight the
    /// pack it drew, two seconds after pulling back, the amazon and the sorceress still walked to the spot and out again for
    /// four more seconds without an arrow or an orb.
    /// </summary>
    private bool PullBackHolds(Point pullBack)
        => DateTime.UtcNow < _pullBackUntil && (_walkingBack || Leader.Game.Me.Location.Distance(pullBack) <= PullBackLeft);

    /// <summary>
    /// Whether the party falls back and holds when a member drops out (<see cref="FallBackOnDrop"/>,
    /// <see cref="HoldForTheShort"/>); set by the bot for where the packs hit hardest.
    /// </summary>
    public Func<bool> HoldsForTheShort { get; set; } = () => false;

    /// <summary>Members in the game and out of town when last counted, to see one drop out.</summary>
    private int _fieldSeen;

    /// <summary>Set when a member dropped out, until the party is whole again or the hold gave up on it.</summary>
    private bool _shortHanded;

    private int FieldCount => members.Count(member => member.InGame && member.Game.Me != null && !member.Game.IsInTown());

    private bool NoteDrop()
    {
        var field = FieldCount;
        var dropped = field < _fieldSeen;
        _fieldSeen = field;
        if (dropped && HoldsForTheShort())
        {
            _shortHanded = true;
        }

        return dropped;
    }

    /// <summary>
    /// A member dropping out mid-fight sends the rest back to cleared ground at once, whatever pulled back before: in Hell's
    /// Chaos Sanctuary three of four chickens on 17 and 18 Nov 2026 followed another within 45 seconds - a median 6.4 s
    /// apart - while the others fought on where the first had fallen, and the barbarian was the first in half of them.
    /// </summary>
    private async Task FallBackOnDrop()
    {
        if (!NoteDrop() || !HoldsForTheShort() || InBossFight || !Leader.InGame || Leader.Game.Me == null || Leader.Game.IsInTown())
        {
            return;
        }

        var leaderAt = Leader.Game.Me.Location;
        var pack = PackAround(leaderAt);
        if (pack.Count < FallBackPack)
        {
            return;
        }

        var centre = Centre(pack);
        if (FallBackSpot(centre, leaderAt) is not { } spot)
        {
            Log.Information("A member dropped out with {Count} monsters around the leader at {Location}; no cleared spot to fall back to", pack.Count, leaderAt);
            return;
        }

        Log.Information("A member dropped out with {Count} monsters around the leader at {Location}; the party falls back to {Spot}", pack.Count, leaderAt, spot);
        await PullBack(spot, centre);
    }

    /// <summary>A drop with fewer monsters than this near is fought out where it is.</summary>
    private const int FallBackPack = 3;

    /// <summary>
    /// The members not with the party: out of the game, in town, away from the leader, or back without the Battle Orders the
    /// party's barbarian gives.
    /// </summary>
    private List<PartyMember> ShortOf()
    {
        var leaderAt = Leader.Game.Me.Location;
        var shouts = members.Any(member => member.InGame && member.Game.Me != null && !member.Game.IsInTown() && member.SkillLevel(D2NG.Core.D2GS.Players.Skill.BattleOrders) > 0);
        return members.Where(member => !member.IsLeader && (!member.InGame || member.Game.Me == null || member.Game.IsInTown()
            || member.Game.Me.Location.Distance(leaderAt) > ShortHandedNear
            || (shouts && !member.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders)))).ToList();
    }

    /// <summary>
    /// After a member dropped out the party does not walk on into the next pack without it: it fights what comes to it where
    /// it stands until the member is back beside the leader with Battle Orders - twelve seconds from the chicken through the
    /// rejoin portal and five more for the shout, at the median of 17 Nov - or the wait runs out.
    /// </summary>
    private async Task HoldForTheShort()
    {
        NoteDrop();
        if (!_shortHanded)
        {
            return;
        }

        if (!HoldsForTheShort() || !Leader.InGame || Leader.Game.Me == null || Leader.Game.IsInTown())
        {
            _shortHanded = false;
            return;
        }

        var missing = ShortOf();
        if (missing.Count == 0)
        {
            _shortHanded = false;
            return;
        }

        var since = DateTime.UtcNow;
        Log.Information("The party holds at {Location} until {Members} are back", Leader.Game.Me.Location, string.Join(", ", missing.Select(member => member.Name)));
        D2NG.Core.Tracing.TraceEvents.Wait(Leader.Game, "short-handed", true);
        try
        {
            while (DateTime.UtcNow - since < ShortHandedHold && Leader.InGame && !Leader.Game.IsInTown() && !RegroupRequested && !TownTripRequested
                   && !StopRequested() && ShortOf().Count > 0)
            {
                await DuringFight();
                await OfferPortalToRejoiners();
                await FightHere(EngageRadius);
                await Task.Delay(300);
            }
        }
        finally
        {
            D2NG.Core.Tracing.TraceEvents.Wait(Leader.Game, "short-handed", false);
        }

        _shortHanded = false;
        var still = Leader.InGame && Leader.Game.Me != null ? ShortOf() : [];
        Log.Information("The party {Outcome} after holding {Seconds:0}s", still.Count == 0 ? "is whole again" : $"goes on without {string.Join(", ", still.Select(member => member.Name))}",
            (DateTime.UtcNow - since).TotalSeconds);
    }

    /// <summary>Back with the party: within this of the leader.</summary>
    private const double ShortHandedNear = 20;

    /// <summary>Nine in ten chickened members were back through the portal within 19 s, and three in four had Battle Orders 10 s after.</summary>
    private static readonly TimeSpan ShortHandedHold = TimeSpan.FromSeconds(45);

    public Point PullBackTo { get; private set; }

    private DateTime _pullBackUntil = DateTime.MinValue;

    private DateTime _pulledBackAt = DateTime.MinValue;

    private const double RangedBehind = 7;

    private const double BehindArrived = 3;

    /// <summary>
    /// Where a ranged member steps to while monsters are near the leader: a few steps behind him, on the far side from them.
    /// Null when nothing is near or where it stands will do.
    /// </summary>
    private Point BehindTheLeader(PartyMember member, Point leaderAt)
    {
        var near = NPCHelpers.GetNearbyNPCs(Leader.Client, leaderAt, 30, (int)EngageRadius)
            .Where(npc => !LevelingCombat.Untargetable.Contains(npc.NPCCode)).ToList();
        var bosses = near.Select(npc => (At: npc.Location, Keep: combat.KeepFromBoss(member, npc)))
            .Where(boss => boss.Keep != null).Select(boss => (boss.At, boss.Keep.Value)).ToList();
        var monsters = near.Select(npc => npc.Location).ToList();
        return MustStepBehind(member.Game.Me.Location, leaderAt, monsters, bosses) ? BehindSpot(leaderAt, monsters, bosses) : null;
    }

    private const double TooCloseToShoot = 8;

    /// <summary>
    /// Whether a ranged member has to leave where it stands: a monster is on it, a boss is nearer than it keeps, or it stands
    /// nearer the pack than the leader. The spot behind him moves with every step he and the pack take, and walking to it
    /// before every arrow took a quarter of the amazon's fighting time: 1154 runs between 649 shots in one Chaos run.
    /// </summary>
    public static bool MustStepBehind(Point me, Point leaderAt, IReadOnlyList<Point> monsters, IReadOnlyList<(Point At, double Keep)> bosses)
    {
        if (monsters.Count == 0)
        {
            return false;
        }

        if (monsters.Any(monster => monster.Distance(me) < TooCloseToShoot) || bosses.Any(boss => boss.At.Distance(me) < boss.Keep))
        {
            return true;
        }

        var pack = new Point((ushort)monsters.Average(monster => monster.X), (ushort)monsters.Average(monster => monster.Y));
        return me.Distance(pack) < leaderAt.Distance(pack);
    }

    private const double BossKeepMargin = 2;

    private const double MostBehind = 30;

    /// <summary>
    /// <see cref="RangedBehind"/> behind the leader on the far side from the monsters, further back while a boss is nearer
    /// than the member keeps from it; null when nothing is near or no spot up to <see cref="MostBehind"/> is clear. Seven
    /// behind the paladin hammering Diablo was nine from him: the amazon and the sorceress stepped out and walked back
    /// in turn through whole Diablo fights, the amazon without one arrow at him in five runs.
    /// </summary>
    public static Point BehindSpot(Point leaderAt, IReadOnlyList<Point> monsters, IReadOnlyList<(Point At, double Keep)> bosses)
    {
        if (monsters.Count == 0)
        {
            return null;
        }

        var dx = leaderAt.X - monsters.Average(monster => monster.X);
        var dy = leaderAt.Y - monsters.Average(monster => monster.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            return null;
        }

        for (var back = RangedBehind; back <= MostBehind; back++)
        {
            var spot = new Point((ushort)(leaderAt.X + dx / length * back), (ushort)(leaderAt.Y + dy / length * back));
            if (bosses.All(boss => boss.At.Distance(spot) >= boss.Keep + BossKeepMargin))
            {
                return spot;
            }
        }

        return null;
    }

    private const int CorridorRadius = 6;

    private const double CorridorWalkable = 0.35;

    /// <summary>Whether the leader stands in a narrow hall: little of the ground around him can be walked on.</summary>
    private async Task<bool> InCorridor(Point at)
    {
        var map = await mapApiService.GetArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area);
        if (map == null)
        {
            return false;
        }

        var walkable = 0;
        var total = 0;
        for (var dx = -CorridorRadius; dx <= CorridorRadius; dx++)
        {
            for (var dy = -CorridorRadius; dy <= CorridorRadius; dy++)
            {
                total++;
                if (map.IsNavigable(new Point((ushort)(at.X + dx), (ushort)(at.Y + dy))))
                {
                    walkable++;
                }
            }
        }

        return walkable < total * CorridorWalkable;
    }

    private Point _lastPackCentre;

    private bool _pullBackGivenUp;

    private const double SamePackDistance = 20;

    private static readonly TimeSpan PullBackGiveUp = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PullBackCooldown = TimeSpan.FromSeconds(20);

    private const int BigPack = 8;

    private const double BigPackRadius = 20;

    private const double PullBackDistance = 25;

    private const double PullBackReach = 60;

    private const double PullBackArrived = 6;

    private static readonly TimeSpan PullBackHold = TimeSpan.FromSeconds(6);

    /// <summary>
    /// On the way to a boss every room is cleared before the next: pushing on with monsters alive pulled
    /// Andariel's whole level onto the party. Set while a boss is searched for.
    /// </summary>
    public bool ClearRoomByRoom { get; set; }

    /// <summary>
    /// On the way to a boss the party is there for the boss, not the levels: only what stands in the way is fought.
    /// </summary>
    public bool Rushing { get; set; }

    private const double RushEngageRadius = 8;

    private static readonly TimeSpan ClearLimit = TimeSpan.FromSeconds(180);

    /// <summary>How far around the leader seen monsters count as part of the room being cleared.</summary>
    private const double RoomRadius = 30;

    /// <summary>The last place the leader stood with nothing left alive around it.</summary>
    public Point LastQuietSpot { get; private set; }

    /// <summary>Every cleared spot since the boss hunt began, oldest first.</summary>
    private readonly List<Point> _quietSpots = [];

    /// <summary>
    /// How far from the boss the spot it is drawn back to has to be. The latest cleared spot was 15 from Andariel,
    /// inside her own room, and the party fought her there with her lair around it.
    /// </summary>
    private const double LureBackDistance = 40;

    /// <summary>The area the cleared spots belong to: they outlive a trip to town and back, not a move to another level.</summary>
    private Area _quietSpotsArea;

    private async Task RecordQuietSpot(Point spot)
    {
        // Only ground of the party's own level: just inside the Chaos Sanctuary the leader still stood on the River of
        // Flame's side, the party was pulled back there, and the members inside could not path across the border - 95
        // empty paths in five seconds - while two fought the entrance alone.
        if (!await pathingService.IsNavigatablePointInArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area, spot))
        {
            return;
        }

        if (_quietSpotsArea != Area)
        {
            _quietSpots.Clear();
            _quietSpotsArea = Area;
        }

        LastQuietSpot = spot;
        if (_quietSpots.Count == 0 || _quietSpots[^1].Distance(spot) > 8)
        {
            _quietSpots.Add(spot);
        }
    }

    /// <summary>The most recent cleared spot well away from the boss, or the first one when none is far enough.</summary>
    private Point LureSpotFor(Point boss)
        => _quietSpots.LastOrDefault(spot => spot.Distance(boss) >= LureBackDistance) ?? _quietSpots.FirstOrDefault() ?? LastQuietSpot;
    private static readonly TimeSpan PushOnAfterGivingUp = TimeSpan.FromSeconds(20);

    public async Task WaitForParty(double distance)
    {
        var leaderAt0 = Leader.Game.Me.Location;
        if (Followers.Where(member => member.InGame && member.Mode == FollowMode.Follow).All(member => member.Game.Me.Location.Distance(leaderAt0) <= distance))
        {
            return;
        }

        D2NG.Core.Tracing.TraceEvents.Wait(Leader.Game, "party", true);
        try
        {
            await WaitForPartyCore(distance);
        }
        finally
        {
            D2NG.Core.Tracing.TraceEvents.Wait(Leader.Game, "party", false);
        }
    }

    private async Task WaitForPartyCore(double distance)
    {
        await GeneralHelpers.TryWithTimeout(async _ =>
        {
            var leaderAt = Leader.Game.Me.Location;
            var away = Followers.Where(member => member.InGame && member.Mode == FollowMode.Follow && member.Game.Me.Location.Distance(leaderAt) > distance).ToList();
            if (away.Count == 0)
            {
                return true;
            }

            // A member held in a fight is helped, not waited for: the barbarian whirled through a pack of Doom Knights 33
            // yards from the paladin for ten seconds while the other three stood waiting for him.
            if (away.FirstOrDefault(InAFight) is { } fighting)
            {
                await WalkTo(Leader, leaderAt.GetPointBeforePointInSameDirection(fighting.Game.Me.Location, HelpDistance), TimeSpan.FromSeconds(2));
                await FightHere(EngageRadius);
                return false;
            }

            await Task.Delay(200);
            return false;
        }, GatherLimit);
    }

    private const double HelpDistance = 6;

    private static bool InAFight(PartyMember member)
        => !member.Game.IsInTown() && NPCHelpers.GetNearbyNPCs(member.Client, member.Game.Me.Location, 10, (int)EngageRadius)
            .Any(npc => !LevelingCombat.Untargetable.Contains(npc.NPCCode));

    /// <summary>
    /// Walks the leader along <paramref name="path"/>, fighting and gathering the party every few points. A
    /// stretch it did not finish - a narrow cave, something in the way - is not skipped: the rest is pathed
    /// afresh from where the leader stands, until it is at the destination.
    /// </summary>
    private const double LeftAreaJump = 150;

    public async Task<bool> Advance(List<Point> path, Func<bool> stop = null)
    {
        if (path.Count == 0)
        {
            return true;
        }

        var destination = path[^1];
        var remaining = path;
        for (var stretches = 0; stretches < MaxStretches; stretches++)
        {
            if (!await LeaderPresent() || StopRequested() || RegroupRequested || stop?.Invoke() == true)
            {
                return false;
            }

            if (Leader.Game.Me.Location.Distance(destination) <= ArrivedDistance)
            {
                break;
            }

            await FightHere(Rushing && !ClearRoomByRoom ? RushEngageRadius : EngageRadius);
            TrackMonsters();
            if (ClearRoomByRoom)
            {
                await ClearSeenNear(RoomRadius);
            }

            await WaitForParty(GatherDistance);
            await OfferPortalToRejoiners();
            await BetweenStretches();
            await HoldForTheShort();
            if (!await LeaderPresent() || StopRequested() || RegroupRequested)
            {
                return false;
            }

            var stretch = remaining.Take(StretchPoints).ToList();
            // Doors the stretch runs through are opened before they are reached; the ones beside the path are left
            // shut, opening every door in reach cost a click and a wait for each.
            await OpenDoorsNear(Leader, 15, door => stretch.Any(point => point.Distance(door.Location) <= 5));
            remaining = remaining.Skip(StretchPoints).ToList();
            if (stretch.Count > 0)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await MovementHelpers.TakePathOfLocations(Leader.Game, stretch, MovementMode.Walking, cancel.Token);
            }

            // Stairs and warps on the way move the leader into another area's coordinates; the walk is over then,
            // and the caller decides whether that is where it wanted to be.
            if (stretch.Count > 0 && Leader.Game.Me.Location.Distance(stretch[^1]) > LeftAreaJump
                && !await pathingService.IsNavigatablePointInArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area, Leader.Game.Me.Location))
            {
                Log.Information("{Character} left {Area} on the way, now at {Location}", Leader.Name, Area, Leader.Game.Me.Location);
                return true;
            }

            var behind = stretch.Count == 0 || Leader.Game.Me.Location.Distance(stretch[^1]) > StretchMissed;
            if (behind && await OpenDoorsNear(Leader, 8))
            {
                remaining = [.. stretch, .. remaining];
                continue;
            }

            if (behind && Leader.Game.Me.Location.Distance(destination) > ArrivedDistance)
            {
                remaining = await pathingService.GetPathToLocation(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area, Leader.Game.Me.Location, destination, MovementMode.Walking);
                if (remaining.Count == 0)
                {
                    Log.Warning("{Character} found no path on from {Location} to {Destination}", Leader.Name, Leader.Game.Me.Location, destination);
                    break;
                }
            }
        }

        await FightHere(EngageRadius);
        return Leader.Game.Me.Location.Distance(destination) <= ArrivedDistance * 2;
    }

    public async Task<bool> WalkTo(Point destination, Func<bool> stop = null)
    {
        // An unset point is no destination: the sorceress asked for paths to (0, 0) three times and stood idle after.
        if (destination == null || (destination.X == 0 && destination.Y == 0))
        {
            Log.Warning("{Character} was sent to an unset point; not walking", Leader.Name);
            return false;
        }

        var path = await pathingService.GetPathToLocation(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area, Leader.Game.Me.Location, destination, MovementMode.Walking);
        if (path.Count == 0)
        {
            // Silent, this left every Chaos Sanctuary seal "not opened" without a step taken toward any of them.
            Log.Warning("{Character} found no path in {Area} from {From} to {To}", Leader.Name, Area, Leader.Game.Me.Location, destination);
            return false;
        }

        return await Advance(path, stop);
    }

    /// <summary>
    /// The walk stopped short of the border: something the map grid does not show stands in the way. Tries
    /// walkable ground on the far side around the crossing, nearest first, until the leader is across.
    /// </summary>
    private async Task NudgeAcross(Point crossing, Area target)
    {
        foreach (var radius in new[] { 6, 12, 18 })
        {
            for (var step = 0; step < 8; step++)
            {
                var angle = step * Math.PI / 4;
                var candidate = new Point((ushort)(crossing.X + radius * Math.Cos(angle)), (ushort)(crossing.Y + radius * Math.Sin(angle)));
                if (!await pathingService.IsNavigatablePointInArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, target, candidate))
                {
                    continue;
                }

                await OpenDoorsNear(Leader, 15);
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await MovementHelpers.MoveToLocation(Leader.Game, pathingService, mapApiService, candidate, MovementMode.Walking, cancel.Token);
                Leader.Game.RequestUpdate(Leader.Game.Me.Id);
                await Task.Delay(300);
                if (await pathingService.IsNavigatablePointInArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, target, Leader.Game.Me.Location))
                {
                    Log.Information("The leader got into {Target} by way of {Candidate}, {Radius} from the crossing", target, candidate, radius);
                    return;
                }
            }
        }

        Log.Warning("The leader found no way across into {Target} around {Crossing}", target, crossing);
    }

    /// <summary>The whole party crosses into the neighbouring <paramref name="target"/> together.</summary>
    /// <param name="knownPath">A path to the crossing worked out by the caller, for borders the generic search cannot find.</param>
    public async Task<bool> EnterArea(Area target, List<Point> knownPath = null)
    {
        var from = Area;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var path = attempt == 0 && knownPath != null ? knownPath : await traveler.PathIntoAdjacent(Leader.Client, from, target, attempt);
            if (path == null)
            {
                await Task.Delay(1000);
                continue;
            }

            await Advance(path);
            if (RegroupRequested)
            {
                return false;
            }

            // The position is stale for a moment after walking, so ask the server before deciding the leader is across.
            if (path.Count > 0 && !await traveler.ConfirmInArea(Leader.Client, target, 2))
            {
                await NudgeAcross(path[^1], target);
            }

            await WaitForParty(10);
            foreach (var member in Followers)
            {
                member.Mode = FollowMode.Off;
            }

            try
            {
                var leaderAt = Leader.Game.Me.Location;
                var crossed = await Task.WhenAll(members.Where(member => member.InGame).Select(async member =>
                {
                    if (!member.IsLeader && member.Game.Me.Location.Distance(leaderAt) > 6)
                    {
                        // Pathing does not cross a border, so a follower takes the leader's own path and then steps across to it.
                        var start = path.Count == 0 ? 0 : path.Select((point, index) => (point, index))
                            .MinBy(pair => pair.point.Distance(member.Game.Me.Location)).index;
                        if (start < path.Count - 1)
                        {
                            await MovementHelpers.TakePathOfLocations(member.Game, path.Skip(start).ToList(), MovementMode.Walking);
                        }

                        await member.Game.MoveToAsync(leaderAt);
                        await Task.Delay(400);
                    }

                    // A gate often stands right at a warp or border, past where the path ends.
                    await OpenDoorsNear(member, 15);
                    return await traveler.FinishCrossing(member.Client, from, target);
                }));
                if (crossed.All(done => done))
                {
                    ForgetMonsters();
                    Area = target;
                    Log.Information("Party entered {Area}", target);
                    return true;
                }

                Log.Warning("Not everyone crossed from {From} into {Target}: {Who}", from, target,
                    string.Join(", ", members.Where((_, index) => index < crossed.Length && !crossed[index]).Select(member => member.Name)));
                if (crossed[members.IndexOf(Leader)])
                {
                    Area = target;
                    return true;
                }
            }
            finally
            {
                foreach (var member in Followers)
                {
                    member.Mode = FollowMode.Follow;
                }
            }
        }

        return false;
    }

    public static Waypoint? WaypointIn(Area area) => (int)area < 256 && Enum.IsDefined(typeof(Waypoint), (byte)area) ? (Waypoint)(byte)area : null;

    private static readonly Dictionary<Act, Waypoint> TownWaypoints = new()
    {
        [Act.Act1] = Waypoint.RogueEncampment,
        [Act.Act2] = Waypoint.LutGholein,
        [Act.Act3] = Waypoint.KurastDocks,
        [Act.Act4] = Waypoint.ThePandemoniumFortress
    };

    /// <summary>
    /// The client only learns which waypoints a character holds from a waypoint menu, so every game starts
    /// with each member opening the town's. Without it every waypoint step ran again in every new game.
    /// </summary>
    public async Task LearnWaypoints()
    {
        var town = TownWaypoints[Area.MapToAct()];
        // Every member walks to the waypoint and reads it at the same time. The leader walking there first and then
        // waiting for the others added half of the thirty seconds this took at the start of every game.
        foreach (var member in Followers)
        {
            member.Mode = FollowMode.Off;
        }

        try
        {
            await LearnWaypointsTogether(town);
        }
        finally
        {
            foreach (var member in Followers)
            {
                member.Mode = FollowMode.Follow;
            }
        }

        Log.Information("Waypoints held: {Held}", string.Join("; ", members.Select(member => $"{member.Name} {member.Game.Me.AllowedWaypoints.Count}")));
    }

    private async Task LearnWaypointsTogether(Waypoint town)
    {
        await Task.WhenAll(members.Where(member => member.InGame).Select(async member =>
        {
            // Each walks there itself: a member still at a merchant cannot even see the waypoint.
            var path = await pathingService.GetPathToObject(member.Game.MapId, AreaTraveler.MapDifficulty, Area, member.Game.Me.Location, town.ToEntityCode(), MovementMode.Walking);
            if (path.Count > 0 && member.Game.GetEntityByCode(town.ToEntityCode()).All(entity => entity.Location.Distance(member.Game.Me.Location) > 8))
            {
                await MovementHelpers.TakePathOfLocations(member.Game, path, MovementMode.Walking);
            }

            var target = member.Game.GetEntityByCode(town.ToEntityCode()).OrderBy(entity => entity.Location.Distance(member.Game.Me.Location)).FirstOrDefault();
            if (target == null)
            {
                return;
            }

            if (target.Location.Distance(member.Game.Me.Location) > 4)
            {
                await MovementHelpers.MoveToWorldObject(member.Game, pathingService, mapApiService, target, MovementMode.Walking);
            }

            for (var attempt = 0; attempt < 3 && member.Game.ReadWaypoints(target) == null; attempt++)
            {
                await MovementHelpers.MoveToWorldObject(member.Game, pathingService, mapApiService, target, MovementMode.Walking);
                await member.Game.MoveToAsync(target);
                await Task.Delay(300);
            }
        }));
    }

    /// <summary>Whether every member travelling with the party holds the waypoint; one waiting in town is not travelling.</summary>
    public bool AllHold(Waypoint waypoint)
        => members.Where(member => member.InGame && !(member.Mode == FollowMode.Off && member.Game.IsInTown()))
            // A list not read yet - just rejoined - is unknown, not empty: it ruled out every farming spot after a drop.
            .All(member => member.Game.Me.AllowedWaypoints.IsEmpty || member.Game.Me.AllowedWaypoints.Contains(waypoint));

    /// <summary>Walks the leader to the waypoint in the party's area and has every member that lacks it open it.</summary>
    public async Task<bool> TouchWaypoint()
    {
        if (WaypointIn(Area) is not { } waypoint)
        {
            return false;
        }

        if (AllHold(waypoint))
        {
            return true;
        }

        if (!await WalkToObject(waypoint.ToEntityCode()))
        {
            Log.Warning("The party could not reach the {Waypoint} waypoint", waypoint);
            return false;
        }

        // A hit interrupts operating an object: in Jail 1 all four stood on the waypoint for ten tries each while
        // monsters hit them, and none got a menu.
        await FightHere(EngageRadius + 10);
        await WaitForParty(8);
        await Task.WhenAll(members.Where(member => member.InGame && !member.Game.Me.AllowedWaypoints.Contains(waypoint)).Select(async member =>
        {
            var target = member.Game.GetEntityByCode(waypoint.ToEntityCode()).OrderBy(entity => entity.Location.Distance(member.Game.Me.Location)).FirstOrDefault();
            if (target == null && Leader.Game.GetEntityByCode(waypoint.ToEntityCode()).FirstOrDefault() is { } seenByLeader)
            {
                // Too far behind to have it streamed in: walk to where the leader sees it.
                await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, seenByLeader.Location, MovementMode.Walking);
                GeneralHelpers.TryWithTimeout(_ => (target = member.Game.GetEntityByCode(waypoint.ToEntityCode()).FirstOrDefault()) != null, TimeSpan.FromSeconds(3));
            }

            if (target == null)
            {
                Log.Information("{Character} cannot see the {Waypoint} waypoint from {Location}", member.Name, waypoint, member.Game.Me.Location);
                return;
            }

            if (target.Location.Distance(member.Game.Me.Location) > 4)
            {
                await MovementHelpers.MoveToWorldObject(member.Game, pathingService, mapApiService, target, MovementMode.Walking);
            }

            for (var attempt = 0; attempt < 6 && !member.Game.Me.AllowedWaypoints.Contains(waypoint); attempt++)
            {
                if (attempt > 0 && member.IsLeader)
                {
                    await FightHere(EngageRadius + 10);
                }

                // The click only counts from right beside it, and the client's position lags: in Jail 1 everyone
                // believed it stood on the waypoint while the server had it 5 away, and ten clicks each were ignored.
                await ComeAlongside(member, target);
                if (member.Game.ReadWaypoints(target) != null)
                {
                    break;
                }

                // A first click on a waypoint nobody has used yet only starts it; the menu comes from the next click.
                var activating = GeneralHelpers.TryWithTimeout(_ => target.State != EntityState.Alive, TimeSpan.FromSeconds(1));
                Log.Information("{Character} got no waypoint menu on try {Attempt}, {Distance:0.0} from it{Activation}", member.Name, attempt + 1,
                    target.Location.Distance(member.Game.Me.Location), activating ? ", it is activating" : "");
                await Task.Delay(activating ? 1500 : 300);
            }

            Log.Information("{Character} {Outcome} the {Waypoint} waypoint", member.Name,
                member.Game.Me.AllowedWaypoints.Contains(waypoint) ? "now holds" : "did NOT get", waypoint);
        }));
        return AllHold(waypoint);
    }

    /// <summary>Gets the member within reach of <paramref name="target"/> by a position the server confirmed.</summary>
    private async Task ComeAlongside(PartyMember member, WorldObject target)
    {
        var game = member.Game;
        for (var step = 0; step < 3; step++)
        {
            game.RequestUpdate(game.Me.Id);
            await Task.Delay(300);
            var distance = target.Location.Distance(game.Me.Location);
            if (distance <= 2.5)
            {
                return;
            }

            if (distance > 12)
            {
                await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, target, MovementMode.Walking);
            }

            await game.MoveToAsync(target.Location);
            await Task.Delay(500);
        }
    }

    /// <summary>Everyone takes the waypoint they stand near to <paramref name="destination"/>; the party is then there.</summary>
    /// <param name="leaderSuffices">The leader through is enough: the members without this waypoint follow by portal.</param>
    public async Task<bool> TakeWaypoint(Waypoint destination, bool leaderSuffices = false)
    {
        var here = WaypointIn(Area) ?? (TownWaypoints.TryGetValue(Area.MapToAct(), out var town) ? town : null);
        if (here == null || !await WalkToObject(here.Value.ToEntityCode()))
        {
            return false;
        }

        await WaitForParty(8);
        foreach (var member in Followers)
        {
            member.Mode = FollowMode.Off;
        }

        try
        {
            var taken = await Task.WhenAll(members.Where(member => member.InGame).Select(async member =>
            {
                // Taking a waypoint the character does not hold throws, and took the whole bot down; a member without
                // it stays behind and follows by portal.
                if (!member.Game.Me.AllowedWaypoints.IsEmpty && !member.Game.Me.AllowedWaypoints.Contains(destination))
                {
                    return false;
                }

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var target = member.Game.GetEntityByCode(here.Value.ToEntityCode()).OrderBy(entity => entity.Location.Distance(member.Game.Me.Location)).FirstOrDefault();
                    if (target == null)
                    {
                        await Task.Delay(500);
                        continue;
                    }

                    if (target.Location.Distance(member.Game.Me.Location) > 4)
                    {
                        await MovementHelpers.MoveToWorldObject(member.Game, pathingService, mapApiService, target, MovementMode.Walking);
                    }

                    bool tookIt;
                    try
                    {
                        tookIt = member.Game.TakeWaypoint(target, destination);
                    }
                    catch (InvalidOperationException exception)
                    {
                        Log.Warning("{Character} cannot take the waypoint to {Destination}: {Reason}", member.Name, destination, exception.Message);
                        return false;
                    }

                    if (tookIt && await traveler.ConfirmInArea(member.Client, destination.ToArea()))
                    {
                        return true;
                    }
                }

                return false;
            }));
            if (taken.All(done => done) || (leaderSuffices && taken[members.Where(member => member.InGame).ToList().IndexOf(Leader)]))
            {
                ForgetMonsters();
                Area = destination.ToArea();
                Log.Information("Party took the waypoint to {Area}{Missing}", Area, taken.All(done => done) ? "" : " (not everyone holds it)");
                await Task.Delay(500);
                return true;
            }

            Log.Warning("Not everyone took the waypoint to {Destination}", destination);
            return false;
        }
        finally
        {
            foreach (var member in Followers)
            {
                member.Mode = FollowMode.Follow;
            }
        }
    }

    /// <summary>
    /// The Compelling Orb stands on the council's platform: the path ended 15.5 from it on one map and 21 on another, and
    /// the step failed both times. Far enough still to catch a stop well short of the object, Jail 1's 183 behind doors.
    /// </summary>
    private const double ObjectReached = 25;

    public async Task<bool> WalkToObject(EntityCode code)
    {
        // Standing on a waypoint, pathing comes back empty: its tile is unwalkable in the map grid.
        if (Leader.Game.GetEntityByCode(code).Any(entity => entity.Location.Distance(Leader.Game.Me.Location) < 8))
        {
            return true;
        }

        // A path to an object the map cannot reach - behind a closed door - ends at the nearest reachable point,
        // and arriving there is not arriving at the object: in Jail 1 the leader stopped 183 short of the
        // waypoint, behind doors, and the step counted it as reached. Only seeing it close by counts.
        bool Near(double distance) => Leader.Game.GetEntityByCode(code).Any(entity => entity.Location.Distance(Leader.Game.Me.Location) <= distance);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var path = await pathingService.GetPathToObject(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area, Leader.Game.Me.Location, code, MovementMode.Walking);
            if (path.Count == 0 && attempt < 3 && !Near(ObjectReached))
            {
                // Straight after a portal the client still reports the old area's position and no path comes back:
                // the Summoner step gave up half a second into the Arcane Sanctuary. The position is synced and the
                // path asked for again.
                await traveler.SyncPosition(Leader.Client, Area);
                await Task.Delay(700);
                continue;
            }

            if (path.Count == 0)
            {
                return Near(ObjectReached);
            }

            await Advance(path);
            if (Near(ObjectReached) || RegroupRequested || StopRequested())
            {
                break;
            }

            await OpenDoorsNear(Leader, 15);
            if (!await StepThroughDoor(code))
            {
                Log.Warning("{Leader} stopped at {Location} short of {Code} with no door in reach", Leader.Name, Leader.Game.Me.Location, code);
                break;
            }
        }

        var reached = Near(ObjectReached);
        if (!reached)
        {
            var nearest = Leader.Game.GetEntityByCode(code).Select(entity => entity.Location.Distance(Leader.Game.Me.Location)).DefaultIfEmpty(-1).Min();
            Log.Warning("{Leader} did not reach {Code} in {Area}: stopped at {Location}, {Distance:0} from it (-1: not in sight)", Leader.Name, code, Area,
                Leader.Game.Me.Location, nearest);
        }

        return reached;
    }

    /// <summary>
    /// The map draws door tiles as wall, so no path ever runs through one, open or not. The leader walks
    /// through the open door nearest it by hand - onto it, then a few steps on towards the object - and the
    /// next path starts on the far side.
    /// </summary>
    private async Task<bool> StepThroughDoor(EntityCode code)
    {
        var game = Leader.Game;
        var map = await mapApiService.GetArea(game.MapId, AreaTraveler.MapDifficulty, Area);
        if (map?.Objects?.GetValueOrDefault((int)code)?.FirstOrDefault() is not { } goal)
        {
            return false;
        }

        var door = game.WorldObjects.Where(entry => entry.Key.Item2 == EntityType.Object).Select(entry => entry.Value)
            .Where(entity => DoorCodes.Contains((int)entity.Code) && entity.State != EntityState.Alive && entity.Location.Distance(game.Me.Location) <= 20)
            .OrderBy(entity => entity.Location.Distance(goal) + entity.Location.Distance(game.Me.Location)).FirstOrDefault();
        if (door == null)
        {
            return false;
        }

        var length = Math.Max(1, door.Location.Distance(goal));
        var past = new Point((ushort)(door.Location.X + (goal.X - door.Location.X) * 6 / length), (ushort)(door.Location.Y + (goal.Y - door.Location.Y) * 6 / length));
        await game.MoveToAsync(door.Location);
        await Task.Delay(400);
        await game.MoveToAsync(past);
        await Task.Delay(400);
        Log.Information("{Leader} stepped through the door at {Door} towards {Goal}, now at {Location}", Leader.Name, door.Location, goal, game.Me.Location);
        await WaitForParty(GatherDistance);
        return true;
    }

    /// <summary>The area a step last sent the party home from in this game; its portals are not taken back into it.</summary>
    public Area? FledFrom { get; set; }

    /// <summary>
    /// Gets the party to <paramref name="destination"/>: by the held waypoint nearest to it when that saves
    /// walking, then on foot, opening every waypoint on the way.
    /// </summary>
    public async Task<bool> GoTo(Area destination)
    {
        if (Area == destination)
        {
            return true;
        }

        var walking = (await traveler.Route(Leader.Client, destination)).Count;
        var candidates = new List<string>();
        Waypoint? best = null;
        // No walking route at all - another act - is as far as it gets, not zero: any waypoint beats it. Counted as zero,
        // nothing could, and the party in act 4 never reached the act 3 spots it was sent to farm.
        var bestHops = walking == 0 ? int.MaxValue : walking;
        foreach (var waypoint in Enum.GetValues<Waypoint>().Where(waypoint => waypoint.ToArea().MapToAct() == destination.MapToAct() && waypoint.ToArea() != Area && AllHold(waypoint)))
        {
            // A waypoint in the destination itself is the best there is: its route is empty.
            var hops = waypoint.ToArea() == destination ? 1 : (await pathingService.GetAreaRoute(Leader.Game.MapId, AreaTraveler.MapDifficulty, waypoint.ToArea(), destination)).Count;
            candidates.Add($"{waypoint} {hops}");
            // Compared as hops plus the waypoint itself on both sides: storing the bare hops made the next candidate
            // need two fewer, and the party took the Great Marsh waypoint over the Flayer Jungle's one hop closer.
            // A tie goes to the waypoint: the hop is nearly instant, and walking out of the Pandemonium Fortress for Izual
            // - three areas against the City of the Damned's waypoint and two - stuck at the fortress's edge.
            if (hops > 0 && hops + 1 <= bestHops)
            {
                best = waypoint;
                bestHops = hops + 1;
            }
        }

        Log.Information("Way to {Destination} from {Area}: walking {Walking} areas, waypoints {Candidates}, chose {Best}", destination, Area, walking,
            string.Join(", ", candidates), best?.ToString() ?? "none");

        // An open town portal - another player's too - that leads into the destination's act and saves walking beats
        // the waypoint: a player opening one at Andariel's level spares the whole walk down the Catacombs. Not the one the
        // party fled through: it leads back into the pack it fled from, with nobody's Battle Orders - in the Chaos Sanctuary
        // three of four chickened within thirty seconds of stepping through, attempt after attempt.
        if (Leader.Game.IsInTown() && await PortalCloserThan(destination, best == null ? walking : bestHops - 1) is { } portal
            && portal.TownPortalArea != FledFrom)
        {
            var into = portal.TownPortalArea;
            if (await StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(entity => entity.Id == portal.Id), into))
            {
                Log.Information("The party took a town portal into {Area} on its way to {Destination}", into, destination);
                best = null;
            }
        }

        if (best != null && (Leader.Game.IsInTown() || WaypointIn(Area) != null) && !await TakeWaypoint(best.Value))
        {
            Log.Warning("Taking the waypoint to {Waypoint} failed; walking instead", best);
        }

        var route = await traveler.Route(Leader.Client, destination);
        foreach (var next in route.Skip(1))
        {
            if (StopRequested())
            {
                return false;
            }

            if (!await EnterArea(next))
            {
                Log.Error("The party could not get from {From} into {Next}", Area, next);
                return false;
            }

            if (WaypointIn(next) is { } passed && !AllHold(passed))
            {
                await TouchWaypoint();
            }
        }

        return Area == destination;
    }

    /// <summary>A town portal standing in town whose far side is fewer areas from <paramref name="destination"/> than <paramref name="hops"/>.</summary>
    private async Task<WorldObject> PortalCloserThan(Area destination, int hops)
    {
        foreach (var portal in Leader.Game.GetEntityByCode(EntityCode.TownPortal)
            .Where(portal => portal.TownPortalArea != Area.None && portal.TownPortalArea.MapToAct() == destination.MapToAct()
                && portal.TownPortalArea != Leader.Game.Act.MapTownArea()))
        {
            var portalHops = portal.TownPortalArea == destination ? 1
                : (await pathingService.GetAreaRoute(Leader.Game.MapId, AreaTraveler.MapDifficulty, portal.TownPortalArea, destination)).Count;
            if (portalHops > 0 && portalHops < hops)
            {
                return portal;
            }
        }

        return null;
    }

    /// <summary>Back to town by the nearest waypoint: the one here, or the next one on the way home.</summary>
    public async Task<bool> GoHome()
    {
        var town = TownWaypoints[Area.MapToAct()];
        if (Leader.Game.IsInTown())
        {
            Area = town.ToArea();
            return true;
        }

        // What lies around is picked up before leaving: Diablo's four rares and a magic bow lay where he fell on 20 Nov 2026
        // while the party went home six seconds after his death (the user's rule: any boss or kill, before going home).
        await LootBeforeLeaving();
        if (await PortalHome(town.ToArea()))
        {
            Area = town.ToArea();
            return true;
        }

        // Tristram has no level link: its only way out is the Cairn Stones' portal back to the Stony Field.
        if (Area == Area.Tristram && (!await WalkToObject(EntityCode.RedTownPortal)
            || !await StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.RedTownPortal)
                .OrderBy(portal => portal.Location.Distance(member.Game.Me.Location)).FirstOrDefault(), Area.StonyFields)))
        {
            return false;
        }

        var route = await traveler.Route(Leader.Client, town.ToArea());
        var stop = route.FirstOrDefault(area => WaypointIn(area) is { } waypoint && AllHold(waypoint));
        if (stop == Area.None || stop == town.ToArea())
        {
            return await TravelTo(town.ToArea());
        }

        return await GoTo(stop) && await TakeWaypoint(town);
    }

    /// <summary>The leader opens a town portal from its tome and everyone steps through it.</summary>
    private static int PortalCharges(PartyMember member)
        => member.Game.Inventory.Items.FirstOrDefault(item => item.Name == ItemName.TomeOfTownPortal)?.Amount is { } amount ? (int)amount : 0;

    /// <summary>
    /// The leader, or the member beside it with the most charges when its tome is empty: rejoin portals and trips to town
    /// together ran the leader's twenty charges out in Hell, and the party walked up three levels of the Flayer Dungeon.
    /// </summary>
    private PartyMember PortalCaster()
        => PortalCharges(Leader) > 0 ? Leader
            : members.Where(member => member.InGame && !member.Game.IsInTown() && PortalCharges(member) > 0
                    && member.Game.Me.Location.Distance(Leader.Game.Me.Location) <= 20)
                .OrderByDescending(PortalCharges).FirstOrDefault();

    private async Task<bool> PortalHome(Area town)
    {
        var caster = PortalCaster();
        if (caster == null)
        {
            Log.Warning("Nobody near {Leader} has a town portal charge left", Leader.Name);
            return false;
        }

        await WaitForParty(10);
        if (!await CastPortal(caster))
        {
            Log.Warning("{Caster} could not open a town portal", caster.Name);
            return false;
        }

        var owner = caster.Game.Me.Id;
        var home = await StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.TownPortal)
            .Where(portal => portal.TownPortalOwnerId == owner).MaxBy(portal => portal.Id), town);
        Log.Information("The party {Outcome} home through {Caster}'s portal", home ? "went" : "did NOT all go", caster.Name);
        return home;
    }

    /// <summary>
    /// The leader opens a town portal where a boss hunt starts, so a member that chickened comes straight back
    /// through it instead of walking down from the nearest waypoint.
    /// </summary>
    public async Task OpenRejoinPortal()
    {
        // Every member opens one: a member that chickened finds a portal back whoever else is still standing.
        await Task.WhenAll(members.Where(member => member.InGame && !member.Game.IsInTown()
            && member.Game.Inventory.Items.Any(item => item.Name == ItemName.TomeOfTownPortal && item.Amount > 0)).Select(async member =>
        {
            var opened = await CastPortal(member);
            Log.Information("{Character} {Outcome} a town portal in {Area} for members that rejoin", member.Name, opened ? "opened" : "could NOT open", Area);
        }));
    }

    /// <summary>
    /// Clears room by room towards <paramref name="target"/> and stops <paramref name="stopShort"/> from it: the rooms
    /// before a boss emptied, so the restock trip and the lure back have somewhere quiet to land.
    /// </summary>
    /// <returns>True when the boss came out to the party on the way, so the hunt starts at once.</returns>
    public async Task<bool> ClearTowards(Point target, double stopShort, NPCCode boss, Point lair, double lairRadius)
    {
        ClearRoomByRoom = true;
        // Going back for monsters seen near the boss pulled the party into Andariel's room before the restock trip.
        _keepAwayFrom = lair;
        _keepAwayRadius = lairRadius;
        // The boss counts as hunted already: when it comes out, the clearing gives way to drawing it back. Without
        // this the party fought Andariel where she met it.
        _huntedBoss = boss;
        combat.FacedLast = boss;
        try
        {
            await WalkTo(target, () => Leader.Game.Me.Location.Distance(target) <= stopShort || BossTooClose());
            if (!BossTooClose())
            {
                await FightHere(EngageRadius);
                await ClearSeenNear(RoomRadius);
            }

            var came = BossTooClose();
            Log.Information("Cleared the way to {Location}, {Distance:0} from {Target}{Boss}", Leader.Game.Me.Location, Leader.Game.Me.Location.Distance(target), target,
                came ? $"; {boss} came out" : "");
            return came;
        }
        finally
        {
            ClearRoomByRoom = false;
            _keepAwayFrom = null;
            _huntedBoss = null;
            combat.FacedLast = null;
        }
    }

    private Point _keepAwayFrom;
    private double _keepAwayRadius;

    /// <summary>
    /// Through the leader's portal to town, <paramref name="inTown"/> there, and back through the same portal: a
    /// restock right before a boss, so the party meets it with full belts and fresh antidotes.
    /// </summary>
    public async Task<bool> TownTrip(Func<Task> inTown)
    {
        var field = Area;
        var town = TownWaypoints[field.MapToAct()].ToArea();
        if (PortalCaster() is not { } caster)
        {
            Log.Warning("Nobody near {Leader} has a town portal charge for a restock trip", Leader.Name);
            return false;
        }

        await WaitForParty(10);
        if (!await CastPortal(caster))
        {
            Log.Warning("{Caster} could not open a town portal for a restock trip", caster.Name);
            return false;
        }

        var owner = caster.Game.Me.Id;
        if (!await StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.TownPortal)
            .Where(portal => portal.TownPortalOwnerId == owner).MaxBy(portal => portal.Id), town))
        {
            return false;
        }

        var arrival = Leader.Game.Me.Location;
        await inTown();
        using (var back = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            await MovementHelpers.MoveToLocation(Leader.Game, pathingService, mapApiService, arrival, MovementMode.Walking, back.Token);
        }

        var returned = await StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.TownPortal)
            .FirstOrDefault(portal => portal.TownPortalOwnerId == owner && portal.TownPortalArea == field), field);
        Log.Information("The party {Outcome} to {Area} after restocking in town", returned ? "went back" : "did NOT get back", field);
        return returned;
    }

    /// <summary>
    /// Cast again for a few seconds as the cow bot does: the first cast right after stepping through a portal or
    /// another action is often not answered, and three of four rejoin portals were missing for it.
    /// </summary>
    public static Task<bool> CastPortalFor(PartyMember member) => CastPortal(member);

    private static async Task<bool> CastPortal(PartyMember member)
        => await GeneralHelpers.TryWithTimeout(async _ => await member.Game.CreateTownPortal(), TimeSpan.FromSeconds(3.5));

    /// <summary>How close a party member's portal has to stand to the leader to bring a rejoining member back to the party.</summary>
    private const double NearbyPortal = 40;

    private DateTime _rejoinPortalAt = DateTime.MinValue;
    private Area _rejoinPortalArea;

    /// <summary>
    /// A member back in town after a chicken comes back through a portal the leader opens where the party is, rather
    /// than by waypoint and on foot: the amazon walked the Flayer Dungeon alone to catch up.
    /// </summary>
    private async Task OfferPortalToRejoiners()
    {
        // Out of town on the server's word: right after the crossing out of Lut Gholein the client's own area still read
        // the field, and the leader opened a "rejoin" portal in town.
        // Not gated on the tome's count: the client did not see the paladin's portal tome refilled, read it empty, and opened no
        // portal at all for a game while it still held scrolls. A cast that fails says so, and another member tries.
        if (!Leader.Game.IsOutOfTownConfirmed() || !members.Any(member => member.InGame && !member.IsLeader && member.Game.IsInTown())
            || (_rejoinPortalArea == Area && DateTime.UtcNow - _rejoinPortalAt < TimeSpan.FromSeconds(10)))
        {
            return;
        }

        // A party member's portal still standing close by does the job; a new one only where the party has moved on.
        var partyIds = members.Where(member => member.InGame).Select(member => member.Game.Me.Id).ToHashSet();
        if (Leader.Game.GetEntityByCode(EntityCode.TownPortal).Any(portal => partyIds.Contains(portal.TownPortalOwnerId)
            && portal.Location.Distance(Leader.Game.Me.Location) <= NearbyPortal))
        {
            return;
        }

        _rejoinPortalAt = DateTime.UtcNow;
        _rejoinPortalArea = Area;
        var leaderAt = Leader.Game.Me.Location;
        var casters = members.Where(member => member.InGame && member.Game.Me != null && member.Game.IsOutOfTownConfirmed()
                && member.Game.Me.Location.Distance(leaderAt) <= NearbyPortal
                && member.Game.Inventory.Items.Any(item => item.Name == ItemName.TomeOfTownPortal))
            .OrderBy(member => member.IsLeader ? 0 : 1).ToList();
        foreach (var caster in casters)
        {
            if (await CastPortal(caster))
            {
                Log.Information("{Caster} opened a town portal in {Area} for a member back in town", caster.Name, Area);
                return;
            }
        }

        Log.Information("Nobody could open a town portal in {Area} for a member back in town ({Tried} tried)", Area, casters.Count);
    }

    /// <summary>A member in town steps through any town portal that leads into <paramref name="area"/>.</summary>
    public async Task<bool> TakePortalInto(PartyMember member, Area area) => await ThroughPortalInto(member, area) != null;

    /// <summary>
    /// Where a member that went through a portal came out: <paramref name="area"/>, or - <paramref name="beside"/>, for a
    /// town portal out of town - the level next to it when the portal's far end stands across their border. A town portal
    /// leads to the level its caster stood in by the server's word: one cast at the Chaos Sanctuary's edge put the leader
    /// four yards inside the River of Flame, the party counted itself in the Sanctuary, looked for a way from the
    /// Sanctuary into the Sanctuary three times, and the Diablo step went home. Null while it is still where it was.
    /// </summary>
    private async Task<Area?> CameOutIn(PartyMember member, Area area, bool beside)
    {
        if (await traveler.ConfirmInArea(member.Client, area, attempts: 4))
        {
            return area;
        }

        if (!beside || area.IsTown())
        {
            return null;
        }

        var at = await traveler.CurrentArea(member.Client);
        if (at == area)
        {
            return area;
        }

        if (at.IsTown() || at.MapToAct() != area.MapToAct()
            || (await pathingService.GetAreaRoute(member.Game.MapId, AreaTraveler.MapDifficulty, at, area)).Count != 2)
        {
            return null;
        }

        Log.Information("{Character} came out of the portal into {Area} across the border, in {Beside} at {Location}", member.Name, area, at, member.Game.Me.Location);
        return at;
    }

    /// <summary><see cref="TakePortalInto"/>, giving the area the member came out in (<see cref="CameOutIn"/>).</summary>
    private async Task<Area?> ThroughPortalInto(PartyMember member, Area area)
    {
        var game = member.Game;
        WorldObject portal = null;
        GeneralHelpers.TryWithTimeout(_ => (portal = PortalInto(member, area)) != null, TimeSpan.FromSeconds(2));
        // Portals stand at the town's portal spot, out of sight of the waypoint where a member left behind waits: the
        // orb sorceress stood in Lut Gholein while three portals into the Canyon went unseen. It walks to where members
        // come out of portals in this town, or where it entered the game, and looks again.
        if (portal == null && (_townPortalSpots.GetValueOrDefault(game.Act) ?? KnownTownPortalSpots.GetValueOrDefault(game.Act) ?? _spawnSpots.GetValueOrDefault(member.Name)) is { } spot
            && spot.Distance(game.Me.Location) > 10)
        {
            using (var walk = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                await MovementHelpers.MoveToLocation(game, pathingService, mapApiService, spot, MovementMode.Walking, walk.Token);
            }

            GeneralHelpers.TryWithTimeout(_ => (portal = PortalInto(member, area)) != null, TimeSpan.FromSeconds(3));
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Looked for again each time: one that closed since - its owner went back through it - is not clicked at again.
            if (attempt > 0)
            {
                portal = PortalInto(member, area);
            }

            if (portal == null)
            {
                return null;
            }

            if (portal.Location.Distance(game.Me.Location) > 3)
            {
                await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, portal, MovementMode.Walking);
            }

            // The party turned for home on the way to the portal.
            if (HomeBound)
            {
                return null;
            }

            game.InteractWithEntity(portal);
            if (await CameOutIn(member, area, beside: true) is { } landed)
            {
                return landed;
            }
        }

        return null;
    }

    /// <summary>
    /// A town portal into <paramref name="area"/>: the leader's first, which stays open until the leader goes last, then the
    /// member's own; another member's closes under it when that member goes through first.
    /// </summary>
    private WorldObject PortalInto(PartyMember member, Area area)
    {
        var game = member.Game;
        var leaderId = game.Players.FirstOrDefault(player => player.Name.Equals(Leader.Name, StringComparison.OrdinalIgnoreCase))?.Id;
        return game.GetEntityByCode(EntityCode.TownPortal).Where(candidate => candidate.TownPortalArea == area)
            .OrderBy(candidate => candidate.TownPortalOwnerId == leaderId ? 0 : candidate.TownPortalOwnerId == game.Me.Id ? 1 : 2)
            .FirstOrDefault();
    }

    /// <summary>How long a portal's owner waits in town for the others to go through it first.</summary>
    private static readonly TimeSpan OwnerWaits = TimeSpan.FromSeconds(5);

    /// <summary>Where members came out of a portal in each act's town: where the portals into the field stand.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Act, Point> _townPortalSpots = new();

    /// <summary>
    /// Towns with a fixed layout, where the portals always stand in one place: Lut Gholein's, seen in every act 2 log,
    /// is ninety yards from where a member enters the game.
    /// </summary>
    private static readonly Dictionary<Act, Point> KnownTownPortalSpots = new() { [Act.Act2] = new Point(5168, 5057) };

    /// <summary>Where each member stood on entering the game, in town.</summary>
    private readonly Dictionary<string, Point> _spawnSpots = members.Where(member => member.InGame && member.Game.IsInTown()
            && member.Game.Me.Location is { X: > 0, Y: > 0 })
        .ToDictionary(member => member.Name, member => member.Game.Me.Location);

    /// <summary>Every member steps through the portal <paramref name="portal"/> finds for it, into <paramref name="destination"/>.</summary>
    public async Task<bool> StepThroughPortal(Func<PartyMember, WorldObject> portal, Area destination)
    {
        await WaitForParty(8);
        foreach (var member in Followers)
        {
            member.Mode = FollowMode.Off;
        }

        try
        {
            var intoTown = TownWaypoints[destination.MapToAct()].ToArea() == destination;
            Area? leaderLanded = null;
            var going = members.Where(member => member.InGame).ToList();
            // Out of town a portal's owner goes after the others: a town portal closes once its owner goes back through it,
            // and the amazon, a second behind the paladin into his, clicked at nothing until she left the game.
            var owners = intoTown ? new List<PartyMember>() : going.Where(member => member.Game.IsInTown() && portal(member) is { } target && target.TownPortalOwnerId == member.Game.Me.Id).ToList();
            var made = new System.Collections.Concurrent.ConcurrentDictionary<PartyMember, bool>();
            var first = going.Except(owners).Select(async member => made[member] = await Step(member)).ToList();
            if (owners.Count > 0)
            {
                // Not for long: the others would stand in the field without them.
                await Task.WhenAny(Task.WhenAll(first), Task.Delay(OwnerWaits));
            }

            await Task.WhenAll(first.Concat(owners.Select(async member => made[member] = await Step(member))));

            if (made.GetValueOrDefault(Leader))
            {
                ForgetMonsters();
                Area = leaderLanded ?? destination;
                Log.Information("Party stepped through a portal into {Area}{Missing}", Area,
                    made.Values.All(done => done) ? "" : " (not everyone made it)");
                return true;
            }

            return false;

            async Task<bool> Step(PartyMember member)
            {
                // Already there - back on its own after a chicken or a restock - it would take the leader's portal out
                // to the field instead.
                if (intoTown && member.Game.IsInTown())
                {
                    return true;
                }

                var fromTown = member.Game.IsInTown();
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var target = portal(member);
                    if (target == null)
                    {
                        await Task.Delay(500);
                        continue;
                    }

                    if (target.Location.Distance(member.Game.Me.Location) > 3)
                    {
                        await MovementHelpers.MoveToWorldObject(member.Game, pathingService, mapApiService, target, MovementMode.Walking);
                    }

                    member.Game.InteractWithEntity(target);
                    if (await CameOutIn(member, destination, beside: fromTown) is { } landed)
                    {
                        if (member.Game.IsInTown())
                        {
                            _townPortalSpots[member.Game.Act] = member.Game.Me.Location;
                        }

                        if (member == Leader)
                        {
                            leaderLanded = landed;
                        }

                        return true;
                    }
                }

                return false;
            }
        }
        finally
        {
            foreach (var member in Followers)
            {
                member.Mode = FollowMode.Follow;
            }
        }
    }

    private WorldObject FindBoss(NPCCode boss)
        => members.Where(member => member.InGame)
            .SelectMany(member => member.Game.WorldObjects.Where(entry => entry.Key.Item2 == EntityType.NPC && entry.Value.NPCCode == boss).Select(entry => entry.Value))
            .FirstOrDefault();

    /// <summary>Searches the area for <paramref name="boss"/> and fights until it is dead.</summary>
    private readonly HashSet<NPCCode> _lured = [];

    /// <summary>Called once when the hunted boss first comes close: the moment for antidotes and the like.</summary>
    public Action<NPCCode> BossInSight { get; set; }

    private DateTime _backedOffAt = DateTime.MinValue;
    private bool _bossSighted;

    /// <summary>
    /// The leader gives ground when its life keeps sinking: a few yards back from the boss and a few seconds for the
    /// potions to work, then back in. Standing still, it drank every few seconds and chickened anyway.
    /// </summary>
    private async Task<bool> BackOff(WorldObject boss)
    {
        var me = Leader.Game.Me;
        if (me.MaxLife <= 0 || (double)me.Life / me.MaxLife >= BackOffBelow || DateTime.UtcNow - _backedOffAt < TimeSpan.FromSeconds(6))
        {
            return false;
        }

        _backedOffAt = DateTime.UtcNow;
        var here = me.Location;
        var length = Math.Max(1, here.Distance(boss.Location));
        var away = new Point((ushort)(here.X + (here.X - boss.Location.X) * 10 / length), (ushort)(here.Y + (here.Y - boss.Location.Y) * 10 / length));
        Log.Information("{Leader} backs off from {Boss} at {Life} of {Max} life", Leader.Name, boss.NPCCode, me.Life, me.MaxLife);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await MovementHelpers.MoveToLocation(Leader.Game, pathingService, mapApiService, away, MovementMode.Walking, cancel.Token);
        await Task.Delay(3000);
        return true;
    }

    private const double BackOffBelow = 0.4;
    private NPCCode? _huntedBoss;

    /// <summary>
    /// A boss all but dead is worth staying for: the leader had Andariel at 2% and left at 24%. Checked wherever the
    /// party fights during a hunt, not once in the boss loop - the end of a fight is often in the clearing fight.
    /// </summary>
    /// <summary>True while a nearly dead boss holds the members in at the finishing chicken.</summary>
    public bool Finishing => _finishing;

    private void UpdateFinishingChicken()
    {
        var finishing = _huntedBoss is { } hunted && FindBoss(hunted) is { } boss && boss.State is not (EntityState.Dead or EntityState.Dieing)
            && boss.LifePercentage <= FinishBossBelow;
        // With the boss close a member low on life steps away, drinks and comes back rather than leaving: chickening at 35%
        // in the Diablo fight sent the whole party home. The step-out starts at 45%, which leaves it room.
        double? chicken = finishing ? FinishingChicken : _bossSighted ? BossFightChicken : null;
        if (chicken != _bossChicken)
        {
            _bossChicken = chicken;
            foreach (var member in members)
            {
                Chicken.ChickenService.OverrideLifeChicken(member.Client, chicken);
            }
        }

        if (finishing == _finishing)
        {
            return;
        }

        _finishing = finishing;

        if (finishing)
        {
            Log.Information("{Boss} is nearly dead; the party stays in down to {Chicken:P0} life", _huntedBoss, FinishingChicken);
        }
    }

    private bool _finishing;

    private const double FinishBossBelow = 15;
    private const double FinishingChicken = 0.12;
    private const double BossFightChicken = 0.25;
    private double? _bossChicken;

    private static readonly TimeSpan FinishingGrace = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan EveryoneInTownWait = TimeSpan.FromSeconds(90);

    /// <summary>Until every member is in game, in town and restocked, so the party goes back in at full strength.</summary>
    public async Task WaitForEveryoneInTown(TimeSpan limit)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < limit && !members.All(member => member.InGame && member.Game.IsInTown() && member.Mode == FollowMode.Follow))
        {
            await Task.Delay(1000);
        }

        Log.Information("{Ready} of {Count} members are in town to go back in together", members.Count(member => member.InGame && member.Game.IsInTown()), members.Count);
    }

    // Diablo comes once every seal is cleared, so there is nothing left to draw him away from: luring him kept the leader
    // walking back and waiting for 80% of a five-minute fight, hammering for 10%.
    // Izual too: in Hell he was under 15% and the party walked back to its lure spot instead of finishing him.
    private static readonly HashSet<NPCCode> NotLured = [NPCCode.Summoner, NPCCode.Mephisto, NPCCode.Diablo, NPCCode.Izual];

    /// <summary>How close a boss comes before the party draws it back to the cleared spot.</summary>
    private const double LureDistance = 25;

    /// <summary>True while a boss kill runs: a restock trip then would turn back a party already at the boss.</summary>
    public volatile bool InBossFight;

    public async Task<bool> KillBoss(NPCCode boss, TimeSpan limit)
    {
        InBossFight = true;
        ClearRoomByRoom = true;
        _huntedBoss = boss;
        combat.FacedLast = LevelingCombat.FocusFirst.Contains(boss) ? null : boss;
        _bossSighted = false;
        _lured.Clear();
        // The cleared spots from the way in are kept: wiped here, the only spot left after the restock trip was where
        // the party stood, ten yards from Andariel, and she was "drawn back" to herself.
        await RecordQuietSpot(Leader.Game.Me.Location);
        try
        {
            await OpenRejoinPortal();
            return await KillBossCore(boss, limit);
        }
        finally
        {
            InBossFight = false;
            ClearRoomByRoom = false;
            _finishing = false;
            _bossChicken = null;
            foreach (var member in members)
            {
                Chicken.ChickenService.OverrideLifeChicken(member.Client, null);
            }
            _huntedBoss = null;
            combat.FacedLast = null;
        }
    }

    private async Task<bool> KillBossCore(NPCCode boss, TimeSpan limit)
    {
        await SettleInArea();
        var started = DateTime.UtcNow;
        var searches = 0;
        Point lastSeen = null;
        // A boss nearly dead gets a little longer: Nightmare Diablo was under 15% when the five minutes ran out, and the
        // party left after twenty minutes of seals.
        while ((DateTime.UtcNow - started < limit || (_finishing && DateTime.UtcNow - started < limit + FinishingGrace))
            && await LeaderPresent() && !RegroupRequested)
        {
            if (TownTripRequested && RestockInTown != null)
            {
                TownTripRequested = false;
                Log.Information("Only {Count} of the party are left fighting {Boss}; everyone goes to town and comes back together",
                    members.Count(member => member.InGame && !member.Game.IsInTown()), boss);
                var away = DateTime.UtcNow;
                if (!await FleeToTown(async () =>
                    {
                        await RestockInTown();
                        await WaitForEveryoneInTown(EveryoneInTownWait);
                    }))
                {
                    RegroupRequested = true;
                    break;
                }

                // The time in town is not fight time.
                started += DateTime.UtcNow - away;
                continue;
            }

            var found = FindBoss(boss);
            if (found == null && lastSeen != null)
            {
                // Seen already, out of sight because the party drew back: wait for it at the lure spot, then edge
                // towards where it was. Counting this as a failed search walked the party to Andariel and back
                // until it gave up on her.
                var waited = DateTime.UtcNow;
                D2NG.Core.Tracing.TraceEvents.Wait(Leader.Game, "lure", true);
                while (DateTime.UtcNow - waited < TimeSpan.FromSeconds(12) && FindBoss(boss) == null && await LeaderPresent() && !RegroupRequested)
                {
                    UpdateFinishingChicken();
                    if (!await combat.FightTick(Leader, Leader.Game.Me.Location, EngageRadius + 10))
                    {
                        await Task.Delay(300);
                    }
                }

                D2NG.Core.Tracing.TraceEvents.Wait(Leader.Game, "lure", false);
                if (FindBoss(boss) == null)
                {
                    var here = Leader.Game.Me.Location;
                    var halfway = new Point((ushort)((here.X + lastSeen.X) / 2), (ushort)((here.Y + lastSeen.Y) / 2));
                    await WalkTo(halfway, () => FindBoss(boss) != null);
                }

                continue;
            }

            if (found == null)
            {
                // Searched whenever the boss is not in sight, not once up front: a glimpse of it skipped the search
                // and the next look found nothing, which gave up on Andariel three seconds into her level.
                if (searches++ >= 2)
                {
                    Log.Warning("{Boss} is nowhere to be seen in {Area} after {Searches} searches", boss, Area, searches - 1);
                    return false;
                }

                await SearchForBoss(boss, limit - (DateTime.UtcNow - started));
                continue;
            }

            if (found.State is EntityState.Dead or EntityState.Dieing)
            {
                Log.Information("{Boss} is dead after {Seconds:0}s", boss, (DateTime.UtcNow - started).TotalSeconds);
                return true;
            }

            lastSeen = found.Location;
            UpdateFinishingChicken();
            if (!_bossSighted && found.Location.Distance(Leader.Game.Me.Location) <= LureDistance)
            {
                _bossSighted = true;
                // The fight gets the whole limit: counted from the start, the walk through Durance 3 used four of
                // Mephisto's five minutes and the party gave up on him a minute into the fight.
                started = DateTime.UtcNow;
                BossInSight?.Invoke(boss);
            }

            // Lured, not chased: the party falls back to the last cleared spot and lets the boss come to it there,
            // away from the rooms it has not cleared yet. Within a few yards of that spot it simply fights.
            // Far off - seen through a door across rooms not cleared yet - the party clears its way towards it first,
            // opening doors as it goes; drawing back the moment it was in sight left the room before Andariel full.
            if (found.Location.Distance(Leader.Game.Me.Location) > LureDistance)
            {
                await ClearSeenNear(RoomRadius);
                var approach = Leader.Game.Me.Location.GetPointBeforePointInSameDirection(found.Location, found.Location.Distance(Leader.Game.Me.Location) - LureDistance + 5);
                await WalkTo(approach, () => FindBoss(boss) is { } near && near.Location.Distance(Leader.Game.Me.Location) <= LureDistance);
                continue;
            }

            // Bosses that do not follow are walked up to, not drawn back: the Summoner hardly moves, and Mephisto stays
            // behind his moat - the party waited at the lure spot for five minutes and never hit him once.
            var lure = NotLured.Contains(boss) ? null : LureSpotFor(found.Location);
            if (lure != null && Leader.Game.Me.Location.Distance(lure) > 6 && found.Location.Distance(lure) > 10)
            {
                if (_lured.Add(boss))
                {
                    Log.Information("{Boss} seen at {Location}; drawing it back to {Lure}", boss, found.Location, lure);
                }

                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await MovementHelpers.MoveToLocation(Leader.Game, pathingService, mapApiService, lure, MovementMode.Walking, cancel.Token);
            }

            if (await BackOff(found))
            {
                continue;
            }

            await combat.FightTick(Leader, Leader.Game.Me.Location, EngageRadius + 10);
            await BetweenStretches();
        }

        return false;
    }

    /// <summary>
    /// Waits until the leader's position is a walkable spot of the party's area. Straight after a crossing it can still
    /// be the last area's: every path from it fails at once, and the Izual hunt "swept" the Plains of Despair - 170
    /// stops in six seconds - without taking a step, then gave up.
    /// </summary>
    public async Task SettleInArea()
    {
        for (var wait = 0; wait < 20 && Leader.InGame && !await pathingService.IsNavigatablePointInArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area, Leader.Game.Me.Location); wait++)
        {
            // The server is asked every fourth look only: asking on every one sent the paladin's position requests in
            // bursts of dozens a minute.
            if (wait % 4 == 0)
            {
                await traveler.SyncPosition(Leader.Client, Area);
            }

            await Task.Delay(300);
        }
    }

    /// <summary>Most bosses stand at a preset the map knows: walk there first, and sweep the level only when it is not there.</summary>
    private async Task SearchForBoss(NPCCode boss, TimeSpan limit)
    {
        await traveler.SyncPosition(Leader.Client, Area);
        var map = await mapApiService.GetArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area);
        if (map?.Npcs?.GetValueOrDefault((int)boss)?.FirstOrDefault() is { } preset)
        {
            Log.Information("Walking from {From} to {Boss}'s place at {Preset} in {Area}", Leader.Game.Me.Location, boss, preset, Area);
            await WalkTo(preset, () => FindBoss(boss) is { } seen && seen.Location.Distance(Leader.Game.Me.Location) <= LureDistance);
            if (FindBoss(boss) != null)
            {
                return;
            }
        }

        await Sweep(25, () => Task.FromResult(FindBoss(boss) != null), limit);
    }

    public async Task<bool> TravelTo(Area destination)
    {
        if (Area == destination)
        {
            return true;
        }

        var route = await traveler.Route(Leader.Client, destination);
        if (route.Count == 0)
        {
            Log.Error("No route from {From} to {Destination}", Area, destination);
            return false;
        }

        foreach (var next in route.Skip(1))
        {
            if (!await EnterArea(next))
            {
                Log.Error("The party could not get from {From} into {Next}", Area, next);
                return false;
            }
        }

        return true;
    }

    /// <summary>Walks a tour through the whole of the current area, until <paramref name="done"/> says to stop.</summary>
    /// <param name="hunt">Go back for every monster seen alive afterwards: for a quest that needs the area empty.</param>
    public async Task<bool> Sweep(int spacing, Func<Task<bool>> done, TimeSpan limit, bool hunt = false)
    {
        await SettleInArea();
        var map = await mapApiService.GetArea(Leader.Game.MapId, AreaTraveler.MapDifficulty, Area);
        var walkable = map.Map.Select(row => row.Select(AreaMapExtensions.IsMovable).ToArray()).ToList();
        var here = Leader.Game.Me.Location;
        var tour = ExplorationTour.Plan(walkable, (here.X - map.LevelOrigin.X, here.Y - map.LevelOrigin.Y), spacing);
        Log.Information("Sweeping {Area}: {Stops} stops", Area, tour.Count);
        var stops = tour.Select(stop => new Point((ushort)(map.LevelOrigin.X + stop.Column), (ushort)(map.LevelOrigin.Y + stop.Row))).ToList();
        var started = DateTime.UtcNow;
        var walked = 0;
        while (stops.Count > 0)
        {
            if (await done() || DateTime.UtcNow - started > limit || !await LeaderPresent())
            {
                break;
            }

            var next = stops[0];
            stops.RemoveAt(0);
            walked++;
            // Every stop the leader passes within sight of is seen already; walking to it again is
            // the same corridor twice.
            await WalkTo(next, () =>
            {
                var at = Leader.Game.Me.Location;
                stops.RemoveAll(stop => stop.Distance(at) <= SightRadius);
                return false;
            });
        }

        Log.Information("Swept {Area}: walked to {Walked} of {Stops} stops in {Seconds:0}s", Area, walked, tour.Count, (DateTime.UtcNow - started).TotalSeconds);
        if (hunt && !await done())
        {
            await HuntRemaining(done, limit - (DateTime.UtcNow - started));
        }

        return await done();
    }
}
