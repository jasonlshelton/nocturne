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

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// <see cref="JobScheduler"/>'s dispatch over a mocked ledger: what it claims, the scope it runs a job
/// in, and what it writes back. <see cref="JobSchedulerPostgresTests"/> runs it over the real ledger.
/// </summary>
[Trait("Category", "Unit")]
public class JobSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TenantContext Tenant = new(Guid.CreateVersion7(), "t", "T", true, IsDemo: false);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly Mock<IJobLedger> _ledger = new();

    [Fact]
    public void Two_jobs_of_one_name_fail_the_scheduler_at_construction()
    {
        var act = () => Scheduler(new ProbeJob("same"), new ProbeJob("same"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*same*");
    }

    [Fact]
    public async Task It_claims_no_more_rows_than_it_has_free_slots()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var claimed = new List<int>();
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyCollection<string> _, int limit, DateTimeOffset _, DateTimeOffset _, CancellationToken _) =>
            {
                claimed.Add(limit);
                return Enumerable.Range(0, limit).Select(_ => Lease("probe")).ToList();
            });

        await scheduler.PollAsync(default);
        await scheduler.PollAsync(default);
        job.Gate.SetResult();
        await scheduler.DrainAsync();
        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        claimed.Should().Equal(JobScheduler.Parallelism, JobScheduler.Parallelism);
        job.Runs.Should().HaveCount(2 * JobScheduler.Parallelism);
    }

    [Fact]
    public async Task A_job_runs_in_a_scope_of_its_tenant_attributed_to_the_system_as_the_job()
    {
        var job = new ProbeJob("probe");
        var scheduler = Scheduler(job);
        var lease = Lease("probe", new JobCursor(Page: 3), attempts: 2);
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        var run = job.Runs.Should().ContainSingle().Subject;
        run.Tenant.Should().Be(Tenant);
        run.Cursor.Should().Be(new JobCursor(Page: 3));
        run.Attempts.Should().Be(2);
        run.StartedAt.Should().Be(Now);
        job.SeenTenant.Should().Be(Tenant);
        job.SeenAudit.Should().Be((true, "job:probe"));
        _ledger.Verify(l => l.CompleteAsync(lease, Now, null, null, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_resumed_outcome_writes_its_cursor_and_due_time()
    {
        var next = new JobCursor(Page: 4);
        var scheduler = Scheduler(new ProbeJob("probe") { Outcome = JobOutcome.Resume(next, Now.AddHours(1)) });
        var lease = Lease("probe");
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.CompleteAsync(lease, Now, next, Now.AddHours(1), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_failure_backs_off_from_the_rows_attempts_and_keeps_the_error_short()
    {
        var scheduler = Scheduler(new ProbeJob("probe") { Throw = new InvalidOperationException(new string('x', 5000)) });
        var lease = Lease("probe", attempts: 2);
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.FailAsync(
            lease,
            It.Is<string>(e => e.StartsWith("InvalidOperationException: xxx") && e.Length == 2000),
            Now.AddMinutes(4),
            It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.CompleteAsync(It.IsAny<JobLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<JobCursor?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_job_that_throws_its_own_cancellation_is_failed_not_released()
    {
        var scheduler = Scheduler(new ProbeJob("probe") { Throw = new OperationCanceledException("gave up") });
        var lease = Lease("probe", attempts: 1);
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.FailAsync(
            lease, "OperationCanceledException: gave up", Now.AddMinutes(2), It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.ReleaseAsync(It.IsAny<JobLease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_job_that_turns_shutdown_into_another_exception_is_released_not_failed()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource(), WrapsCancellation = true };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        using var shutdown = new CancellationTokenSource();

        await scheduler.PollAsync(shutdown.Token);
        await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await shutdown.CancelAsync();
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.ReleaseAsync(lease, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.FailAsync(It.IsAny<JobLease>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Shutdown_releases_the_lease_and_records_no_failure()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        using var shutdown = new CancellationTokenSource();

        await scheduler.PollAsync(shutdown.Token);
        await shutdown.CancelAsync();
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.ReleaseAsync(lease, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.FailAsync(It.IsAny<JobLease>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task A_lease_extension_that_throws_is_retried_at_the_next_beat(Type thrown)
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        var extensions = 0;
        _ledger.Setup(l => l.ExtendLeaseAsync(lease, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref extensions) == 1
                ? Task.FromException<bool>((Exception)Activator.CreateInstance(thrown)!)
                : Task.FromResult(true));

        await scheduler.PollAsync(default);
        await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref extensions) < 3 && DateTime.UtcNow < deadline)
        {
            _clock.Advance(JobScheduler.HeartbeatInterval);
            await Task.Delay(20);
        }

        job.Gate.SetResult();
        await scheduler.DrainAsync();

        extensions.Should().BeGreaterThanOrEqualTo(3, "the beats after a failed extension still extend the lease");
        _ledger.Verify(l => l.CompleteAsync(lease, It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()));
        _ledger.Invocations.Clear();
        await scheduler.PollAsync(default);
        _ledger.Verify(l => l.ClaimDueAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), JobScheduler.Parallelism,
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), "the run gave its slot back");
    }

    [Fact]
    public async Task A_lost_lease_cancels_the_run_and_records_nothing()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        _ledger.Setup(l => l.ExtendLeaseAsync(lease, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await scheduler.PollAsync(default);
        await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(JobScheduler.HeartbeatInterval);
        await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        job.Gate.Task.IsCompleted.Should().BeFalse("the run ended because its lease was lost, not because it finished");
        _ledger.Verify(l => l.CompleteAsync(It.IsAny<JobLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<JobCursor?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        _ledger.Verify(l => l.FailAsync(It.IsAny<JobLease>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _ledger.Verify(l => l.ReleaseAsync(It.IsAny<JobLease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enrolment_gives_each_job_the_rows_its_enrolment_names()
    {
        var scheduler = Scheduler(
            new ProbeJob("tenants"),
            new ProbeJob("platform") { Enrolment = JobEnrolment.Platform },
            new ProbeJob("requested") { Enrolment = JobEnrolment.OnRequest });

        await scheduler.EnrolAsync(default);

        _ledger.Verify(l => l.EnrolPerTenantAsync("tenants", Now, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.EnrolPlatformAsync("platform", Now, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.EnrolPerTenantAsync("requested", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _ledger.Verify(l => l.EnrolPlatformAsync("requested", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(1, 60, 1)]
    [InlineData(2, 60, 2)]
    [InlineData(3, 60, 4)]
    [InlineData(7, 60, 60)]
    [InlineData(40, 60, 60)]
    [InlineData(3, 3, 3)]
    [InlineData(3, 600, 4)]
    [InlineData(9, 600, 60)]
    public void Backoff_doubles_from_a_minute_up_to_the_lower_cap(int attempts, int capMinutes, int expectedMinutes) =>
        JobScheduler.Backoff(attempts, TimeSpan.FromMinutes(capMinutes)).Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    private void Claims(JobLease lease)
    {
        _ledger.SetupSequence(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([lease])
            .ReturnsAsync([]);
    }

    private static JobLease Lease(string job, JobCursor? cursor = null, int attempts = 0) =>
        new(Guid.CreateVersion7(), job, Tenant, cursor, attempts, Now, $"test:{Guid.NewGuid():N}");

    private JobScheduler Scheduler(params IScheduledJob[] jobs)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        var provider = services.BuildServiceProvider();
        return new JobScheduler(
            jobs,
            _ledger.Object,
            provider,
            _clock,
            new JobMetrics(provider.GetRequiredService<IMeterFactory>()),
            NullLogger<JobScheduler>.Instance);
    }

    private sealed class ProbeJob(string name) : IScheduledJob
    {
        public string Name => name;

        public JobEnrolment Enrolment { get; init; } = JobEnrolment.PerTenant;

        public TaskCompletionSource? Gate { get; init; }

        public bool WrapsCancellation { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobOutcome Outcome { get; init; } = JobOutcome.Done;

        public Exception? Throw { get; init; }

        public System.Collections.Concurrent.ConcurrentQueue<JobRun> Runs { get; } = new();

        public TenantContext? SeenTenant { get; private set; }

        public (bool IsSystem, string? Endpoint) SeenAudit { get; private set; }

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Runs.Enqueue(run);
            SeenTenant = run.Services.GetRequiredService<ITenantAccessor>().Context;
            var audit = run.Services.GetRequiredService<IAuditContext>();
            SeenAudit = (audit.IsSystem, audit.Endpoint);
            Started.TrySetResult();
            if (Gate is not null)
            {
                try
                {
                    await Gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (WrapsCancellation)
                {
                    throw new InvalidOperationException("the aborted call failed");
                }
            }

            if (Throw is not null)
                throw Throw;
            return Outcome;
        }
    }
}
