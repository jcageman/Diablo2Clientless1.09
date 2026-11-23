using System;
using System.Collections.Generic;

namespace D2NG.MuleManager.Services.Refresh;

public enum RefreshKind
{
    /// <summary>What the mule characters hold and how much room they have.</summary>
    Mules,

    /// <summary>What the characters the bots play wear.</summary>
    Gear,
}

public enum RefreshState
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>How one account's visit is going. Written by the one task that visits it, read by the status endpoint.</summary>
public class AccountProgress
{
    public string Account { get; init; }
    public RefreshKind Kind { get; init; }
    public RefreshState State { get; set; } = RefreshState.Queued;
    public int Characters { get; set; }
    public int Recorded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string Current { get; set; }

    /// <summary>The last thing that went wrong, in words.</summary>
    public string Problem { get; set; }
}

/// <summary>One request to refresh: every mule, every played character, or one account or character.</summary>
public class RefreshJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Description { get; init; }
    public RefreshState State { get; set; } = RefreshState.Queued;
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<AccountProgress> Accounts { get; init; } = [];

    internal List<(Configuration.MuleManagerAccount Account, AccountProgress Progress, bool Force, string OnlyCharacter)> Work { get; init; } = [];
}
