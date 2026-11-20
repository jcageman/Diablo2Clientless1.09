using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core;

/// <summary>A change to how <see cref="Game"/> reads a packet or a client action, switched on from its rules version on.</summary>
public enum ClientRule
{
    /// <summary>A monster stop (0x6D) states its life and so does an assign (0x56/0x57); before, both left the held life as it was.</summary>
    MonsterLifeFromStopsAndAssigns,

    /// <summary>A dying or dead monster's state (0x69) holds life 0; before, its direction byte was read as life, 0 to 49%.</summary>
    LifeZeroForDyingAndDead,

    /// <summary>
    /// An item taken out of the stash, cube or inventory goes on the cursor only if this client asked to lift it; before,
    /// every removal that was not a sale or a use did, and an item the server took (a quest hand-in) stayed there for good.
    /// </summary>
    CursorNeedsLift,

    /// <summary>
    /// Every lift not answered yet is remembered, so a lift answered after the next one was asked still puts its item on
    /// the cursor; before, only the last lift counted and the late item was believed gone while the server held it there.
    /// </summary>
    OutstandingLifts,

    /// <summary>
    /// A worn quiver's count follows the server's quantity updates (0x3E type 0x46) as a javelin's does, so the check
    /// that leaves a quiver unequipped with no arrows or bolts left off the cursor (unswitched, older than the rules) can
    /// see it empty; before, a quiver kept the count its item packet gave, the check never fired and the client held the
    /// emptied quiver on its cursor while the server no longer had it.
    /// </summary>
    QuiverCountFollowsServer,

    /// <summary>
    /// A carried item's count - a tome in the inventory, a stack in the cube or stash - follows the server's quantity updates
    /// (0x3E type 0x46) too; before, only worn javelins and quivers did, and a tome kept the count its item packet gave on
    /// joining: the leveling paladin's portal tome read empty all game while the server had it refilled to 100.
    /// </summary>
    CarriedCountFollowsServer,

    /// <summary>
    /// 0x1C carries the lifetime experience total and sets it; 0x1A and 0x1B add what was gained. Before, all three added,
    /// and the total grew by the whole lifetime figure at every 0x1C, which comes on joining and every few minutes.
    /// </summary>
    ExperienceTotalSets,

    /// <summary>
    /// The cursor item put into a socket leaves the cursor: the server says so only with the gem's ItemInSocket action,
    /// and the paladin's client kept a socketed perfect diamond "on the cursor", failing every later put-down.
    /// </summary>
    SocketingClearsCursor,
}

/// <summary>
/// The rules a <see cref="Game"/> interprets packets under. A recording names the rules version it was made under, so
/// a replay holds the state that bot held; the live bot runs <see cref="Latest"/>. Each rule keeps both behaviours.
/// </summary>
public sealed class ClientRules
{
    /// <summary>Every rule with the version that introduced it and what it switches; versions only ever grow.</summary>
    public static readonly IReadOnlyList<(ClientRule Rule, int Version, string Switches)> Table =
    [
        (ClientRule.MonsterLifeFromStopsAndAssigns, 1, "monster life from 0x6D stops and 0x56/0x57 assigns (ActData.UpdateNPCStop, AddNPC)"),
        (ClientRule.LifeZeroForDyingAndDead, 1, "life 0 for a dying or dead monster's 0x69 instead of its direction byte (ActData.UpdateNPCState)"),
        (ClientRule.CursorNeedsLift, 2, "a removal from stash, cube or inventory reaches the cursor only after a Lift client action (GameData.RemoveFromContainer)"),
        (ClientRule.OutstandingLifts, 3, "every unanswered Lift counts, not only the last, until the server is past it: its item's removal, put-down or drop ends it and every older one, any item reaching the cursor, a cursor clear or an act load ends all (GameData.Lifted, LiftSettled, CursorFilled)"),
        (ClientRule.QuiverCountFollowsServer, 4, "a worn quiver's (arrows or bolts) amount follows 0x3E quantity updates, so one emptied and unequipped stays off the cursor (GameData.UpdateItemStats)"),
        (ClientRule.CarriedCountFollowsServer, 5, "an inventory, cube or stash item's amount follows 0x3E quantity updates, a tome's refills and uses among them (GameData.UpdateItemStats)"),
        (ClientRule.ExperienceTotalSets, 5, "0x1C sets the experience total instead of adding to it (GameData.AddExperience)"),
        (ClientRule.SocketingClearsCursor, 6, "the cursor item's ItemInSocket action clears the cursor (GameData item action ItemInSocket)"),
    ];

    public const int LatestVersion = 6;

    public static ClientRules None { get; } = new(0);

    public static ClientRules Latest { get; } = new(LatestVersion);

    private static readonly ClientRules[] ByVersion = [.. Enumerable.Range(0, LatestVersion + 1).Select(v => v == 0 ? None : v == LatestVersion ? Latest : new ClientRules(v))];

    /// <summary>The rules of a version this build knows; a newer one is <see cref="Latest"/>, which the caller must say.</summary>
    public static ClientRules ForVersion(int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return ByVersion[Math.Min(version, LatestVersion)];
    }

    private ClientRules(int version) : this(version, [.. Table.Where(r => r.Version <= version).Select(r => r.Rule)])
    {
    }

    private ClientRules(int version, ClientRule[] on)
    {
        Version = version;
        _on = on;
        MonsterLifeFromStopsAndAssigns = Has(ClientRule.MonsterLifeFromStopsAndAssigns);
        LifeZeroForDyingAndDead = Has(ClientRule.LifeZeroForDyingAndDead);
        CursorNeedsLift = Has(ClientRule.CursorNeedsLift);
        OutstandingLifts = Has(ClientRule.OutstandingLifts);
        QuiverCountFollowsServer = Has(ClientRule.QuiverCountFollowsServer);
        CarriedCountFollowsServer = Has(ClientRule.CarriedCountFollowsServer);
        ExperienceTotalSets = Has(ClientRule.ExperienceTotalSets);
        SocketingClearsCursor = Has(ClientRule.SocketingClearsCursor);
    }

    private readonly ClientRule[] _on;

    /// <summary>These rules with one switched off, to score a rule on its own; no recording is ever made under such a set.</summary>
    public ClientRules Without(ClientRule rule) => new(Version, [.. _on.Where(r => r != rule)]) { Partial = true };

    /// <summary>Made by <see cref="Without"/>: not the rules of any version.</summary>
    public bool Partial { get; private init; }

    public int Version { get; }

    public bool MonsterLifeFromStopsAndAssigns { get; }

    public bool LifeZeroForDyingAndDead { get; }

    public bool CursorNeedsLift { get; }

    public bool OutstandingLifts { get; }

    public bool QuiverCountFollowsServer { get; }

    public bool CarriedCountFollowsServer { get; }

    public bool ExperienceTotalSets { get; }

    public bool SocketingClearsCursor { get; }

    public bool Has(ClientRule rule) => Array.IndexOf(_on, rule) >= 0;

    public IEnumerable<ClientRule> InForce => _on;

    public override string ToString()
        => $"rules {Version}{(Partial ? " in part" : "")} ({(_on.Length == 0 ? "none" : string.Join(", ", _on))})";
}
