using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Nocturne.API.Services.Demo;

/// <summary>
/// The one owner of "everything the demo service wrote": the table list and the delete that empties
/// it, for every caller that clears demo data. Takes the context so a caller may bring either
/// tenant-scoping pattern — a context reading as the scope's tenant, or one pinned to the demo
/// tenant the caller looked up.
/// </summary>
/// <remarks>
/// Hard delete (<see cref="PurgeExtensions.PurgeAsync{TEntity}"/>), unlike the audited soft-delete
/// every other source's delete routes through: demo data is regenerated wholesale, and the primary
/// path that does so — <see cref="DemoTenantService.ResetAsync"/> — drops the tenant row and lets the
/// database cascade clear every tenant-scoped table before the seeder refills it. A
/// <c>deleted_by_user</c> stamp would only block that regeneration, and the audit trail it would be
/// written for does not survive the reset either.
/// <para>
/// A caller tells <see cref="ILegacyReadCacheInvalidation"/> what a purge emptied through
/// <see cref="DemoPurgeWrites"/>.
/// </para>
/// </remarks>
internal static class DemoDataPurge
{
    internal static readonly Type[] GlucoseModels = [typeof(SensorGlucose), typeof(MeterGlucose), typeof(Calibration)];

    internal static readonly Type[] TreatmentModels =
    [
        typeof(Bolus), typeof(CarbIntake), typeof(BGCheck), typeof(Note), typeof(DeviceEvent),
        typeof(BolusCalculation), typeof(TempBasal), typeof(ProfileActivation), typeof(StateSpan),
        typeof(CompressionLowSuggestion), typeof(LabHbA1cResult),
    ];

    internal static readonly Type[] DeviceStatusModels = [typeof(ApsSnapshot)];

    /// <summary>Glucose the demo service wrote: sensor, meter and calibration readings.</summary>
    public static async Task<long> PurgeEntriesAsync(NocturneDbContext db, CancellationToken ct)
    {
        long deleted = await db.SensorGlucose.PurgeAsync(SourceFilter.For<SensorGlucoseEntity>(DataSources.DemoService), ct);
        deleted += await db.MeterGlucose.PurgeAsync(SourceFilter.For<MeterGlucoseEntity>(DataSources.DemoService), ct);
        deleted += await db.Calibrations.PurgeAsync(SourceFilter.For<CalibrationEntity>(DataSources.DemoService), ct);
        return deleted;
    }

    /// <summary>
    /// Treatments and compression-low review data the demo service wrote.
    /// </summary>
    public static async Task<long> PurgeTreatmentsAsync(
        NocturneDbContext db, TimeProvider time, CancellationToken ct)
    {
        long deleted = await db.Boluses.PurgeAsync(SourceFilter.For<BolusEntity>(DataSources.DemoService), ct);
        deleted += await db.CarbIntakes.PurgeAsync(SourceFilter.For<CarbIntakeEntity>(DataSources.DemoService), ct);
        deleted += await db.BGChecks.PurgeAsync(SourceFilter.For<BGCheckEntity>(DataSources.DemoService), ct);
        deleted += await db.Notes.PurgeAsync(SourceFilter.For<NoteEntity>(DataSources.DemoService), ct);
        deleted += await db.DeviceEvents.PurgeAsync(SourceFilter.For<DeviceEventEntity>(DataSources.DemoService), ct);
        deleted += await db.BolusCalculations.PurgeAsync(SourceFilter.For<BolusCalculationEntity>(DataSources.DemoService), ct);
        deleted += await db.TempBasals.PurgeAsync(SourceFilter.For<TempBasalEntity>(DataSources.DemoService), ct);
        deleted += await db.ProfileActivations.PurgeAsync(SourceFilter.For<ProfileActivationEntity>(DataSources.DemoService), ct);
        var demoAcceptedSpanIds = (await db.StateSpans
                .Where(s => s.Source == "compression-low-detection" && s.MetadataJson != null)
                .Select(s => new { s.Id, s.MetadataJson })
                .ToListAsync(ct))
            .Where(span => HasDemoSeedMarker(span.MetadataJson))
            .Select(span => span.Id)
            .ToArray();
        deleted += await db.StateSpans.PurgeAsync(
            s => s.Source == DataSources.DemoService || demoAcceptedSpanIds.Contains(s.Id), ct);
        deleted += await db.CompressionLowSuggestions.PurgeAsync(
            s => s.DataSource == DataSources.DemoService, ct);
        deleted += await db.LabHbA1cResults
            .Where(result => result.Note == "Demo lab result")
            .ExecuteUpdateAsync(update => update.SetProperty(
                result => result.DeletedAt, time.GetUtcNow().UtcDateTime), ct);
        return deleted;
    }

    private static bool HasDemoSeedMarker(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.TryGetProperty("DemoSeed", out var marker)
                && marker.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>APS snapshots the demo service wrote.</summary>
    public static async Task<long> PurgeDeviceStatusAsync(NocturneDbContext db, CancellationToken ct) =>
        await db.ApsSnapshots.PurgeAsync(SourceFilter.For<ApsSnapshotEntity>(DataSources.DemoService), ct);
}

/// <summary>
/// The model types a demo purge may have deleted from, by group. A group counts once it deleted a row,
/// or once it started and threw, since each of its statements commits on its own and the failure
/// leaves how many unknown. A group that never started is not named.
/// </summary>
internal sealed class DemoPurgeWrites
{
    private const string AuditEndpoint = "service:demo-purge";

    private readonly HashSet<Type> _models = [];

    public Task<long> PurgeEntriesAsync(NocturneDbContext db, CancellationToken ct) =>
        RunAsync(DemoDataPurge.GlucoseModels, () => DemoDataPurge.PurgeEntriesAsync(db, ct));

    public Task<long> PurgeTreatmentsAsync(NocturneDbContext db, TimeProvider time, CancellationToken ct) =>
        RunAsync(DemoDataPurge.TreatmentModels, () => DemoDataPurge.PurgeTreatmentsAsync(db, time, ct));

    public Task<long> PurgeDeviceStatusAsync(NocturneDbContext db, CancellationToken ct) =>
        RunAsync(DemoDataPurge.DeviceStatusModels, () => DemoDataPurge.PurgeDeviceStatusAsync(db, ct));

    /// <summary>Tells <paramref name="legacyReads"/> the models the purge wrote, as the scope's tenant.</summary>
    public Task ReportAsync(ILegacyReadCacheInvalidation? legacyReads) =>
        _models.Count == 0 ? Task.CompletedTask : legacyReads?.WrittenAsync([.. _models]) ?? Task.CompletedTask;

    /// <summary>
    /// <see cref="ReportAsync"/> as <paramref name="tenant"/>, for a caller whose own scope resolved
    /// none and would clear nothing.
    /// </summary>
    public async Task ReportInTenantAsync(IServiceProvider services, TenantContext tenant)
    {
        if (_models.Count == 0)
            return;

        await using var job = TenantJobScope.Open(services, tenant, AuditEndpoint);
        await ReportAsync(job.ServiceProvider.GetRequiredService<ILegacyReadCacheInvalidation>());
    }

    private async Task<long> RunAsync(Type[] models, Func<Task<long>> purge)
    {
        var deleted = 0L;
        var completed = false;
        try
        {
            deleted = await purge();
            completed = true;
            return deleted;
        }
        finally
        {
            if (!completed || deleted > 0)
                _models.UnionWith(models);
        }
    }
}

/// <summary>
/// <see cref="DemoDataPurge"/> over one context of the scope's reader, for a caller that holds no
/// context of its own.
/// </summary>
public sealed class DemoDataPurger(
    INocturneDbContextFactory contexts, TimeProvider time, ILegacyReadCacheInvalidation? legacyReads = null)
{
    /// <summary>Hard-deletes everything the demo service wrote, returning the counts by group.</summary>
    public async Task<(long Glucose, long Treatments, long DeviceStatus)> PurgeAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var writes = new DemoPurgeWrites();
        try
        {
            var treatments = await writes.PurgeTreatmentsAsync(db, time, ct);
            var glucose = await writes.PurgeEntriesAsync(db, ct);
            var deviceStatus = await writes.PurgeDeviceStatusAsync(db, ct);
            return (glucose, treatments, deviceStatus);
        }
        finally
        {
            await writes.ReportAsync(legacyReads);
        }
    }
}
