using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Spare gold goes on gambling the slots the drops leave weak - the party reached Hell with single-resist belts and
/// boots and terrible gloves while sitting on millions. Each kind only from the level its best affixes roll at, so the
/// gold waits for items worth having instead of going on low ones.
/// </summary>
public static class LevelingGamble
{
    /// <summary>
    /// The gambler's level from which each kind is gambled; a gambled item's level follows the character's. From the
    /// classic affix tables in item-affixes.json.
    /// </summary>
    private static readonly (ClassificationType Kind, int FromLevel)[] Kinds =
    [
        // Garnet/Cobalt/Coral/Jade 21-30 resistance from 18, of the Leech and of the Bat from 6, of the Mammoth 31-40
        // life at 24; Prismatic (15 to all) only at 62.
        (ClassificationType.Ring, 24),
        // Ruby/Sapphire/Amber/Emerald 31-50 resistance at 25, of Speed 30% run at 22, of the Titan 16-20 strength at 30.
        (ClassificationType.Boots, 30),
        // 21-30 resistance and of Stability 24% hit recovery at 18, of the Colosuss 41-60 life and of the Titan at 30.
        (ClassificationType.Belt, 30),
        // Leech from 34, 21-30 resistance only from 55.
        (ClassificationType.Gloves, 55),
        // Amulets only from 90, by the user's rule.
        (ClassificationType.Amulet, 90),
    ];

    public static HashSet<ClassificationType> KindsFor(int level) => Kinds.Where(kind => level >= kind.FromLevel).Select(kind => kind.Kind).ToHashSet();

    /// <summary>
    /// Fixed amounts at high levels, not a share of a carry cap: the realm let members carry a million gold each at level 37,
    /// so a cap of ten thousand a level made every game a gambling game. Lower at the levels gambling opens, where the party
    /// holds a hundred or two thousand each.
    /// </summary>
    private static int StartAtGold(PartyMember member) => Math.Min(500_000, 10_000 * member.Level);

    /// <summary>
    /// Items a round may gamble. Eight a round at one round per half hour, doubling after rounds that kept nothing, left the
    /// party at level 64 with 5.4 million gold and the gloves, boots and belts the drops gave it: 104 items in all of Hell.
    /// </summary>
    private const int MaxPerVisit = 30;

    /// <summary>What the gambler is topped up to from the others; a gambler already this rich takes nothing.</summary>
    public static int GoldCap(PartyMember member) => StartAtGold(member) + 1_000_000;

    /// <summary>What a member keeps when its gold is pooled for gambling: potions, repairs and the merchants' gear.</summary>
    public static int Keeps(PartyMember member) => Math.Min(250_000, 4_000 * member.Level);

    public static bool Wants(PartyMember member) => KindsFor(member.Level).Count > 0 && LevelingGear.Gold(member.Game) >= StartAtGold(member);

    /// <summary>
    /// One round per half hour at most: gambling costs the party its time. The wait no longer doubles after rounds that kept
    /// nothing: the gold has no other use, and the doubling held the party to a round every two to four hours in Hell.
    /// </summary>
    private static readonly TimeSpan RoundInterval = TimeSpan.FromMinutes(30);

    private static DateTime _lastRound = DateTime.MinValue;

    /// <summary>Rounds in a row that kept nothing, logged with each round.</summary>
    private static int _emptyRounds;

    /// <summary>Where the round clock and the empty rounds are kept, so a restart does not start them over.</summary>
    public static string StateFile { get; set; }

    private sealed record GambleState(DateTime LastRound, int EmptyRounds);

    private static bool _loaded;

    private static void Load()
    {
        if (_loaded || StateFile == null)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (System.IO.File.Exists(StateFile) && System.Text.Json.JsonSerializer.Deserialize<GambleState>(System.IO.File.ReadAllText(StateFile)) is { } state)
            {
                _lastRound = state.LastRound;
                _emptyRounds = state.EmptyRounds;
            }
        }
        catch (Exception exception) when (exception is System.IO.IOException or System.Text.Json.JsonException)
        {
            Log.Warning("Could not read the gamble state: {Reason}", exception.Message);
        }
    }

    private static void SaveState()
    {
        if (StateFile == null)
        {
            return;
        }

        try
        {
            System.IO.File.WriteAllText(StateFile, System.Text.Json.JsonSerializer.Serialize(new GambleState(_lastRound, _emptyRounds)));
        }
        catch (System.IO.IOException exception)
        {
            Log.Warning("Could not save the gamble state: {Reason}", exception.Message);
        }
    }

    /// <summary>Whether the party's spare gold, pooled on <paramref name="gambler"/>, would bring it near its cap.</summary>
    public static bool PartyCanFund(IEnumerable<PartyMember> members, PartyMember gambler)
        => LoadedNow() && KindsFor(gambler.Level).Count > 0 && DateTime.UtcNow - _lastRound >= RoundInterval
            && LevelingGear.Gold(gambler.Game) + members.Where(member => member != gambler).Sum(member => Math.Max(0, LevelingGear.Gold(member.Game) - Keeps(member)))
            >= StartAtGold(gambler);

    private static bool LoadedNow()
    {
        Load();
        return true;
    }

    /// <summary>Gambles at the act's gamble merchant, keeping what improves anyone in the party and selling the rest back.</summary>
    public static void Gamble(PartyMember member, Party party, LevelingGear gear, WorldObject npc)
    {
        var game = member.Game;
        var kinds = KindsFor(member.Level);
        var stopAt = Keeps(member);
        if (!GeneralHelpers.TryWithTimeout(_ =>
            {
                if (game.Me.Location.Distance(npc.Location) >= 5)
                {
                    game.MoveTo(npc);
                    return false;
                }

                return game.InteractWithNPC(npc);
            }, TimeSpan.FromSeconds(5)))
        {
            Log.Warning("{Character} could not reach {Npc} to gamble", member.Name, npc.NPCCode);
            return;
        }

        _lastRound = DateTime.UtcNow;

        game.InitiateEntityChat(npc);
        game.TownFolkAction(npc, TownFolkActionType.Gamble);
        var startGold = LevelingGear.Gold(game);
        var tries = 0;
        var kept = new List<string>();
        try
        {
            while (tries < MaxPerVisit && LevelingGear.Gold(game) > stopAt)
            {
                GeneralHelpers.TryWithTimeout(_ => Offers(game, kinds).Count > 0, TimeSpan.FromSeconds(2));
                var offers = Offers(game, kinds);
                if (offers.Count == 0)
                {
                    break;
                }

                foreach (var offer in offers)
                {
                    if (tries >= MaxPerVisit || LevelingGear.Gold(game) <= stopAt || game.Inventory.FindFreeSpace(offer) == null)
                    {
                        break;
                    }

                    tries++;
                    var before = game.Inventory.Items.Select(item => item.Id).ToHashSet();
                    game.GambleItem(npc, offer);
                    Item won = null;
                    var identified = GeneralHelpers.TryWithTimeout(_ =>
                    {
                        won ??= game.Inventory.Items.FirstOrDefault(item => !before.Contains(item.Id));
                        if (won == null)
                        {
                            return false;
                        }

                        if (!won.IsIdentified)
                        {
                            game.IdentifyGambleItem(won);
                        }

                        won = game.Inventory.FindItemById(won.Id) ?? won;
                        return won.IsIdentified;
                    }, TimeSpan.FromSeconds(3));
                    if (!identified || won == null)
                    {
                        break;
                    }

                    if (gear.Keeps(party, member, won))
                    {
                        kept.Add($"{won.Quality} {won.Name}");
                    }
                    else
                    {
                        game.SellItem(npc, won);
                    }
                }

                game.TownFolkAction(npc, TownFolkActionType.RefreshGamble);
            }
        }
        finally
        {
            game.TerminateEntityChat(npc);
        }

        _emptyRounds = kept.Count == 0 ? _emptyRounds + 1 : 0;
        SaveState();

        Log.Information("{Character} gambled {Tries} items for {Gold} gold at {Npc}, kept {Kept} ({Empty} rounds in a row kept nothing)", member.Name, tries,
            startGold - LevelingGear.Gold(game), npc.NPCCode, kept.Count == 0 ? "none" : string.Join(", ", kept), _emptyRounds);
    }

    private static List<Item> Offers(D2NG.Core.Game game, HashSet<ClassificationType> kinds)
        => game.Items.Values.Where(item => item.IsInMerchantTab() && kinds.Contains(item.Classification)).ToList();
}
