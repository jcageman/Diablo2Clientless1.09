using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Concurrent;

namespace ConsoleBot.Bots.Types.CS;

internal sealed class CsState
{
    public uint? TeleportId { get; set; }

    public Point KillLocation { get; set; }

    /// <summary>The seal boss of the current fight once he has spawned, so followers can close on him.</summary>
    public uint? BossId { get; set; }

    /// <summary>The seal boss a follower has committed to closing on; the others follow on the first one's decision rather than their own view of the pack.</summary>
    public uint? FocusedBossId { get; set; }

    /// <summary>
    /// Super uniques already dead when a seal started, by id with their monster type: earlier seals'
    /// bosses, whose corpses must not end the next seal. The type is part of the key because the
    /// server reuses unit ids - Infector came back under an earlier boss's id 606 and his own death
    /// was ignored as that old corpse.
    /// </summary>
    public ConcurrentDictionary<uint, NPCCode> EarlierBosses { get; } = new();

    public bool IsEarlierBoss(WorldObject boss)
    {
        return EarlierBosses.TryGetValue(boss.Id, out var code) && code == boss.NPCCode;
    }

    /// <summary>Where the paladin stands to meet the boss: his measured spawn point, set once the party is assembled and the seal is about to open.</summary>
    public Point PaladinPost { get; set; }

    /// <summary>When the taxi last started opening a seal; the paladin keeps hammers spinning for a few seconds after, so the spawn arrives into them.</summary>
    public DateTime SealOpeningAt { get; set; } = DateTime.MinValue;

    /// <summary>Where an experience shrine was seen in the sanctuary this game, or null.</summary>
    public Point ExperienceShrineLocation { get; set; }

    /// <summary>Set once the third seal boss is down, which is when the shrine becomes worth a detour.</summary>
    public bool SealsDone { get; set; }

    /// <summary>Set once the taxi's portal at the star is up, which is the one a benched follower waits for.</summary>
    public bool DiabloPortalUp { get; set; }

    /// <summary>Set when the taxi has decided to move the spot, before she teleports: followers head for town at once instead of after her new portal is up.</summary>
    public bool RelocationPending { get; set; }

    /// <summary>The character that should take the experience shrine: the highest level one when the seals were done.</summary>
    public string ExperienceShrineTaker { get; set; }

    /// <summary>Claimed by the taker before it heads out, so nobody tries twice.</summary>
    public bool ExperienceShrineTaken { get; set; }

    public bool TeleportHasChanged(CsState otherState)
    {
        return TeleportId != otherState.TeleportId;
    }
}
