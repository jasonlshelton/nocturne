using System.Collections.Concurrent;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Jobs;

/// <summary>
/// Runs every registered <see cref="IScheduledJob"/> from <see cref="IJobLedger"/>: it enrols each
/// job's rows, claims the due ones every <see cref="PollInterval"/>, runs up to
/// <see cref="Parallelism"/> at once, and writes each run's outcome back to its row.
/// </summary>
/// <remarks>
/// <para>
/// The ledger holds every schedule and cursor, so a restart resumes where the last process stopped
/// rather than replaying work: a row is due when its time has come, not when the process starts.
/// The claim leases a row for <see cref="LeaseLength"/>, renewed while the run lasts, so replicas
/// sharing the ledger never run one row at once, and a row whose holder died is claimable when its
/// lease runs out. A run whose lease is lost is cancelled and records nothing. Only rows of jobs this
/// process has are claimed, so during a rolling deploy each version runs only the jobs it knows.
/// </para>
/// <para>
/// A run that throws keeps its cursor and is due again after <see cref="Backoff"/>. A run that throws
/// once shutdown has begun, whatever it throws, is not a failure: it releases its lease and keeps its
/// due time and cursor, so the next holder resumes at once.
/// Each run gets a scope of its own for its row's tenant, reading as no one and system-attributed as
/// <c>job:&lt;name&gt;</c>.
/// </para>
/// </remarks>
public sealed class JobScheduler : BackgroundService
{
    internal const int Parallelism = 4;

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan EnrolInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(40);
    internal static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);

    private const int MaxErrorLength = 2000;

    private readonly Dictionary<string, IScheduledJob> _jobs;
    private readonly IJobLedger _ledger;
    private readonly IServiceProvider _root;
    private readonly TimeProvider _time;
    private readonly JobMetrics _metrics;
    private readonly ILogger<JobScheduler> _logger;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private int _running;

    public JobScheduler(
        IEnumerable<IScheduledJob> jobs,
        IJobLedger ledger,
        IServiceProvider root,
        TimeProvider time,
        JobMetrics metrics,
        ILogger<JobScheduler> logger)
    {
        var byName = new Dictionary<string, IScheduledJob>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (!byName.TryAdd(job.Name, job))
                throw new InvalidOperationException($"Two scheduled jobs are named '{job.Name}'.");
        }

        _jobs = byName;
        _ledger = ledger;
        _root = root;
        _time = time;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>This process's name, which prefixes the token of each lease it takes.</summary>
    internal string Holder { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>
    /// How long a job waits after its <paramref name="attempts"/>th failure in a row: one minute,
    /// doubling, up to the lower of <see cref="IScheduledJob.DefaultMaxBackoff"/> and <paramref name="cap"/>.
    /// </summary>
    internal static TimeSpan Backoff(int attempts, TimeSpan cap)
    {
        var limit = cap < IScheduledJob.DefaultMaxBackoff ? cap : IScheduledJob.DefaultMaxBackoff;
        var wait = FirstBackoff * Math.Pow(2, Math.Clamp(attempts - 1, 0, 30));
        return wait < limit ? wait : limit;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Detach from the sequential hosted-service startup chain before touching anything.
        await Task.Yield();

        using var timer = new PeriodicTimer(PollInterval, _time);
        var enrolledAt = DateTimeOffset.MinValue;
        try
        {
            do
            {
                try
                {
                    if (_time.GetUtcNow() - enrolledAt >= EnrolInterval)
                    {
                        await EnrolAsync(stoppingToken);
                        enrolledAt = _time.GetUtcNow();
                    }

                    await PollAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The job scheduler could not read the job ledger");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await DrainAsync();
        }
    }

    /// <summary>Gives every job its rows and refreshes the failing count.</summary>
    internal async Task EnrolAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        foreach (var job in _jobs.Values)
        {
            switch (job.Enrolment)
            {
                case JobEnrolment.PerTenant:
                    await _ledger.EnrolPerTenantAsync(job.Name, now, ct);
                    break;
                case JobEnrolment.Platform:
                    await _ledger.EnrolPlatformAsync(job.Name, now, ct);
                    break;
            }
        }

        _metrics.SetFailing(await _ledger.CountFailingAsync(ct));
    }

    /// <summary>Claims as many due rows as there are free slots and starts a run for each.</summary>
    /// <returns>How many runs it started.</returns>
    internal async Task<int> PollAsync(CancellationToken ct)
    {
        var free = Parallelism - Volatile.Read(ref _running);
        if (free <= 0)
            return 0;

        var now = _time.GetUtcNow();
        var leases = await _ledger.ClaimDueAsync(Holder, _jobs.Keys, free, now, now + LeaseLength, ct);
        foreach (var lease in leases)
        {
            Interlocked.Increment(ref _running);
            var run = Task.Run(() => RunAsync(lease, now, ct), CancellationToken.None);
            _inFlight.TryAdd(run, 0);
            _ = run.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
        }

        return leases.Count;
    }

    /// <summary>Waits for every run started so far.</summary>
    internal Task DrainAsync() => Task.WhenAll(_inFlight.Keys);

    private async Task RunAsync(JobLease lease, DateTimeOffset startedAt, CancellationToken ct)
    {
        var job = _jobs[lease.Job];
        var started = _time.GetTimestamp();
        var outcome = "failed";
        try
        {
            using var leaseLost = new CancellationTokenSource();
            using var running = CancellationTokenSource.CreateLinkedTokenSource(ct, leaseLost.Token);
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var renewing = RenewLeaseAsync(lease, leaseLost, heartbeat.Token);

            JobOutcome? result = null;
            Exception? thrown = null;
            try
            {
                await using var scope = TenantJobScope.Open(_root, lease.Tenant, $"job:{job.Name}");
                result = await job.RunAsync(
                    new JobRun(lease.Tenant, lease.Cursor, lease.Attempts, scope.ServiceProvider, startedAt),
                    running.Token);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                // Stopped before the ledger write, so no beat lands on a row the write released.
                await heartbeat.CancelAsync();
                await renewing;
            }

            if (leaseLost.IsCancellationRequested)
            {
                if (thrown is not null)
                {
                    _logger.Log(
                        thrown is OperationCanceledException ? LogLevel.Debug : LogLevel.Warning,
                        thrown,
                        "Scheduled job {Job} ended on row {RowId} after losing its lease",
                        lease.Job, lease.Id);
                }

                outcome = "lost";
            }
            else if (thrown is not null && ct.IsCancellationRequested)
            {
                await _ledger.ReleaseAsync(lease, CancellationToken.None);
                outcome = "cancelled";
            }
            else if (thrown is not null)
            {
                _logger.LogWarning(thrown, "Scheduled job {Job} failed for tenant {TenantId}", lease.Job, lease.Tenant?.TenantId);
                var error = $"{thrown.GetType().Name}: {thrown.Message}";
                await _ledger.FailAsync(
                    lease,
                    error.Length > MaxErrorLength ? error[..MaxErrorLength] : error,
                    _time.GetUtcNow() + Backoff(lease.Attempts + 1, job.MaxBackoff),
                    CancellationToken.None);
            }
            else
            {
                var (cursor, nextDueAt) = result is JobOutcome.Resumed resumed
                    ? (resumed.Cursor, resumed.NextDueAt)
                    : ((JobCursor?)null, (DateTimeOffset?)null);
                await _ledger.CompleteAsync(lease, _time.GetUtcNow(), cursor, nextDueAt, CancellationToken.None);
                outcome = nextDueAt is null ? "done" : "resumed";
            }
        }
        catch (Exception ex)
        {
            // The lease runs out on its own, and the row is claimed again then.
            _logger.LogError(ex, "Could not record the run of scheduled job {Job}", lease.Job);
        }
        finally
        {
            _metrics.RecordRun(lease.Job, outcome, _time.GetElapsedTime(started));
            Interlocked.Decrement(ref _running);
        }
    }

    /// <summary>
    /// Extends the lease every <see cref="HeartbeatInterval"/> until <paramref name="ct"/> ends it.
    /// A failed extension is retried at the next beat; only the ledger answering that the lease is no
    /// longer held stops it, and that cancels the run through <paramref name="leaseLost"/>. Never throws.
    /// </summary>
    private async Task RenewLeaseAsync(JobLease lease, CancellationTokenSource leaseLost, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, _time, ct);
                if (await _ledger.ExtendLeaseAsync(lease, _time.GetUtcNow() + LeaseLength, ct))
                    continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not renew the lease of scheduled job {Job}; retrying at the next beat", lease.Job);
                continue;
            }
            catch (Exception)
            {
                return;
            }

            _logger.LogWarning(
                "Scheduled job {Job} lost its lease on row {RowId}; the run is cancelled and records nothing",
                lease.Job, lease.Id);
            try
            {
                await leaseLost.CancelAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A cancellation callback of scheduled job {Job} threw", lease.Job);
            }

            return;
        }
    }
}
