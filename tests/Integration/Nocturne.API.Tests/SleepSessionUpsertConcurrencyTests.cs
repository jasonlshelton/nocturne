using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Two upserts of one sleep session, the first held uncommitted until the second is blocked. The
/// second must wait on the first and then replace its row; without the upsert lock it misses the
/// uncommitted row, inserts, and fails the unique index once the first commits.
/// </summary>
[Trait("Category", "Integration")]
public class SleepSessionUpsertConcurrencyTests : ApiIntegrationTestBase
{
    public SleepSessionUpsertConcurrencyTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task UpsertSessionAsync_SameSourceRecordWhileFirstUncommitted_WaitsAndReplacesIt()
    {
        var (first, second) = await RaceAsync(
            Session(id: null, originalId: "sleep-race-source", score: 70),
            Session(id: null, originalId: "sleep-race-source", score: 90));

        second.Id.Should().Be(first.Id);
        (await StoredAsync()).Should().ContainSingle()
            .Which.Should().Be((Guid.Parse(first.Id!), (string?)"sleep-race-source", (short?)90));
    }

    [Fact]
    public async Task UpsertSessionAsync_SameIdWithoutSourceRecordWhileFirstUncommitted_WaitsAndReplacesIt()
    {
        var id = Guid.CreateVersion7();

        var (first, second) = await RaceAsync(
            Session(id: id, originalId: "sleep-race-by-id", score: 70),
            Session(id: id, originalId: null, score: 90));

        first.Id.Should().Be(id.ToString());
        second.Id.Should().Be(id.ToString());
        (await StoredAsync()).Should().ContainSingle().Which.Should().Be((id, (string?)null, (short?)90));
    }

    private async Task<(SleepSession First, SleepSession Second)> RaceAsync(SleepSession first, SleepSession second)
    {
        TestTenantDbContextFactory contexts;
        await using (var seed = Fixture.CreateDbContext(Fixture.TenantId))
            contexts = new TestTenantDbContextFactory(seed);
        var held = await contexts.CreateAsync();
        var racer = await contexts.CreateAsync();
        try
        {
            Task<SleepSession>? secondTask = null;
            var firstResult = await held.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await held.Database.BeginTransactionAsync();
                var result = await new SleepSessionRepository(new Pinned(held)).UpsertSessionAsync(first);

                secondTask = new SleepSessionRepository(new Pinned(racer)).UpsertSessionAsync(second);
                await WaitUntilABackendWaitsOnALockAsync(secondTask);

                await transaction.CommitAsync();
                return result;
            });
            return (firstResult, await secondTask!.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await held.Database.CloseConnectionAsync();
            await racer.Database.CloseConnectionAsync();
        }
    }

    private async Task WaitUntilABackendWaitsOnALockAsync(Task racing)
    {
        await using var probe = Fixture.CreateDbContext(Fixture.TenantId);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (racing.IsCompleted)
                throw new InvalidOperationException("The second upsert finished while the first was uncommitted", racing.Exception);

            var waiting = await probe.Database
                .SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock'
                    """)
                .SingleAsync();
            if (waiting > 0)
                return;

            await Task.Delay(20);
        }

        throw new TimeoutException("The second upsert never blocked on the first");
    }

    private async Task<List<(Guid Id, string? OriginalId, short? SleepScore)>> StoredAsync()
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return (await db.SleepSessions.AsNoTracking().ToListAsync())
            .Select(s => (s.Id, s.OriginalId, s.SleepScore))
            .ToList();
    }

    private static SleepSession Session(Guid? id, string? originalId, short score) => new()
    {
        Id = id?.ToString(),
        StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
        EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
        Type = SleepSessionType.Overnight,
        DetectionMethod = SleepDetectionMethod.Manual,
        Source = SleepSource.Manual,
        DurationMs = 28_800_000,
        TotalSleepMs = 28_800_000,
        OriginalId = originalId,
        SleepScore = score,
    };

    private sealed class Pinned(NocturneDbContext context) : ITenantDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(context);
    }
}
