using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Nightscout's v3 delete keeps the document with <c>isValid: false</c> and a new <c>srvModified</c>,
/// so a client syncing through <c>history</c> is told of it. The bulk soft delete therefore moves the
/// write stamp a history read pages on, as a tracked save would, spread over milliseconds so no one
/// millisecond holds more than a history page can take.
/// </summary>
[Trait("Category", "Unit")]
public class SoftDeleteHistoryStampTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Written = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    public SoftDeleteHistoryStampTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task BulkSoftDelete_MovesTheWriteStamp_AtMostOneGroupPerMillisecond()
    {
        var total = NocturneDbContext.SystemTimestampGroupSize + 1;
        var ids = await SeedAsync(total, legacyId: null);

        await using var context = _db.CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.ApsSnapshots.Where(a => a.AidAlgorithm == "Loop"),
            SystemAuditContext.ForService("connector:test"),
            "scope=test");

        deleted.Should().Be(total);

        await using var verify = _db.CreateContext();
        var rows = await verify.ApsSnapshots.IgnoreQueryFilters().OrderBy(a => a.Id).ToListAsync();
        var deletedAt = rows[0].DeletedAt!.Value;
        rows.Should().OnlyContain(a => a.DeletedAt == deletedAt);
        rows.Take(NocturneDbContext.SystemTimestampGroupSize).Should().OnlyContain(a => a.SysUpdatedAt == deletedAt);
        rows[^1].SysUpdatedAt.Should().Be(deletedAt.AddMilliseconds(1));
        rows.Select(a => a.Id).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task DeleteByLegacyId_IsReadBackByTheHistory_FlaggedDeleted_AtItsDelete()
    {
        await SeedAsync(1, legacyId: "65f000000000000000000aaa");
        var repository = new ApsSnapshotRepository(
            new TestTenantDbContextFactory(_context),
            new SystemAuditContext(),
            NullLogger<ApsSnapshotRepository>.Instance);
        var cursor = new DateTimeOffset(Written, TimeSpan.Zero).ToUnixTimeMilliseconds();

        (await repository.GetModifiedSinceAsync(cursor, 1000)).Should().BeEmpty();

        (await repository.DeleteByLegacyIdAsync("65f000000000000000000aaa", WriteOrigin.Live)).Should().Be(1);

        var row = (await repository.GetModifiedSinceAsync(cursor, 1000)).Should().ContainSingle().Subject;
        row.Deleted.Should().BeTrue();
        row.Record.LegacyId.Should().Be("65f000000000000000000aaa");
        row.Record.ModifiedAt.Should().BeAfter(Written);
    }

    private async Task<List<Guid>> SeedAsync(int count, string? legacyId)
    {
        var entities = Enumerable.Range(0, count).Select(i => new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyId,
            Timestamp = Written.AddMinutes(-i),
            AidAlgorithm = "Loop",
        }).ToList();

        _context.ApsSnapshots.AddRange(entities);
        await _context.SaveChangesAsync();

        foreach (var entity in entities)
            entity.SysUpdatedAt = Written;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return entities.Select(e => e.Id).ToList();
    }
}
