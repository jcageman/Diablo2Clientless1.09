using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.MuleManager;
using D2NG.MuleManager.Services.Stock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.Refresh;

/// <summary>
/// Refresh requests, run one job at a time so two refreshes never log on to the same account at
/// once. Within a job a few accounts are visited in parallel.
/// </summary>
public class RefreshQueue
{
    private readonly Channel<RefreshJob> _channel = Channel.CreateUnbounded<RefreshJob>();
    private readonly List<RefreshJob> _jobs = [];
    private readonly Lock _lock = new();
    private readonly MuleManagerConfiguration _configuration;

    public RefreshQueue(IOptions<MuleManagerConfiguration> configuration)
    {
        _configuration = configuration.Value;
    }

    public ChannelReader<RefreshJob> Reader => _channel.Reader;

    /// <summary>The most recent jobs, newest first.</summary>
    public IReadOnlyList<RefreshJob> Jobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs.OrderByDescending(j => j.QueuedAt).Take(10)];
            }
        }
    }

    /// <summary>Every mule, forced, and every played character: what a first run needs.</summary>
    public RefreshJob EnqueueEverything()
        => Enqueue("Every account", [
            .. _configuration.Accounts.Select(a => (a, RefreshKind.Mules, true, (string)null)),
            .. _configuration.GearAccounts.Select(a => (a, RefreshKind.Gear, false, (string)null)),
        ]);

    /// <summary>One mule account, or every mule account when <paramref name="account"/> is null.</summary>
    public RefreshJob EnqueueMules(string account, bool force)
    {
        var accounts = Pick(_configuration.Accounts, account);
        return accounts == null ? null : Enqueue(account == null ? "Every mule" : $"Mules on {account}", [.. accounts.Select(a => (a, RefreshKind.Mules, force, (string)null))]);
    }

    /// <summary>The gear of one character, every character on one account, or every played character.</summary>
    public RefreshJob EnqueueGear(string account, string character)
    {
        var accounts = Pick(_configuration.GearAccounts, account);
        var description = account == null ? "Every played character" : character == null ? $"Gear on {account}" : $"Gear of {character}";
        return accounts == null ? null : Enqueue(description, [.. accounts.Select(a => (a, RefreshKind.Gear, false, character))]);
    }

    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Any(j => j.State is RefreshState.Queued or RefreshState.Running);
            }
        }
    }

    private static List<MuleManagerAccount> Pick(List<MuleManagerAccount> accounts, string name)
        => name == null ? accounts : accounts.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } one ? one : null;

    private RefreshJob Enqueue(string description, List<(MuleManagerAccount Account, RefreshKind Kind, bool Force, string OnlyCharacter)> work)
    {
        var job = new RefreshJob
        {
            Description = description,
            Work = [.. work.Select(w => (w.Account, new AccountProgress { Account = w.Account.Name, Kind = w.Kind }, w.Force, w.OnlyCharacter))],
        };
        job.Accounts.AddRange(job.Work.Select(w => w.Progress));
        lock (_lock)
        {
            _jobs.Add(job);
            _jobs.RemoveAll(j => j.FinishedAt != null && _jobs.Count > 20);
        }

        _channel.Writer.TryWrite(job);
        return job;
    }
}

public class RefreshWorker : BackgroundService
{
    private readonly RefreshQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly StockCache _stock;
    private readonly MuleManagerConfiguration _configuration;

    public RefreshWorker(RefreshQueue queue, IServiceScopeFactory scopes, StockCache stock, IOptions<MuleManagerConfiguration> configuration)
    {
        _queue = queue;
        _scopes = scopes;
        _stock = stock;
        _configuration = configuration.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            job.State = RefreshState.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            Log.Information("Refresh started: {Job}", job.Description);
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = _configuration.Parallelism, CancellationToken = stoppingToken };
                await Parallel.ForEachAsync(job.Work, options, async (work, token) =>
                {
                    using var scope = _scopes.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IMuleManagerService>();
                    await service.RefreshAccount(work.Account, work.Progress.Kind, work.Force, work.OnlyCharacter, work.Progress, token);
                    _stock.Invalidate();
                });
                job.State = job.Accounts.All(a => a.State == RefreshState.Failed) ? RefreshState.Failed : RefreshState.Done;
            }
            catch (OperationCanceledException)
            {
                job.State = RefreshState.Failed;
            }
            catch (Exception e)
            {
                Log.Error(e, "Refresh {Job} failed", job.Description);
                job.State = RefreshState.Failed;
            }

            job.FinishedAt = DateTimeOffset.UtcNow;
            _stock.Invalidate();
            Log.Information("Refresh finished: {Job}, {State}", job.Description, job.State);
        }
    }
}
