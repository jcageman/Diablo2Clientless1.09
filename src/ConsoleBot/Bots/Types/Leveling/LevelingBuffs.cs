using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Keeps the buffs a build has points in running outside town: the armors, Holy Shield, and the barbarian's
/// shouts, which cover everyone standing near him. Low-level casters lived or died by Frozen Armor and never
/// cast it.
/// </summary>
public static class LevelingBuffs
{
    private static readonly (Skill Skill, EntityEffect Effect)[] Buffs =
    [
        (Skill.ChillingArmor, EntityEffect.ChillingArmor),
        (Skill.ShiverArmor, EntityEffect.Shiverarmor),
        (Skill.FrozenArmor, EntityEffect.Frozenarmor),
        (Skill.BoneArmor, EntityEffect.Bonearmor),
        (Skill.HolyShield, EntityEffect.Holyshield),
        (Skill.Shout, EntityEffect.Shout),
        (Skill.BattleOrders, EntityEffect.BattleOrders),
        (Skill.BattleCommand, EntityEffect.Battlecommand),
    ];

    private static readonly ConcurrentDictionary<(string, Skill), DateTime> LastCast = new();
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

    private const double ShoutReach = 10;

    private const int StepFromBarbarian = 4;

    private static readonly (int, int)[] StepOffsets = [(4, 0), (-4, 0), (0, 4), (0, -4)];

    /// <summary>
    /// When each member's Battle Orders runs out, counted from a cast it is known to have taken: the server says only on or
    /// off, and nothing at all when a cast renews it.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> BattleOrdersUntil = new();

    /// <summary>
    /// Renewed this long before it runs out. A renewal reaches only the members beside the barbarian, and in a fight they
    /// stand apart: with twenty seconds' margin it ran out on members who never came close in time.
    /// </summary>
    private static readonly TimeSpan RecastBefore = TimeSpan.FromSeconds(60);

    /// <summary>A member out of reach without it, or with less than this left, is fetched: the barbarian walks over to it.</summary>
    private static readonly TimeSpan FetchBefore = TimeSpan.FromSeconds(20);

    /// <summary>Members further than this from the barbarian are elsewhere - catching up, in another area - and are not fetched.</summary>
    private const double FetchReach = 35;

    private static readonly TimeSpan FetchWalk = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The server drops a cast made while he whirls or stands stunned: in Hell's River of Flame he cast for himself five
    /// times in ten seconds mid-whirl, none of them landed, and each was counted as good for six minutes.
    /// </summary>
    private static readonly EntityEffect[] CastDropped = [EntityEffect.Skillmove, EntityEffect.Uninterruptable, EntityEffect.Stunned];

    private static readonly TimeSpan WhirlEnds = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The barbarian's mana is the receipt for a renewal, which sends no state packet: the cast cost 7 at level 20, shown
    /// half a second later, and regeneration gives back one a tick meanwhile.
    /// </summary>
    private const int CastManaSeen = 4;

    private static readonly TimeSpan Landing = TimeSpan.FromSeconds(1.5);

    /// <summary>Under this share of its new maximum a member that just got Battle Orders drinks before it fights.</summary>
    private const double TopUpBelow = 0.7;

    /// <summary>One shout at a time: the barbarian's own loop and the fight's upkeep both keep it up.</summary>
    private static readonly SemaphoreSlim OneShout = new(1, 1);

    /// <summary>
    /// When a cast was last tried for each member. The wait is per member, not per barbarian: a cast for the others while
    /// a rejoined member was still in town held its own off for ten seconds once it reached the party.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> LastTriedFor = new();

    /// <summary>
    /// A member that just came through a waypoint takes no shout: the first cast at the Far Oasis waypoint reached only the
    /// barbarian, every time, and the others went ten seconds without it. A member still lacking it is tried again soon.
    /// </summary>
    private static readonly TimeSpan PartyRetry = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan MissedBackOff = TimeSpan.FromSeconds(12);

    private const int BattleOrdersMana = 10;

    /// <summary>A potion refills over five seconds: another before that only queues behind it.</summary>
    private static readonly TimeSpan ManaPotionForOrders = TimeSpan.FromSeconds(5);

    private static readonly Lazy<D2NG.Core.SkillData.SkillTable> Skills = new(() =>
        D2NG.Core.GameDataLocation.TryResolve(D2NG.Core.GameDataLocation.SkillsFile, out var path) ? D2NG.Core.SkillData.SkillTable.Load(path) : null);

    /// <summary>
    /// Battle Orders' duration from skills.bin: Params 0 and 1 are frames at level 1 and per level after (4000 and 250 on
    /// 1.09, 160 s plus 10 s a level), 25 frames a second. Without the table, the 1.09 values.
    /// </summary>
    private static TimeSpan BattleOrdersDuration(int level)
    {
        var parameters = Skills.Value?.Skills.FirstOrDefault(skill => skill.Id == (int)Skill.BattleOrders)?.Params;
        var (first, perLevel) = parameters is { Length: >= 2 } ? (parameters[0], parameters[1]) : (4000, 250);
        return TimeSpan.FromSeconds((first + perLevel * Math.Max(0, level - 1)) / 25.0);
    }

    /// <summary>
    /// Battle Orders for the party, not only for the barbarian: his and the amazon's ran out apart in Hell's River of
    /// Flame, her life fell from 1370 to 714 and Urdar's next Smite of 908 killed her. A member without it, or with under a
    /// minute left, gets a fresh cast when it stands beside him; one out of reach is fetched once it has none or nearly none.
    /// </summary>
    public static async Task KeepUpForParty(PartyMember barbarian, Party party)
    {
        // Out of town by the server's word: leaving town, the client's area changes first and a cast in that moment
        // reached nobody - every game the barbarian cast twice ten seconds apart.
        if (!barbarian.InGame || barbarian.Game.Me == null || !barbarian.Game.IsOutOfTownConfirmed() || barbarian.SkillLevel(Skill.BattleOrders) <= 0
            || (Needing(barbarian, party.Members).Count == 0 && GuestsLacking(barbarian, party).Count == 0) || !await OneShout.WaitAsync(0))
        {
            return;
        }

        barbarian.Shouting = true;
        try
        {
            if (Needing(barbarian, party.Members).Count > 0)
            {
                await ShoutForParty(barbarian, party);
            }
            else
            {
                await ShoutForGuests(barbarian, GuestsLacking(barbarian, party));
            }
        }
        finally
        {
            barbarian.Shouting = false;
            OneShout.Release();
        }
    }

    private static async Task ShoutForParty(PartyMember barbarian, Party party)
    {
        var needing = Needing(barbarian, party.Members);
        var at = barbarian.Game.Me.Location;
        var near = needing.Where(member => member.Game.Me.Location.Distance(at) <= ShoutReach).ToList();
        if (near.Count == 0)
        {
            var fetch = needing.Where(member => Left(member, DateTime.UtcNow) < FetchBefore).ToList();
            if (fetch.Count == 0)
            {
                return;
            }

            // To where most of them stand together.
            var to = fetch.OrderByDescending(member => fetch.Count(other => other.Game.Me.Location.Distance(member.Game.Me.Location) <= ShoutReach))
                .ThenBy(member => member.Game.Me.Location.Distance(at)).First().Game.Me.Location;
            Log.Information("{Character} goes to {Members} at {Location} for Battle Orders", barbarian.Name, string.Join(", ", fetch.Select(member => member.Name)), to);
            await party.WalkTo(barbarian, to, FetchWalk);
            at = barbarian.Game.Me.Location;
            near = needing.Where(member => member.InGame && member.Game.Me != null && member.Game.Me.Location.Distance(at) <= ShoutReach).ToList();
            if (near.Count == 0)
            {
                foreach (var member in fetch)
                {
                    LastTriedFor[member.Name] = DateTime.UtcNow;
                }

                return;
            }
        }

        // Whirling dry, he had no 10 mana for it: members back from a rejoin fought the Chaos Sanctuary at half their life
        // and chickened again within seconds. A potion now, the cast on a later upkeep.
        if (barbarian.Game.Me.Mana < BattleOrdersMana)
        {
            if (DateTime.Now - barbarian.Game.LastUsedManaPotionTime > ManaPotionForOrders && barbarian.Game.UseManaPotion())
            {
                Log.Information("{Character} drinks a mana potion to cast Battle Orders for {Members}", barbarian.Name, string.Join(", ", near.Select(member => member.Name)));
            }

            return;
        }

        // His own loop holds its attacks while he shouts; the whirl already under way runs out first.
        if (!await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(100);
                return !CastDropped.Any(barbarian.Game.Me.Effects.ContainsKey);
            }, WhirlEnds))
        {
            return;
        }

        LastCast[(barbarian.Name, Skill.BattleOrders)] = DateTime.UtcNow;
        foreach (var member in near)
        {
            LastTriedFor[member.Name] = DateTime.UtcNow;
        }

        // Each of the others takes a step to beside him first. Just through a waypoint a character takes no shout until it
        // has moved: at the Far Oasis waypoint the first cast reached only the barbarian, every game, though all four stood
        // on the same spot. The step also brings a member that hung back into reach.
        var others = near.Where(member => member != barbarian).ToList();
        if (others.Count > 0)
        {
            // A real step: standing on the waypoint tile with him, a two-yard move was never sent at all.
            await Task.WhenAll(others.Select((member, index) =>
            {
                var from = member.Game.Me.Location;
                var (dx, dy) = from.Distance(at) < 2 ? StepOffsets[index % StepOffsets.Length] : (Math.Sign(from.X - at.X) * StepFromBarbarian, Math.Sign(from.Y - at.Y) * StepFromBarbarian);
                return member.Game.RunToAsync(new D2NG.Core.D2GS.Point((ushort)(at.X + dx), (ushort)(at.Y + dy)));
            }));
            await Task.Delay(700);
        }

        var got = await Shout(barbarian, near);
        var missed = near.Except(got).ToList();
        if (got.Count > 0)
        {
            await AlongWithOrders(barbarian);
            Log.Information("{Character} casts Battle Orders for {Members}, good until {Until:HH:mm:ss}", barbarian.Name,
                string.Join(", ", got.Select(member => member.Name)), BattleOrdersUntil.GetValueOrDefault(got[0].Name).ToLocalTime());
        }

        if (missed.Count > 0)
        {
            Log.Information("Battle Orders from {Character} did NOT reach {Members}{Why}", barbarian.Name, string.Join(", ", missed.Select(member => member.Name)),
                got.Count == 0 ? ": the cast spent no mana" : "");
            // Missed by a cast that landed: out of the party, or further than it looked. Fetching it again every three seconds
            // would walk the barbarian out of every fight.
            if (got.Count > 0)
            {
                foreach (var member in missed)
                {
                    LastTriedFor[member.Name] = DateTime.UtcNow + MissedBackOff;
                }
            }
        }
    }

    /// <summary>
    /// One cast of Battle Orders for <paramref name="members"/>, standing beside the barbarian, and the members it is known to
    /// have reached, whose countdown starts now. One without it shows it by its state; one that had it, by his mana falling -
    /// a cast while he whirled was dropped by the server and counted all the same.
    /// </summary>
    public static async Task<List<PartyMember>> Shout(PartyMember barbarian, IReadOnlyList<PartyMember> members)
    {
        var lacking = members.Where(member => member.InGame && Lacks(member)).ToList();
        var manaBefore = barbarian.Game.Me.Mana;
        barbarian.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, barbarian.Game.Me.Location);
        var cast = DateTime.UtcNow;
        var spent = false;
        await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await Task.Delay(100);
            spent |= barbarian.Game.Me.Mana <= manaBefore - CastManaSeen;
            return spent && lacking.All(member => !member.InGame || !Lacks(member));
        }, Landing);
        var landed = spent || lacking.Any(member => member.InGame && !Lacks(member));
        var got = members.Where(member => member.InGame && member.Game.Me != null && (lacking.Contains(member) ? !Lacks(member) : landed)).ToList();
        var until = cast + BattleOrdersDuration(barbarian.SkillLevel(Skill.BattleOrders));
        foreach (var member in got)
        {
            BattleOrdersUntil[member.Name] = until;
        }

        // A member that just got it - back from a chicken, healed in town at its smaller maximum - stands at about half its
        // new one: the barbarian came back at 1314 of 2457 and chickened again half a minute later. It drinks itself up first.
        // A rejuvenation only for one back from a chicken: they cannot be bought, and at a run's start all four stand there.
        foreach (var member in got.Where(member => lacking.Contains(member) && member.Game.Me.Life < member.Game.Me.MaxLife * TopUpBelow))
        {
            var drank = Returners.TryRemove(member.Name, out _) ? DrinkRejuvenation(member) || member.Game.UseHealthPotions() : member.Game.UseHealthPotions();
            Log.Information("{Character} {Outcome} at {Life} of {Max} life after Battle Orders", member.Name, drank ? "drinks up" : "has nothing to drink",
                member.Game.Me.Life, member.Game.Me.MaxLife);
        }

        return got;
    }

    private static readonly TimeSpan GuestRetry = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Players in the game that are not the bots - the user joining a run - beside the barbarian, in his party and without
    /// Battle Orders (the user's pick). Their states come in like any unit's; how long theirs lasts is not known, so they get
    /// a cast once it is gone.
    /// </summary>
    private static List<D2NG.Core.D2GS.Players.Player> GuestsLacking(PartyMember barbarian, Party party)
    {
        var bots = party.Members.Select(member => member.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var me = barbarian.Game.Me;
        var now = DateTime.UtcNow;
        return barbarian.Game.Players.Where(player => player.Id != me.Id && !bots.Contains(player.Name) && player.Location != null
                && player.Location.Distance(me.Location) <= ShoutReach && player.PartyId is { } partyId && partyId != NoParty && partyId == me.PartyId
                && !player.Effects.ContainsKey(EntityEffect.BattleOrders) && now - LastTriedFor.GetValueOrDefault(player.Name) >= GuestRetry)
            .ToList();
    }

    private const ushort NoParty = 0xFFFF;

    private static async Task ShoutForGuests(PartyMember barbarian, List<D2NG.Core.D2GS.Players.Player> guests)
    {
        if (guests.Count == 0 || barbarian.Game.Me.Mana < BattleOrdersMana
            || !await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(100);
                return !CastDropped.Any(barbarian.Game.Me.Effects.ContainsKey);
            }, WhirlEnds))
        {
            return;
        }

        foreach (var guest in guests)
        {
            LastTriedFor[guest.Name] = DateTime.UtcNow;
        }

        barbarian.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, barbarian.Game.Me.Location);
        LastCast[(barbarian.Name, Skill.BattleOrders)] = DateTime.UtcNow;
        await Task.Delay(WarcryGap);
        await AlongWithOrders(barbarian);
        Log.Information("{Character} casts Battle Orders for {Guests}, who are not bots: {Outcome}", barbarian.Name, string.Join(", ", guests.Select(guest => guest.Name)),
            guests.All(guest => guest.Effects.ContainsKey(EntityEffect.BattleOrders)) ? "taken" : "NOT taken by all");
    }

    /// <summary>A warcry's animation: the server drops a cast sent before the last one ends.</summary>
    private static readonly TimeSpan WarcryGap = TimeSpan.FromMilliseconds(700);

    /// <summary>Battle Command and Shout follow Battle Orders for whoever stands beside him (the user's pick).</summary>
    private static async Task AlongWithOrders(PartyMember barbarian)
    {
        foreach (var skill in new[] { Skill.BattleCommand, Skill.Shout })
        {
            if (barbarian.SkillLevel(skill) <= 0 || barbarian.Game.Me.Mana < BattleOrdersMana)
            {
                continue;
            }

            barbarian.Game.UseRightHandSkillOnLocation(skill, barbarian.Game.Me.Location);
            LastCast[(barbarian.Name, skill)] = DateTime.UtcNow;
            await Task.Delay(WarcryGap);
        }
    }

    /// <summary>Out of town within reach of a fetch, without Battle Orders or with under a minute of it left, and not just tried.</summary>
    private static List<PartyMember> Needing(PartyMember barbarian, IEnumerable<PartyMember> members)
    {
        var at = barbarian.Game.Me.Location;
        var now = DateTime.UtcNow;
        return members.Where(member => member.InGame && member.Game.Me != null && member.Game.IsOutOfTownConfirmed()
                && member.Game.Me.Location.Distance(at) <= FetchReach && Left(member, now) < RecastBefore
                && now - LastTriedFor.GetValueOrDefault(member.Name) >= PartyRetry)
            .ToList();
    }

    private static bool Lacks(PartyMember member) => !member.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders);

    /// <summary>What is left of a member's Battle Orders; none without it, and none when no cast it took is known.</summary>
    private static TimeSpan Left(PartyMember member, DateTime now)
        => Lacks(member) || !BattleOrdersUntil.TryGetValue(member.Name, out var until) ? TimeSpan.Zero : until - now;

    /// <summary>
    /// A member back from a rejoin has lost its Battle Orders: its countdown and its last try are dropped, so the first
    /// upkeep that finds it beside the barbarian out of town casts for it at once - whenever it gets there.
    /// </summary>
    public static void Rejoined(PartyMember member)
    {
        BattleOrdersUntil.TryRemove(member.Name, out _);
        LastTriedFor.TryRemove(member.Name, out _);
        Returners[member.Name] = true;
    }

    /// <summary>A rejuvenation from the bag, else from the belt, where the barbarian keeps his; a small one before a full one.</summary>
    private static bool DrinkRejuvenation(PartyMember member)
    {
        var game = member.Game;
        if (game.UseRejuvenationPotion())
        {
            return true;
        }

        if (game.Belt.GetRejuvenationPotions().OrderBy(item => item.Name == D2NG.Core.D2GS.Items.ItemName.FullRejuvenationPotion).FirstOrDefault() is not { } potion)
        {
            return false;
        }

        game.UseBeltItem(potion);
        return true;
    }

    /// <summary>Members back from a chicken whose first Battle Orders has not landed yet.</summary>
    private static readonly ConcurrentDictionary<string, bool> Returners = new();

    /// <summary>Every countdown belongs to its game: one kept from the last game made the first renewal of the next come late.</summary>
    public static void ForgetGame()
    {
        BattleOrdersUntil.Clear();
        LastTriedFor.Clear();
        Returners.Clear();
    }

    public static async Task KeepUp(PartyMember member)
    {
        if (!member.InGame || member.Game.IsInTown() || member.Game.Me.Life <= 0)
        {
            return;
        }

        var me = member.Game.Me;
        var hasAnArmor = false;
        foreach (var (skill, effect) in Buffs)
        {
            var isArmor = skill is Skill.ChillingArmor or Skill.ShiverArmor or Skill.FrozenArmor;
            if (me.Skills.GetValueOrDefault(skill) <= 0 || (isArmor && hasAnArmor))
            {
                continue;
            }

            hasAnArmor |= isArmor;
            if (me.Effects.ContainsKey(effect) || me.Mana < 10
                || DateTime.UtcNow - LastCast.GetValueOrDefault((member.Name, skill)) < Retry)
            {
                continue;
            }

            LastCast[(member.Name, skill)] = DateTime.UtcNow;
            member.Game.UseRightHandSkillOnLocation(skill, me.Location);
            Log.Debug("{Character} casts {Skill}", member.Name, skill);
            await Task.Delay(500);
        }
    }
}
