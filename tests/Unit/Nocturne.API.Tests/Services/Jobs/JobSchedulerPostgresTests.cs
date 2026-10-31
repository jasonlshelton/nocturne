using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Jobs;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// <see cref="JobScheduler"/> over a migrated PostgreSQL ledger. A "restart" is a fresh scheduler
/// over the same database: it has nothing in memory, so whatever it does it read from the ledger.
/// Each test starts with no tenants and an empty ledger, since a per-tenant job enrols every tenant.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JobSchedulerPostgresTests(JobSchedulerPostgresTests.Database database)
    : IClassFixture<JobSchedulerPostgresTests.Database>, IAsyncLifetime
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateMigratedDatabaseAsync("job_scheduler");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly List<ServiceProvider> _providers = [];
    private readonly string _app = database.Created.AppConnectionString;
    private readonly string _migrator = database.Created.MigratorConnectionString;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var reset = new NpgsqlCommand("DELETE FROM job_ledger; DELETE FROM tenants;", conn);
        await reset.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_restarted_scheduler_resumes_from_the_stored_cursor_and_never_reruns_a_finished_row()
    {
        var tenant = await SeedTenantAsync();
        var firstRuns = new PagingJob();
        var first = Scheduler(firstRuns);

        await first.EnrolAsync(default);
        await RunDueAsync(first);
        firstRuns.Pages.Should().Equal([(tenant, (int?)null)], "the first process runs the first page and stops");

        var restartedRuns = new PagingJob();
        var restarted = Scheduler(restartedRuns);
        await restarted.EnrolAsync(default);
        while (await RunDueAsync(restarted) > 0)
        {
        }

        restartedRuns.Pages.Select(p => p.Page).Should().Equal(2, 3, 4);

        _clock.Advance(TimeSpan.FromDays(30));
        var again = new PagingJob();
        var third = Scheduler(again);
        await third.EnrolAsync(default);
        (await RunDueAsync(third)).Should().Be(0);
        again.Pages.Should().BeEmpty("a row the job finished is never due again");
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().BeNull();
    }

    [Fact]
    public async Task Two_schedulers_on_one_ledger_never_run_a_row_twice()
    {
        var tenants = new List<Guid>();
        for (var i = 0; i < 6; i++)
            tenants.Add(await SeedTenantAsync());
        var job = new GatedJob();
        var a = Scheduler(job);
        var b = Scheduler(job);
        await a.EnrolAsync(default);

        var claimed = await Task.WhenAll(a.PollAsync(default), b.PollAsync(default));
        await job.AllStarted(6).WaitAsync(TimeSpan.FromSeconds(30));
        job.Release();
        await Task.WhenAll(a.DrainAsync(), b.DrainAsync());

        claimed.Sum().Should().Be(6);
        job.Overlapped.Should().BeFalse();
        job.Runs.Should().BeEquivalentTo(tenants);
        (await Task.WhenAll(a.PollAsync(default), b.PollAsync(default))).Sum().Should().Be(0);
    }

    [Fact]
    public async Task A_row_whose_holder_died_is_claimed_once_its_lease_runs_out()
    {
        var tenant = await SeedTenantAsync();
        var job = new PagingJob { DoneAfter = 1 };
        var survivor = Scheduler(job);
        await survivor.EnrolAsync(default);
        var ledger = Ledger();
        var dead = (await ledger.ClaimDueAsync("dead", [PagingJob.JobName], 1, _clock.GetUtcNow(), _clock.GetUtcNow() + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(JobScheduler.LeaseLength - TimeSpan.FromSeconds(1));
        (await RunDueAsync(survivor)).Should().Be(0, "the dead holder's lease has not run out");

        _clock.Advance(TimeSpan.FromSeconds(2));
        (await RunDueAsync(survivor)).Should().Be(1);
        job.Pages.Should().Equal([(tenant, (int?)null)]);

        await ledger.CompleteAsync(dead, _clock.GetUtcNow(), null, _clock.GetUtcNow(), default);
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().BeNull("a holder that lost the row writes nothing");
    }

    [Fact]
    public async Task A_failing_job_keeps_its_cursor_and_backs_off_one_two_four_minutes_up_to_an_hour()
    {
        var tenant = await SeedTenantAsync();
        var job = new FailingJob(failures: 8);
        var scheduler = Scheduler(job);
        await scheduler.EnrolAsync(default);
        (await RunDueAsync(scheduler)).Should().Be(1, "the first run succeeds and leaves a cursor");

        var expected = new[] { 1, 2, 4, 8, 16, 32, 60, 60 };
        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            (await RunDueAsync(scheduler)).Should().Be(1);
            var row = await RowAsync(FailingJob.JobName, tenant);
            row.Attempts.Should().Be(attempt);
            row.LastError.Should().Be($"InvalidOperationException: failure {attempt}");
            (row.NextDueAt - _clock.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(expected[attempt - 1]));

            _clock.Advance(TimeSpan.FromMinutes(expected[attempt - 1]) - TimeSpan.FromSeconds(1));
            (await RunDueAsync(scheduler)).Should().Be(0, "the row is not due before its backoff ends");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        (await RunDueAsync(scheduler)).Should().Be(1);
        var healed = await RowAsync(FailingJob.JobName, tenant);
        healed.Attempts.Should().Be(0);
        healed.LastError.Should().BeNull();
        job.Cursors.Skip(1).Should().OnlyContain(c => c == FailingJob.Cursor, "every retry resumes from the cursor the last success left");
    }

    [Fact]
    public async Task A_job_with_a_lower_cap_backs_off_no_longer_than_it()
    {
        var tenant = await SeedTenantAsync();
        var scheduler = Scheduler(new FailingJob(failures: 3) { MaxBackoff = TimeSpan.FromMinutes(3) });
        await scheduler.EnrolAsync(default);
        await RunDueAsync(scheduler);

        var waits = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await RunDueAsync(scheduler);
            var wait = (await RowAsync(FailingJob.JobName, tenant)).NextDueAt!.Value - _clock.GetUtcNow();
            waits.Add(wait);
            _clock.Advance(wait);
        }

        waits.Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task A_recurring_job_is_not_run_again_by_a_restart_before_it_is_due()
    {
        var tenant = await SeedTenantAsync();
        var job = new DailyJob();
        var first = Scheduler(job);
        await first.EnrolAsync(default);
        (await RunDueAsync(first)).Should().Be(1);

        _clock.Advance(TimeSpan.FromHours(23));
        var restarted = Scheduler(job);
        await restarted.EnrolAsync(default);
        (await RunDueAsync(restarted)).Should().Be(0, "the restart replays nothing that is not yet due");

        _clock.Advance(TimeSpan.FromHours(1));
        (await RunDueAsync(restarted)).Should().Be(1);
        job.Runs.Should().Be(2);
        (await RowAsync(DailyJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromDays(2));
    }

    [Fact]
    public async Task A_run_cancelled_by_shutdown_releases_its_row_for_the_next_holder_at_once()
    {
        var tenant = await SeedTenantAsync();
        var job = new GatedJob();
        var stopping = Scheduler(job);
        await stopping.EnrolAsync(default);
        using var shutdown = new CancellationTokenSource();

        (await stopping.PollAsync(shutdown.Token)).Should().Be(1);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        await shutdown.CancelAsync();
        await stopping.DrainAsync();

        var row = await RowAsync(GatedJob.JobName, tenant);
        row.LeaseHolder.Should().BeNull();
        row.Attempts.Should().Be(0, "a shutdown is not a failure");
        row.NextDueAt.Should().Be(Start);

        job.Release();
        (await RunDueAsync(Scheduler(job))).Should().Be(1);
    }

    [Fact]
    public async Task A_row_naming_a_job_this_instance_lacks_is_left_for_an_instance_that_has_it()
    {
        var tenant = await SeedTenantAsync();
        await Ledger().RequestAsync("newer-job", tenant, default);

        (await RunDueAsync(Scheduler(new DailyJob()))).Should().Be(0);

        var row = await RowAsync("newer-job", tenant);
        row.LeaseHolder.Should().BeNull();
        row.Attempts.Should().Be(0);
        row.NextDueAt.Should().Be(Start);
    }

    [Fact]
    public async Task A_claim_that_lost_its_row_writes_nothing_even_when_the_same_process_reclaimed_it()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var first = (await ledger.ClaimDueAsync("process", [PagingJob.JobName], 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(JobScheduler.LeaseLength + TimeSpan.FromSeconds(1));
        var now = _clock.GetUtcNow();
        var second = (await ledger.ClaimDueAsync("process", [PagingJob.JobName], 1, now, now + JobScheduler.LeaseLength, default)).Single();

        second.Holder.Should().NotBe(first.Holder);
        await ledger.CompleteAsync(first, now, null, null, default);
        await ledger.FailAsync(first, "late", now.AddHours(1), default);
        await ledger.ReleaseAsync(first, default);
        (await ledger.ExtendLeaseAsync(first, now.AddHours(1), default)).Should().BeFalse();

        var row = await RowAsync(PagingJob.JobName, tenant);
        row.LeaseHolder.Should().Be(second.Holder);
        row.LeaseUntil.Should().Be(now + JobScheduler.LeaseLength);
        row.NextDueAt.Should().Be(Start);
        row.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task The_heartbeat_keeps_a_long_run_leased_past_its_first_lease()
    {
        var tenant = await SeedTenantAsync();
        var job = new GatedJob();
        var running = Scheduler(job);
        await running.EnrolAsync(default);
        await running.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));

        _clock.Advance(JobScheduler.HeartbeatInterval);
        await WaitForAsync(async () =>
            (await RowAsync(GatedJob.JobName, tenant)).LeaseUntil == Start + JobScheduler.HeartbeatInterval + JobScheduler.LeaseLength);
        _clock.Advance(JobScheduler.LeaseLength - JobScheduler.HeartbeatInterval + TimeSpan.FromSeconds(10));

        (await RunDueAsync(Scheduler(job))).Should().Be(0, "the heartbeat extended the lease past its first expiry");
        job.Release();
        await running.DrainAsync();
        job.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_run_whose_lease_was_taken_is_cancelled_and_leaves_the_row_to_the_new_holder()
    {
        var tenant = await SeedTenantAsync();
        var job = new GatedJob();
        var running = Scheduler(job);
        await running.EnrolAsync(default);
        await running.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        await using (var conn = new NpgsqlConnection(_migrator))
        {
            await conn.OpenAsync();
            await using var steal = new NpgsqlCommand("UPDATE job_ledger SET lease_holder = 'other:claim'", conn);
            await steal.ExecuteNonQueryAsync();
        }

        _clock.Advance(JobScheduler.HeartbeatInterval);
        await running.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30));

        job.Cancelled.Should().Be(1);
        job.Runs.Should().BeEmpty();
        var row = await RowAsync(GatedJob.JobName, tenant);
        row.LeaseHolder.Should().Be("other:claim");
        row.Attempts.Should().Be(0);
        row.LastError.Should().BeNull();
        row.NextDueAt.Should().Be(Start);
    }

    [Fact]
    public async Task A_request_reopens_a_finished_row_and_one_made_mid_run_survives_the_runs_outcome()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        var job = new GatedJob();
        var scheduler = Scheduler(job);

        await scheduler.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        job.Release();
        await scheduler.DrainAsync();

        (await RowAsync(GatedJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromMinutes(1));
        (await RunDueAsync(scheduler)).Should().Be(1);
        (await RowAsync(GatedJob.JobName, tenant)).NextDueAt.Should().BeNull();

        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        (await RunDueAsync(scheduler)).Should().Be(1);
        job.Runs.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_request_made_during_a_run_that_fails_is_not_held_back_by_the_backoff()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        var job = new GatedJob { Fails = true };
        var scheduler = Scheduler(job);

        await scheduler.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromSeconds(30));
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        job.Release();
        await scheduler.DrainAsync();

        var row = await RowAsync(GatedJob.JobName, tenant);
        row.Attempts.Should().Be(1);
        row.NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(30), "the request is due before the one-minute backoff");
    }

    [Fact]
    public async Task A_request_on_a_row_whose_holder_died_does_not_push_it_later()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        await ledger.ClaimDueAsync("dead", [PagingJob.JobName], 1, Start, Start + JobScheduler.LeaseLength, default);

        _clock.Advance(JobScheduler.LeaseLength + TimeSpan.FromMinutes(1));
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start);
    }

    [Fact]
    public async Task A_platform_job_has_one_row_however_often_it_is_enrolled()
    {
        var job = new PlatformJob();
        var scheduler = Scheduler(job);
        await scheduler.EnrolAsync(default);
        await Scheduler(job).EnrolAsync(default);
        await Ledger().RequestAsync(PlatformJob.JobName, null, default);

        (await Ledger().ListAsync(default)).Where(r => r.Job == PlatformJob.JobName)
            .Should().ContainSingle().Which.TenantId.Should().BeNull();
        (await RunDueAsync(scheduler)).Should().Be(1);
        job.Tenants.Should().Equal([(TenantContext?)null]);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(20);
        }
    }

    private async Task<int> RunDueAsync(JobScheduler scheduler)
    {
        var started = await scheduler.PollAsync(default);
        await scheduler.DrainAsync();
        return started;
    }

    private JobScheduler Scheduler(params IScheduledJob[] jobs)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(_app).Options;
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(new Contexts(options));
        services.AddSingleton(sp => sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().AsNocturneFactory());
        services.AddSingleton<TimeProvider>(_clock);
        services.AddTransient<IJobLedger, JobLedgerRepository>();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new JobScheduler(
            jobs,
            provider.GetRequiredService<IJobLedger>(),
            provider,
            _clock,
            new JobMetrics(provider.GetRequiredService<IMeterFactory>()),
            Options.Create(new JobSchedulerOptions()),
            NullLogger<JobScheduler>.Instance);
    }

    private IJobLedger Ledger() => new JobLedgerRepository(
        new Contexts(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(_app).Options).AsNocturneFactory(),
        _clock);

    private async Task<JobLedgerEntry> RowAsync(string job, Guid tenant) =>
        (await Ledger().ListAsync(default)).Single(r => r.Job == job && r.TenantId == tenant);

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES (@id, @slug, 'T', true, now(), now())", conn);
        insert.Parameters.AddWithValue("id", tenant);
        insert.Parameters.AddWithValue("slug", tenant.ToString("N"));
        await insert.ExecuteNonQueryAsync();
        return tenant;
    }

    private sealed class Contexts(DbContextOptions<NocturneDbContext> options) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    /// <summary>Walks pages 1 to <see cref="DoneAfter"/>, one a run.</summary>
    private sealed class PagingJob : IScheduledJob
    {
        public const string JobName = "test-paging";

        public int DoneAfter { get; init; } = 4;

        public ConcurrentQueue<(Guid Tenant, int? Page)> Pages { get; } = new();

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Pages.Enqueue((run.Tenant!.TenantId, run.Cursor?.Page));
            var page = run.Cursor?.Page ?? 1;
            return Task.FromResult(page >= DoneAfter
                ? JobOutcome.Done
                : JobOutcome.Resume(new JobCursor(Page: page + 1), run.StartedAt));
        }
    }

    /// <summary>Holds every run until <see cref="Release"/>, and notes whether two runs of one row overlapped.</summary>
    private sealed class GatedJob : IScheduledJob
    {
        public const string JobName = "test-gated";

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<Guid, int> _running = new();
        private int _started;
        private int _cancelled;

        public int Cancelled => Volatile.Read(ref _cancelled);

        public ConcurrentQueue<Guid> Runs { get; } = new();

        public bool Overlapped { get; private set; }

        public bool Fails { get; init; }

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public void Release() => _gate.TrySetResult();

        public async Task AllStarted(int count)
        {
            while (Volatile.Read(ref _started) < count)
                await Task.Delay(10);
        }

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            var tenant = run.Tenant!.TenantId;
            if (_running.AddOrUpdate(tenant, 1, (_, n) => n + 1) > 1)
                Overlapped = true;
            Interlocked.Increment(ref _started);
            try
            {
                await _gate.Task.WaitAsync(ct);
                if (Fails)
                    throw new InvalidOperationException("gated run failed");
                Runs.Enqueue(tenant);
                return JobOutcome.Done;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally
            {
                _running.AddOrUpdate(tenant, 0, (_, n) => n - 1);
            }
        }
    }

    /// <summary>Succeeds once leaving <see cref="Cursor"/>, fails <c>failures</c> times, then succeeds.</summary>
    private sealed class FailingJob(int failures) : IScheduledJob
    {
        public const string JobName = "test-failing";

        public static readonly JobCursor Cursor = new(Page: 7);

        private int _runs;

        public ConcurrentQueue<JobCursor?> Cursors { get; } = new();

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public TimeSpan MaxBackoff { get; init; } = IScheduledJob.DefaultMaxBackoff;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Cursors.Enqueue(run.Cursor);
            var runs = Interlocked.Increment(ref _runs);
            if (runs > 1 && runs <= failures + 1)
                throw new InvalidOperationException($"failure {runs - 1}");
            return Task.FromResult(JobOutcome.Resume(Cursor, run.StartedAt));
        }
    }

    private sealed class DailyJob : IScheduledJob
    {
        public const string JobName = "test-daily";

        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(JobOutcome.Resume(null, run.StartedAt + TimeSpan.FromDays(1)));
        }
    }

    private sealed class PlatformJob : IScheduledJob
    {
        public const string JobName = "test-platform";

        public ConcurrentQueue<TenantContext?> Tenants { get; } = new();

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.Platform;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Tenants.Enqueue(run.Tenant);
            return Task.FromResult(JobOutcome.Done);
        }
    }
}
