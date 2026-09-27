using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly StatementRecorder _statements = new();

    public SoftDeleteHistoryStampTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test", _statements);
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task BulkSoftDelete_MovesTheWriteStamp_AtMostOneGroupPerMillisecond_InOneReadAndOneUpdatePerGroup()
    {
        var group = NocturneDbContext.SystemTimestampGroupSize;
        var total = 2 * group + 1;
        var ids = await SeedAsync(total, legacyId: null);

        await using var context = _db.CreateContext();
        _statements.Clear();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.ApsSnapshots.Where(a => a.AidAlgorithm == "Loop"),
            SystemAuditContext.ForService("connector:test"),
            "scope=test");

        deleted.Should().Be(total);
        _statements.Count("SELECT").Should().Be(1, "the match set is read once, not once per group");
        _statements.Count("UPDATE").Should().Be(3);

        await using var verify = _db.CreateContext();
        var rows = await verify.ApsSnapshots.IgnoreQueryFilters().OrderBy(a => a.Id).ToListAsync();
        var deletedAt = rows[0].DeletedAt!.Value;
        rows.Should().OnlyContain(a => a.DeletedAt == deletedAt);
        rows.Take(group).Should().OnlyContain(a => a.SysUpdatedAt == deletedAt);
        rows.Skip(group).Take(group).Should().OnlyContain(a => a.SysUpdatedAt == deletedAt.AddMilliseconds(1));
        rows[^1].SysUpdatedAt.Should().Be(deletedAt.AddMilliseconds(2));
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

    [Fact]
    public async Task DeletingADuplicateGroupsPrimary_RepointsIt_AndMovesThePromotedCopysStamp()
    {
        // Two sources' copies of one note, linked into a group led by the first. Once the primary
        // is deleted the other copy leads the group, and a history client has to be sent it.
        var canonical = Guid.CreateVersion7();
        var primary = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Written, Text = "a" };
        var copy = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Written, Text = "b" };
        _context.Notes.AddRange(primary, copy);
        _context.LinkedRecords.AddRange(
            Link(canonical, primary.Id, isPrimary: true, "source-a"),
            Link(canonical, copy.Id, isPrimary: false, "source-b"));
        await _context.SaveChangesAsync();
        primary.SysUpdatedAt = Written;
        copy.SysUpdatedAt = Written;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var deduplication = new Mock<Core.Contracts.Infrastructure.IDeduplicationService>();
        deduplication
            .Setup(d => d.RepointPrimariesAwayFromAsync(RecordType.Note, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var ctx = _db.CreateContext();
                foreach (var link in await ctx.LinkedRecords.ToListAsync())
                    link.IsPrimary = link.RecordId == copy.Id;
                await ctx.SaveChangesAsync();
            });
        var repository = new NoteRepository(
            new TestTenantDbContextFactory(_context), deduplication.Object, new SystemAuditContext(),
            NullLogger<NoteRepository>.Instance);
        var cursor = new DateTimeOffset(Written, TimeSpan.Zero).ToUnixTimeMilliseconds();

        await repository.DeleteAsync(primary.Id, WriteOrigin.Live);

        var page = await repository.GetModifiedSinceAsync(cursor, 1000);
        page.Select(r => (r.Record.Id, r.Deleted)).Should().BeEquivalentTo(new[] { (primary.Id, true), (copy.Id, false) });
        (await repository.GetByIdAsync(copy.Id)).Should().NotBeNull();
    }

    private static LinkedRecordEntity Link(Guid canonical, Guid recordId, bool isPrimary, string source) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, CanonicalId = canonical,
        RecordType = RecordTypeKeys.Key(RecordType.Note), RecordId = recordId, DataSource = source, IsPrimary = isPrimary,
    };

    /// <summary>Counts the statements the connection runs, by leading keyword.</summary>
    private sealed class StatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        public void Clear() => _statements.Clear();

        public int Count(string keyword) =>
            _statements.Count(s => s.StartsWith(keyword, StringComparison.OrdinalIgnoreCase));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText.TrimStart());
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText.TrimStart());
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
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
