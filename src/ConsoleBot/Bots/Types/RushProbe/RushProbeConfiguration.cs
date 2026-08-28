using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.RushProbe;

/// <summary>
/// Configuration for the rush probe, bound from <c>bot:rushprobe</c>. The probe is a harness that
/// exercises the primitives a rush needs against a live server one step at a time, so that each one
/// is proven from the bot's own socket before any rush logic is built on it.
/// </summary>
public class RushProbeConfiguration : AccountConfig
{
    /// <summary>
    /// Steps to run in order. Recognised values are <c>login</c>, <c>create</c>, <c>quests</c> and
    /// <c>death</c>. Keeping them separate means a step that fails stops the run before the next one
    /// sends anything.
    /// </summary>
    /// <remarks>
    /// Deliberately left null rather than given a default list: configuration binding appends to an
    /// existing collection instead of replacing it, so a default would silently prepend itself to
    /// whatever the configuration asks for.
    /// </remarks>
    public List<string> Steps { get; set; }

    /// <summary>
    /// Names the probe may use for its throwaway characters, tried in order until the realm accepts
    /// one. This list is also the deletion allowlist: no character whose name is absent from it is
    /// ever deleted, which makes an accidental match impossible rather than unlikely.
    /// </summary>
    /// <remarks>
    /// Names read like ordinary player names on purpose, so that a character standing in a town does
    /// not announce itself as a test. Letters only: the realm rejects digits. Left null rather than
    /// defaulted because configuration binding appends to a pre-filled list instead of replacing it.
    /// </remarks>
    public List<string> CharacterNames { get; set; }

    /// <summary>
    /// Class to create throwaway characters as. Sorceress by default, the class a rush actually needs
    /// to test with.
    /// </summary>
    public string CharacterClass { get; set; } = "Sorceress";

    /// <summary>
    /// Whether created characters are expansion characters. Classic when <see langword="false"/>.
    /// </summary>
    public bool Expansion { get; set; }

    /// <summary>
    /// Accounts on which the probe is allowed to delete characters. Deletion is refused unless the
    /// account it is logged into appears here, so a mistyped configuration cannot destroy a real
    /// character. Empty by default, meaning nothing may ever be deleted.
    /// </summary>
    public List<string> DeletableAccounts { get; set; } = [];

    /// <summary>
    /// Whether to delete the throwaway characters the probe created when it is done with them. Only
    /// ever applies to characters this run created, and only on <see cref="DeletableAccounts"/>.
    /// </summary>
    public bool CleanUpCreatedCharacters { get; set; }

    /// <summary>
    /// Whether to carry on with a character from <see cref="CharacterNames"/> that already exists
    /// rather than creating a new one. On by default so progress made in an earlier run is kept: once
    /// act 1 is rushed there is no point rushing it again to test act 2.
    /// </summary>
    public bool ReuseExistingCharacter { get; set; } = true;

    /// <summary>
    /// How many extra players to wait for before starting the descent, so a human can join the game
    /// and watch what happens. Zero means start immediately.
    /// </summary>
    public int WaitForExtraPlayers { get; set; }

    /// <summary>
    /// How long to wait for those extra players before giving up on them and starting anyway.
    /// </summary>
    public int WaitForPlayersTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Account of the rusher that opens the portal for the throwaway character. Must be the same game
    /// mode as the created character: a classic character cannot share a game with an expansion one.
    /// </summary>
    public string RusherUsername { get; set; }

    public string RusherPassword { get; set; }

    public string RusherCharacter { get; set; }

    /// <summary>
    /// Waypoint the rusher takes to reach the staging area. Catacombs level 2 is the natural choice:
    /// it is where a real rush stages for Andariel, and it is busy enough to kill a level one
    /// character without any help.
    /// </summary>
    public string DangerousWaypoint { get; set; } = "CatacombsLevel2";

    /// <summary>
    /// Area the rusher opens the portal in, which has to match <see cref="DangerousWaypoint"/>.
    /// </summary>
    public string DeathArea { get; set; } = "CatacombsLevel2";

    /// <summary>
    /// How long the death step waits for the character to be killed before giving up.
    /// </summary>
    [Range(10, 600)]
    public int DeathTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Act the rusher restocks potions in, or empty to skip restocking. Empty by default.
    /// </summary>
    /// <remarks>
    /// Act 4 is the act to name when this is wanted: in normal difficulty the act 1 and act 2 vendors
    /// only stock minor and light potions, which barely register on a level 90 character, while Jamella
    /// sells the full size ones.
    /// <para>
    /// Off by default because restocking runs the whole town routine, not just the shopping. A run of it
    /// identified 22 items on the rusher, sold 21 of them including a Mephisto's Soulstone and every
    /// spare potion in the inventory, and then bought nothing because the belt was already full. Turn it
    /// on when the rusher genuinely needs potions and its inventory is expendable.
    /// </para>
    /// </remarks>
    public string RestockAct { get; set; } = "";

    public int HoldAtTyraelSeconds { get; set; }

    /// <summary>
    /// Name of a game created by hand, for the steps that join a game rather than make one. The
    /// orifice refuses a staff when the game's seven tombs is already settled, so the game has to be
    /// created by the character carrying the staff rather than by the rusher.
    /// </summary>
    public string ManualGameName { get; set; }

    /// <summary>
    /// Waypoint the rusher takes when holding an area open for a person, for example
    /// <c>CanyonOfTheMagi</c> for the tombs or <c>LostCity</c> for the claw viper temple.
    /// </summary>
    public string ManualWaypoint { get; set; } = "CanyonOfTheMagi";

    /// <summary>
    /// Area the rusher clears and opens a portal into. Left empty it means the real tal rasha tomb, which
    /// is resolved per game from the map api rather than guessed.
    /// </summary>
    public string ManualTargetArea { get; set; } = "";

    /// <summary>
    /// Object in that area to stand next to, so the portal lands on the thing the person needs to click.
    /// </summary>
    public string ManualTargetObject { get; set; } = "HoradricOrifice";

    /// <summary>
    /// How long the rusher holds a cleared orifice room open for a person to walk in, insert the staff
    /// and get a capture of it. Its portal disappears when it leaves, so leaving early strands them.
    /// </summary>
    [Range(30, 3600)]
    public int HoldTombSeconds { get; set; } = 600;


    /// <summary>
    /// How many games the portalspots step makes while hunting for act 1 town layouts it has not measured.
    /// </summary>
    public int PortalMeasurementGames { get; set; }

    /// <summary>
    /// How long the rusher holds the game open after Diablo dies, with the rushee logged out, so the
    /// character is free for a real client to join and be captured talking to Tyrael.
    /// </summary>
    public int HoldAfterDiabloSeconds { get; set; }
}