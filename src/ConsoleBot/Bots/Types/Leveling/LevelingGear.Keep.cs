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
/// What the party keeps in its stashes rather than selling: whatever the farming bots' nips keep, and gear a member will be
/// able to wear once its plan's stats and some from gear let it. A magic Martel de Fer with 99% enhanced damage went to the
/// merchant for 24 thousand gold because the barbarian had 90 strength of the 169 it needs, while he whirled with a morning
/// star doing a fifth of its damage. Only the best kept item of each kind per member stays; a better one replaces it.
/// </summary>
public sealed partial class LevelingGear
{
    /// <summary>The party's levels, worn gear and stashes when the stashes were last weighed.</summary>
    private string _stashWeighedFor;

    /// <summary>
    /// What the item is worth to the member once its plan's stats let it wear it, judged on its own rather than against the
    /// resistances and leech the rest of its outfit gives; -1 when it never will be.
    /// </summary>
    private double KeepScore(PartyMember member, Item item)
        => item.IsIdentified || item.Quality < QualityType.Magical ? PieceScore(member, item, later: true, alone: true) : -1;

    /// <summary>One kind per body place: rings are one kind, a weapon and a shield two.</summary>
    private static DirectoryType SlotKind(Item item)
        => WeaponKinds.Contains(item.Classification) ? DirectoryType.RightHand
            : IsWeaponOrShield(item) ? DirectoryType.LeftHand
            : Slots.TryGetValue(item.Classification, out var slots) ? slots[0] : DirectoryType.NotApplicable;

    /// <summary>What the member wears in that place, judged on its own; the weaker ring for rings, nothing worn being 0.</summary>
    private double WornAlone(PartyMember member, DirectoryType kind)
    {
        var places = kind == DirectoryType.RightHandRing ? [DirectoryType.RightHandRing, DirectoryType.LeftHandRing] : new[] { kind };
        return places.Select(place => member.Game.Me.Equipment.GetValueOrDefault(place) is { } worn ? Math.Max(0, PieceScore(member, worn, alone: true)) : 0).Min();
    }

    /// <summary>
    /// Whether the member keeps this item for later rather than selling it: not an upgrade now, but better on its own than
    /// what it wears there - it needs more strength or level than the member has yet, or the resistances the rest of the
    /// outfit gives hold it back for now - and the best of its kind among what the member already holds.
    /// </summary>
    public bool KeepsForLater(PartyMember member, Item item)
    {
        if (OneHandedBackup(member, item))
        {
            return true;
        }

        var kind = SlotKind(item);
        var keep = KeepScore(member, item);
        if (kind == DirectoryType.NotApplicable || keep < 0 || keep <= WornAlone(member, kind) * UpgradeMargin || SlotGain(member, item, out _) > 0)
        {
            return false;
        }

        return !member.Game.Stash.Items.Concat(member.Game.Inventory.Items)
            .Where(other => other.Id != item.Id && SlotKind(other) == kind)
            .Select(other => (other.Id, Score: KeepScore(member, other)))
            .Any(other => other.Score > keep || (other.Score == keep && other.Id < item.Id));
    }

    /// <summary>
    /// A shield bearer swinging a two-hander keeps its best one-handed weapon, as its shield is kept, to go back to both: the
    /// barbarian's maul was sold the moment a one-handed sword and a shield took its place.
    /// </summary>
    private bool OneHandedBackup(PartyMember member, Item item)
    {
        if (IsCaster(member) || !member.Plan.Shield || !WeaponKinds.Contains(item.Classification) || IsTwoHanded(item) || !IsTwoHandedWeaponWorn(member))
        {
            return false;
        }

        var keep = KeepScore(member, item);
        return keep > 0 && !member.Game.Stash.Items.Concat(member.Game.Inventory.Items)
            .Where(other => other.Id != item.Id && WeaponKinds.Contains(other.Classification) && !IsTwoHanded(other))
            .Select(other => (other.Id, Score: KeepScore(member, other)))
            .Any(other => other.Score > keep || (other.Score == keep && other.Id < item.Id));
    }

    public bool WantedLater(Party party, Item item) => party.Members.Where(member => member.InGame).Any(member => KeepsForLater(member, item));

    /// <summary>What the farming bots' nips keep is kept by the party too, worn or not - except the diamonds its shield still needs.</summary>
    public static bool IsPickitKeeper(PartyMember member, Item item)
        => (item.IsIdentified || item.Quality < QualityType.Magical) && LevelingShop.Sellable(item, keepsBows: false)
            && !(LevelingGems.Collecting && LevelingGems.IsDiamond(item))
            && D2NG.Pickit.Pickit.ShouldKeepItem(member.Game, item);

    /// <summary>Whether the holder keeps the item rather than selling it: someone wears it now or later, or the nips keep it.</summary>
    public bool Keeps(Party party, PartyMember holder, Item item)
        => WantedByAnyone(party, item) || WantedLater(party, item) || IsPickitKeeper(holder, item);

    /// <summary>
    /// Stashed items come out once someone can wear them, or once a better one took their place: worn by whoever gains most,
    /// or sold on the next visit.
    /// </summary>
    public async Task TakeFromStash(Party party, IReadOnlyList<PartyMember> present)
    {
        // Weighed again only when what decides it changed: a member's level, what anyone wears, or what the stashes hold.
        var state = string.Join(";", party.Members.Where(member => member.InGame).Select(member => $"{member.Name}:{member.Level}:"
            + string.Join(",", member.Game.Me.Equipment.Values.Concat(member.Game.Stash.Items).Select(item => item.Id).Order())));
        if (state == _stashWeighedFor)
        {
            return;
        }

        _stashWeighedFor = state;
        await Task.WhenAll(present.Where(member => member.InGame).Select(async member =>
        {
            var take = member.Game.Stash.Items
                .Where(item => WantedByAnyone(party, item) || !(WantedLater(party, item) || IsPickitKeeper(member, item)))
                .ToList();
            if (take.Count == 0 || !await WalkToStash(member))
            {
                return;
            }

            var result = await Task.Run(() => InventoryHelpers.MoveStashItemsToInventory(member.Game, take));
            Log.Information("{Character} took {Items} out of its stash: {Result}", member.Name,
                string.Join(", ", take.Select(item => $"{item.Quality} {item.Name}")), result);
        }));

        foreach (var member in present.Where(member => member.InGame))
        {
            await WearUpgrades(member);
        }
    }

    /// <summary>
    /// Items kept for later go to the member they are kept for; then every member stashes what it keeps and nobody wears now.
    /// </summary>
    public async Task StashKeepers(Party party, IReadOnlyList<PartyMember> present)
    {
        foreach (var holder in present.Where(member => member.InGame))
        {
            foreach (var item in holder.Game.Inventory.Items.Where(item => !WantedByAnyone(party, item) && !LevelingGems.ReservedForDiamonds(item)).ToList())
            {
                var owner = present.Where(member => member.InGame && KeepsForLater(member, item))
                    .OrderByDescending(member => SlotGain(member, item, out _, later: true)).FirstOrDefault();
                if (owner != null && owner != holder && await Hand(holder, owner, item))
                {
                    Log.Information("{Holder} handed {Quality} {Item} to {Owner} to keep until it can wear it", holder.Name, item.Quality, item.Name, owner.Name);
                }
            }
        }

        await Task.WhenAll(present.Where(member => member.InGame).Select(async member =>
        {
            var keep = member.Game.Inventory.Items
                .Where(item => !WantedByAnyone(party, item) && !LevelingGems.ReservedForDiamonds(item) && (KeepsForLater(member, item) || IsPickitKeeper(member, item)))
                .ToList();
            if (keep.Count == 0 || !await WalkToStash(member))
            {
                return;
            }

            var result = await Task.Run(() => InventoryHelpers.StashItemsAndGold(member.Game, keep, 0));
            Log.Information("{Character} stashed {Items}: {Result}", member.Name,
                string.Join(", ", keep.Select(item => $"{item.Quality} {item.Name}{(KeepsForLater(member, item) ? " (for later)" : "")}")), result);
        }));
    }

    /// <summary>Where the town map puts the stash; set by the bot, which owns the map. The client only sees it from nearby.</summary>
    public static Func<PartyMember, Task<D2NG.Core.D2GS.Point>> StashOnMap { get; set; }

    internal static async Task<bool> WalkToStash(PartyMember member)
    {
        // Out of sight it is not there for the client: the paladin stood by Jamella in the Pandemonium Fortress and saw none.
        if (member.Game.GetEntityByCode(EntityCode.Stash).Count == 0 && WalkTo != null && StashOnMap != null && await StashOnMap(member) is { } onMap)
        {
            await WalkTo(member, onMap);
        }

        var stash = member.Game.GetEntityByCode(EntityCode.Stash).FirstOrDefault();
        if (stash == null)
        {
            Log.Warning("{Character} sees no stash in town", member.Name);
            return false;
        }

        if (WalkTo != null && member.Game.Me.Location.Distance(stash.Location) > 5)
        {
            await WalkTo(member, stash.Location);
        }

        return true;
    }
}
