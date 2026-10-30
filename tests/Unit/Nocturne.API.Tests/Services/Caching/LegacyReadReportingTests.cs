using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Caching;

/// <summary>
/// The write paths of the V4 repositories that do not end in a single create, each reporting the
/// model it wrote to <see cref="ILegacyReadCacheInvalidation"/>. On SQLite, which runs the bulk
/// statements the InMemory provider cannot.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyReadReportingTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly RecordingLegacyReads _written = new();
    private readonly IDeduplicationService _dedup = Mock.Of<IDeduplicationService>();
    private readonly IAuditContext _audit = new SystemAuditContext();

    public LegacyReadReportingTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(Tenant);
        _context = _db.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private TempBasalRepository TempBasals() =>
        new(_factory, _dedup, _audit, NullLogger<TempBasalRepository>.Instance, legacyReads: _written);

    private BolusRepository Boluses() =>
        new(_factory, _dedup, _audit, NullLogger<BolusRepository>.Instance, legacyReads: _written);

    private BolusCalculationRepository Calculations() =>
        new(_factory, _dedup, _audit, NullLogger<BolusCalculationRepository>.Instance, legacyReads: _written);

    private MeterGlucoseRepository MeterGlucose() =>
        new(_factory, _audit, NullLogger<MeterGlucoseRepository>.Instance, legacyReads: _written);

    private static Bolus Bolus(string? legacyId = null, double insulin = 1) =>
        new() { Timestamp = At, Insulin = insulin, LegacyId = legacyId };

    [Fact]
    public async Task Setting_a_schedule_derived_rate_reports_temp_basals()
    {
        var repository = TempBasals();
        var temp = await repository.CreateAsync(new TempBasal { StartTimestamp = At, Percent = -50 }, WriteOrigin.Live);
        _written.Models.Clear();

        (await repository.SetScheduleDerivedRateAsync(temp.Id, -50, null, 0.5, 1.0)).Should().BeTrue();

        _written.Models.Should().Equal(typeof(TempBasal));
    }

    [Fact]
    public async Task Reconciling_a_sources_window_reports_temp_basals()
    {
        var repository = TempBasals();
        await repository.CreateAsync(
            new TempBasal { StartTimestamp = At, Rate = 0.8, DataSource = "pump", LegacyId = "gone" }, WriteOrigin.Live);
        _written.Models.Clear();

        (await repository.SoftDeleteAbsentBySourceAndDateRangeAsync("pump", At, At, new HashSet<string>())).Should().Be(1);

        _written.Models.Should().Equal(typeof(TempBasal));
    }

    [Fact]
    public async Task A_bulk_delete_too_large_to_broadcast_per_record_reports()
    {
        var repository = MeterGlucose();
        await repository.BulkCreateAsync(
            Enumerable.Range(0, AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1)
                .Select(i => new MeterGlucose { Timestamp = At.AddMinutes(i), Mgdl = 100 }),
            WriteOrigin.Backfill);
        _written.Models.Clear();

        var deleted = await repository.DeleteByTimeRangeAsync(null, null, WriteOrigin.Live);

        deleted.Should().BeGreaterThan(AuditedBulkDeleteExtensions.BroadcastMaterializationCap);
        _written.Models.Should().Equal(typeof(MeterGlucose));
    }

    [Fact]
    public async Task A_restore_reports()
    {
        var repository = Boluses();
        var bolus = await repository.CreateAsync(Bolus(), WriteOrigin.Live);
        await repository.DeleteAsync(bolus.Id, WriteOrigin.Live);
        _written.Models.Clear();

        await repository.RestoreAsync(bolus.Id, WriteOrigin.Live);

        _written.Models.Should().Equal(typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_restore_reports()
    {
        var repository = Boluses();
        var bolus = await repository.CreateAsync(Bolus(), WriteOrigin.Live);
        await repository.DeleteAsync(bolus.Id, WriteOrigin.Live);
        _written.Models.Clear();

        await repository.BulkRestoreAsync([bolus.Id], WriteOrigin.Live);

        _written.Models.Should().Equal(typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_create_reports()
    {
        await Boluses().BulkCreateAsync([Bolus("a"), Bolus("b")], WriteOrigin.Backfill);

        _written.Models.Should().NotBeEmpty().And.OnlyContain(model => model == typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_upsert_that_only_updates_reports()
    {
        var repository = Boluses();
        await repository.BulkCreateAsync([Bolus("a")], WriteOrigin.Live);
        _written.Models.Clear();

        await repository.BulkUpsertAsync([Bolus("a", insulin: 2)], WriteOrigin.Live);

        _written.Models.Should().NotBeEmpty().And.OnlyContain(model => model == typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_upsert_by_legacy_id_reports()
    {
        var repository = Calculations();
        await repository.BulkUpsertByLegacyIdAsync(
            [new BolusCalculation { Timestamp = At, LegacyId = "calc", CarbInput = 20 }], WriteOrigin.Live);
        _written.Models.Clear();

        await repository.BulkUpsertByLegacyIdAsync(
            [new BolusCalculation { Timestamp = At, LegacyId = "calc", CarbInput = 30 }], WriteOrigin.Live);

        _written.Models.Should().NotBeEmpty().And.OnlyContain(model => model == typeof(BolusCalculation));
    }

    [Fact]
    public async Task A_create_that_upserts_onto_its_sync_key_reports()
    {
        var repository = Boluses();
        await repository.CreateAsync(
            new Bolus { Timestamp = At, Insulin = 1, DataSource = "pump", SyncIdentifier = "s1" }, WriteOrigin.Live);
        _written.Models.Clear();

        var upsert = await repository.UpsertAsync(
            new Bolus { Timestamp = At, Insulin = 2, DataSource = "pump", SyncIdentifier = "s1" }, WriteOrigin.Live);

        upsert.Created.Should().BeFalse();
        _written.Models.Should().Equal(typeof(Bolus));
    }
}
