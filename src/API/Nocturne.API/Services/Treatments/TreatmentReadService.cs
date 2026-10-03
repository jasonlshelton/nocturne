using System.Text.Json;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Logging;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// V4-only <see cref="ITreatmentStore"/> that reads all treatments from V4 repositories
/// via the projection service and routes writes through the decomposer.
/// </summary>
public class TreatmentReadService : ITreatmentStore
{
    private readonly IV4ToLegacyProjectionService _projection;
    private readonly ITreatmentDecomposer _decomposer;
    private readonly IDecompositionPipeline _pipeline;
    private readonly ITempBasalRepository _tempBasalRepo;
    private readonly IBolusRepository _bolusRepo;
    private readonly ICarbIntakeRepository _carbIntakeRepo;
    private readonly IBGCheckRepository _bgCheckRepo;
    private readonly INoteRepository _noteRepo;
    private readonly IDeviceEventRepository _deviceEventRepo;
    private readonly IBolusCalculationRepository _bolusCalcRepo;
    private readonly ILogger<TreatmentReadService> _logger;

    public TreatmentReadService(
        IV4ToLegacyProjectionService projection,
        ITreatmentDecomposer decomposer,
        IDecompositionPipeline pipeline,
        ITempBasalRepository tempBasalRepo,
        IBolusRepository bolusRepo,
        ICarbIntakeRepository carbIntakeRepo,
        IBGCheckRepository bgCheckRepo,
        INoteRepository noteRepo,
        IDeviceEventRepository deviceEventRepo,
        IBolusCalculationRepository bolusCalcRepo,
        ILogger<TreatmentReadService> logger)
    {
        _projection = projection;
        _decomposer = decomposer;
        _pipeline = pipeline;
        _tempBasalRepo = tempBasalRepo;
        _bolusRepo = bolusRepo;
        _carbIntakeRepo = carbIntakeRepo;
        _bgCheckRepo = bgCheckRepo;
        _noteRepo = noteRepo;
        _deviceEventRepo = deviceEventRepo;
        _bolusCalcRepo = bolusCalcRepo;
        _logger = logger;
    }

    /// <summary>
    /// Upper bound on rows fetched into memory when a find query carries field filters, which can
    /// only be applied after projection and therefore defeat limit pushdown.
    /// </summary>
    internal int MaxFilterFetch { get; set; } = 100_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> QueryAsync(TreatmentQuery query, CancellationToken ct = default)
    {
        var find = FindQuery.Parse(query.Find);

        var results = find.HasFieldFilters
            ? await QueryFilteredAsync(find, query.Count, query.Skip, ct)
            : await QueryByTimeRangeAsync(find, query.Count, query.Skip, ct);

        if (query.ReverseResults)
            return results.OrderBy(t => t.Mills).ToList();

        return results;
    }

    private async Task<IReadOnlyList<Treatment>> QueryByTimeRangeAsync(
        FindQuery find, int count, int skip, CancellationToken ct)
    {
        var limit = (int)Math.Min((long)count + skip, int.MaxValue);
        var projected = await _projection.GetProjectedTreatmentsAsync(
            find.FromMills, find.ToMills, limit, nativeOnly: false, ct: ct);

        return projected
            .OrderByDescending(t => t.Mills)
            .Skip(skip)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Serves a find query with field filters (eventType, enteredBy, $exists, …) by matching the
    /// projected legacy shape. Paging cannot be pushed down past an in-memory filter, so the fetch
    /// window grows geometrically until the page fills or the window is exhausted.
    /// </summary>
    private async Task<IReadOnlyList<Treatment>> QueryFilteredAsync(
        FindQuery find, int count, int skip, CancellationToken ct)
    {
        var needed = (long)count + skip;
        var fetchLimit = (int)Math.Min(Math.Max(needed * 4, 100), MaxFilterFetch);

        while (true)
        {
            var projected = (await _projection.GetProjectedTreatmentsAsync(
                find.FromMills, find.ToMills, fetchLimit, nativeOnly: false, ct: ct)).ToList();

            var matching = projected.Where(find.Matches).ToList();
            var exhausted = projected.Count < fetchLimit || fetchLimit >= MaxFilterFetch;
            if (matching.Count >= needed || exhausted)
            {
                if (matching.Count < needed && projected.Count >= fetchLimit)
                    _logger.LogWarning(
                        "Find-filtered treatment query hit the {MaxFetch}-row window; older matches are not returned",
                        MaxFilterFetch);

                return matching
                    .OrderByDescending(t => t.Mills)
                    .Skip(skip)
                    .Take(count)
                    .ToList();
            }

            fetchLimit = (int)Math.Min((long)fetchLimit * 4, MaxFilterFetch);
        }
    }

    /// <inheritdoc />
    public async Task<Treatment?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var stored = await FindStoredRecordAsync(id, ct);
        return stored is null ? null : await ProjectAsync(stored.Record, ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default)
        => await _tempBasalRepo.IsDeletedByUserAsync(id, ct)
            || await _bolusRepo.IsDeletedByUserAsync(id, ct)
            || await _carbIntakeRepo.IsDeletedByUserAsync(id, ct)
            || await _bgCheckRepo.IsDeletedByUserAsync(id, ct)
            || await _deviceEventRepo.IsDeletedByUserAsync(id, ct)
            || await _bolusCalcRepo.IsDeletedByUserAsync(id, ct)
            || await _noteRepo.IsDeletedByUserAsync(id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> GetByRangeAsync(
        long fromMills, long toMills, CancellationToken ct = default)
    {
        // Project across all V4 treatment repositories; bounds are inclusive on both ends.
        // The projection service already orders newest-first internally, but we re-sort here
        // to make the contract explicit at the read boundary.
        var projected = await _projection.GetProjectedTreatmentsAsync(
            fromMills, toMills, limit: int.MaxValue, nativeOnly: false, ct: ct);

        return projected.OrderByDescending(t => t.Mills).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Treatment>> GetModifiedSinceAsync(
        long lastModifiedMills, int limit, CancellationToken ct = default)
    {
        var projected = await _projection.GetProjectedTreatmentsModifiedSinceAsync(
            lastModifiedMills, limit, ct);

        return projected.ToList();
    }

    /// <inheritdoc />
    public async Task<BulkWrite<Treatment>> CreateAsync(
        IReadOnlyList<Treatment> treatments, CancellationToken ct = default)
    {
        var results = new List<Treatment>();
        var skippedDeleted = 0;

        foreach (var treatment in treatments)
        {
            try
            {
                var result = await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live, ct);
                skippedDeleted += result.SkippedDeleted;
                var tempBasal = result.CreatedRecords
                    .OfType<Core.Models.V4.TempBasal>()
                    .FirstOrDefault();
                if (tempBasal != null)
                    results.Add(TempBasalToTreatmentMapper.ToTreatment(tempBasal));
                else
                    results.Add(treatment);
            }
            catch (OperationCanceledException)
            {
                // A canceled request (client disconnect, shutdown) is control flow,
                // not a bad treatment. Let it abort the batch — swallowing it here
                // turns one cancellation into a per-record "failure" logged for every
                // remaining treatment, since each subsequent DB call on the canceled
                // token throws too.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompose treatment {Id}", treatment.Id);
            }
        }

        _logger.LogSkippedDeleted(nameof(Treatment), skippedDeleted);
        return new BulkWrite<Treatment>(results, skippedDeleted);
    }

    /// <inheritdoc />
    public async Task<Treatment?> UpdateAsync(string id, Treatment treatment, CancellationToken ct = default)
    {
        var stored = await FindStoredRecordAsync(id, ct);
        if (stored is null || await ProjectAsync(stored.Record, ct) is not { } existing)
            return null;

        TreatmentClientId.KeepStored(treatment, existing);

        treatment.Id = await UpsertKeyAsync(stored, ct);
        try
        {
            await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live, ct);
            return await GetByIdAsync(stored.Record.Id.ToString(), ct);
        }
        catch (OperationCanceledException)
        {
            // Canceled request is control flow, not an update failure: propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update treatment {Id}", id);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var deleted = await _pipeline.DeleteByLegacyIdAsync<Treatment>(id, WriteOrigin.Live, ct);

        // Also check TempBasal (not covered by the pipeline's LegacyId delete)
        var tempBasal = await _tempBasalRepo.GetByLegacyIdAsync(id, ct);
        if (tempBasal == null && Guid.TryParse(id, out var guid))
            tempBasal = await _tempBasalRepo.GetByIdAsync(guid, ct);

        if (tempBasal != null)
        {
            try
            {
                await _tempBasalRepo.DeleteAsync(tempBasal.Id, WriteOrigin.Live, ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete TempBasal record {Id}", tempBasal.Id);
                return false;
            }
        }

        // Projected treatments for V4 rows carry the raw record UUID as their id (the 24-hex
        // ObjectId form only exists on the wire), so the filtered bulk-delete path and direct
        // by-UUID deletes arrive here with a Guid string. Resolve it across the repos, preferring
        // the stored LegacyId so correlated siblings (e.g. a meal bolus's carb) go together.
        if (deleted == 0 && Guid.TryParse(id, out var recordId)
            && await DeleteByRecordIdAsync(recordId, ct))
            return true;

        // A 24-hex ObjectId AAPS derived from the record's UUID: resolve it via the uuid prefix
        // range. Prefer deleting by the resolved record's LegacyId so correlated siblings (e.g. a
        // meal bolus's carb, a bolus wizard's calculation) are removed together — DeleteByGuidRange
        // only deletes the single matched row, which would orphan the sibling into a phantom.
        if (deleted == 0 && MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            var legacyId = await FindLegacyIdByGuidRangeAsync(low, high, ct);
            if (!string.IsNullOrEmpty(legacyId))
            {
                deleted = await _pipeline.DeleteByLegacyIdAsync<Treatment>(legacyId, WriteOrigin.Live, ct);
                if (deleted > 0)
                    return true;
            }

            // Native row with no LegacyId (no correlated sibling to worry about): delete by UUID.
            if (await DeleteByGuidRangeAsync(low, high, ct))
                return true;
        }

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(string? find = null, CancellationToken ct = default)
    {
        var findQuery = FindQuery.Parse(find);

        if (findQuery.HasFieldFilters)
        {
            // Field filters only exist on the projected shape; count matches within the
            // (bounded) window instead of delegating to per-repo counts.
            var projected = (await _projection.GetProjectedTreatmentsAsync(
                findQuery.FromMills, findQuery.ToMills, MaxFilterFetch, nativeOnly: false, ct: ct)).ToList();
            if (projected.Count >= MaxFilterFetch)
                _logger.LogWarning(
                    "Find-filtered treatment count hit the {MaxFetch}-row window; older matches are not counted",
                    MaxFilterFetch);
            return projected.Count(findQuery.Matches);
        }

        var (fromMills, toMills) = (findQuery.FromMills, findQuery.ToMills);
        var from = fromMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime : (DateTime?)null;
        var to = toMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime : (DateTime?)null;

        var bolusCount = await _bolusRepo.CountAsync(from, to, ct);
        var carbCount = await _carbIntakeRepo.CountAsync(from, to, ct);
        var bgCheckCount = await _bgCheckRepo.CountAsync(from, to, ct);
        var noteCount = await _noteRepo.CountAsync(from, to, ct);
        var deviceEventCount = await _deviceEventRepo.CountAsync(from, to, ct);
        var tempBasalCount = await _tempBasalRepo.CountAsync(from, to, ct);
        var bolusCalcCount = await _bolusCalcRepo.CountAsync(from, to, ct);

        return bolusCount + carbCount + bgCheckCount + noteCount
             + deviceEventCount + tempBasalCount + bolusCalcCount;
    }

    #region Private - stored record resolution

    /// <summary>
    /// Resolves a client-sent treatment id to the stored record it names. Each key is tried against
    /// every table before the next, looser one, so an exact match always wins over a prefix or hash
    /// match. The records of one treatment share its legacy id, so the tables are tried in the order
    /// a treatment is named by: a legacy id resolves to the record that heads the treatment, not to
    /// the note any treatment with notes also writes.
    /// </summary>
    private async Task<StoredRecord?> FindStoredRecordAsync(string id, CancellationToken ct)
    {
        foreach (var key in RecordKeysFor(id))
        {
            var stored = await FindAsync(_tempBasalRepo, key, ct)
                ?? await FindAsync(_bolusRepo, key, ct)
                ?? await FindAsync(_carbIntakeRepo, key, ct)
                ?? await FindAsync(_bgCheckRepo, key, ct)
                ?? await FindAsync(_deviceEventRepo, key, ct)
                ?? await FindAsync(_bolusCalcRepo, key, ct)
                ?? await FindAsync(_noteRepo, key, ct);
            if (stored is not null)
                return stored;
        }

        return null;
    }

    /// <summary>
    /// A UUID is a record's own id or a client's legacy id. A 24-hex id is a client's legacy id, the
    /// prefix of a record's own id (the id reads serve), or the coerced legacy id an older create
    /// echoed.
    /// </summary>
    private static IEnumerable<RecordKey> RecordKeysFor(string id)
    {
        if (Guid.TryParse(id, out var guid))
        {
            yield return new RecordKey.RecordId(guid);
            yield return new RecordKey.LegacyId(id);
            yield break;
        }

        yield return new RecordKey.LegacyId(id);

        if (MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            yield return new RecordKey.RecordIdPrefix(low, high);
            yield return new RecordKey.LegacyIdUuidPrefix(id);
            yield return new RecordKey.LegacyIdHash(id);
        }
    }

    private static async Task<StoredRecord?> FindAsync<T>(
        ILegacyKeyedRepository<T> repo, RecordKey key, CancellationToken ct) where T : class, IV4Record
    {
        var record = key switch
        {
            RecordKey.RecordId k => await repo.GetByIdAsync(k.Id, ct),
            RecordKey.LegacyId k => await repo.GetByLegacyIdAsync(k.Id, ct),
            RecordKey.RecordIdPrefix k => await repo.GetByGuidRangeAsync(k.Low, k.High, ct),
            RecordKey.LegacyIdUuidPrefix k => await repo.GetByLegacyIdUuidPrefixAsync(k.ObjectId, ct),
            RecordKey.LegacyIdHash k => await repo.GetByLegacyIdHashAsync(k.ObjectId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        return record is null
            ? null
            : new StoredRecord(
                record,
                c => repo.DeleteAsync(record.Id, WriteOrigin.Live, c),
                c => repo.UpdateAsync(record.Id, record, WriteOrigin.Live, c));
    }

    private async Task<Treatment?> ProjectAsync(IV4Record record, CancellationToken ct)
    {
        if (record is Core.Models.V4.TempBasal tempBasal)
            return TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // A carb intake paired into a Meal Bolus is served under the bolus's id.
        if (record is CarbIntake { CorrelationId: { } correlationId }
            && (await _bolusRepo.GetByCorrelationIdAsync(correlationId, ct)).FirstOrDefault() is { } pairedBolus)
            return await FindProjectedTreatmentAsync(pairedBolus.Mills, pairedBolus.Id.ToString(), ct);

        return await FindProjectedTreatmentAsync(record.Mills, record.Id.ToString(), ct);
    }

    /// <summary>
    /// The LegacyId the decomposer upserts a stored record on. A native V4 row has none, so the
    /// decomposer could not match it and would insert a duplicate; it is given the id the wire
    /// already shows for it.
    /// </summary>
    private static async Task<string> UpsertKeyAsync(StoredRecord stored, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(stored.Record.LegacyId))
            return stored.Record.LegacyId;

        stored.Record.LegacyId = MongoObjectId.FromGuid(stored.Record.Id);
        await stored.SaveAsync(ct);
        return stored.Record.LegacyId;
    }

    private sealed record StoredRecord(
        IV4Record Record,
        Func<CancellationToken, Task> DeleteAsync,
        Func<CancellationToken, Task> SaveAsync);

    private abstract record RecordKey
    {
        public sealed record RecordId(Guid Id) : RecordKey;

        public sealed record LegacyId(string Id) : RecordKey;

        public sealed record RecordIdPrefix(Guid Low, Guid High) : RecordKey;

        public sealed record LegacyIdUuidPrefix(string ObjectId) : RecordKey;

        public sealed record LegacyIdHash(string ObjectId) : RecordKey;
    }

    /// <summary>
    /// Maps a wire id (a 24-hex ObjectId derived from a record's UUID) to the <c>LegacyId</c> the
    /// decomposer upserts on, so an update re-decomposes the existing record in place instead of
    /// creating a duplicate. Returns null for a raw UUID or an id that is already the stored key
    /// (the caller falls back to the existing id in that case).
    /// </summary>
    public async Task<string?> ResolveCanonicalIdAsync(string id, CancellationToken ct = default)
    {
        if (Guid.TryParse(id, out _))
            return null;

        if (!MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
            return null;

        // Return the decomposer's upsert key (LegacyId). A native V4 row has no LegacyId, so the
        // decomposer can't match it and would insert a duplicate; backfill it with the derived
        // ObjectId (which is what the wire already shows for this record) so the update lands in
        // place. Short-circuits on the first repo that owns the record.
        return await ResolveOrBackfillLegacyIdAsync(_bolusRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_carbIntakeRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_bgCheckRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_noteRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_deviceEventRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_bolusCalcRepo, low, high, ct)
            ?? await ResolveOrBackfillLegacyIdAsync(_tempBasalRepo, low, high, ct);
    }

    private static async Task<string?> ResolveOrBackfillLegacyIdAsync<T>(
        IV4Repository<T> repo, Guid low, Guid high, CancellationToken ct) where T : class, IV4Record
    {
        var entity = await repo.GetByGuidRangeAsync(low, high, ct);
        if (entity is null) return null;
        if (!string.IsNullOrEmpty(entity.LegacyId)) return entity.LegacyId;

        var objectId = MongoObjectId.FromGuid(entity.Id);
        entity.LegacyId = objectId;
        await repo.UpdateAsync(entity.Id, entity, WriteOrigin.Live, ct);
        return objectId;
    }

    private async Task<string?> FindLegacyIdByGuidRangeAsync(Guid low, Guid high, CancellationToken ct)
    {
        var bolus = await _bolusRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolus != null) return bolus.LegacyId;

        var carbIntake = await _carbIntakeRepo.GetByGuidRangeAsync(low, high, ct);
        if (carbIntake != null) return carbIntake.LegacyId;

        var bgCheck = await _bgCheckRepo.GetByGuidRangeAsync(low, high, ct);
        if (bgCheck != null) return bgCheck.LegacyId;

        var note = await _noteRepo.GetByGuidRangeAsync(low, high, ct);
        if (note != null) return note.LegacyId;

        var deviceEvent = await _deviceEventRepo.GetByGuidRangeAsync(low, high, ct);
        if (deviceEvent != null) return deviceEvent.LegacyId;

        var bolusCalc = await _bolusCalcRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolusCalc != null) return bolusCalc.LegacyId;

        var tempBasal = await _tempBasalRepo.GetByGuidRangeAsync(low, high, ct);
        if (tempBasal != null) return tempBasal.LegacyId;

        return null;
    }

    /// <summary>
    /// Deletes the record with the given UUID from whichever repo owns it, via LegacyId when one
    /// is stored (so correlated siblings are removed together). TempBasal is handled by the
    /// caller before this runs.
    /// </summary>
    private async Task<bool> DeleteByRecordIdAsync(Guid id, CancellationToken ct)
    {
        return await TryDeleteFromRepoAsync(_bolusRepo, id, ct)
            || await TryDeleteFromRepoAsync(_carbIntakeRepo, id, ct)
            || await TryDeleteFromRepoAsync(_bgCheckRepo, id, ct)
            || await TryDeleteFromRepoAsync(_noteRepo, id, ct)
            || await TryDeleteFromRepoAsync(_deviceEventRepo, id, ct)
            || await TryDeleteFromRepoAsync(_bolusCalcRepo, id, ct);
    }

    private async Task<bool> TryDeleteFromRepoAsync<T>(
        IV4Repository<T> repo, Guid id, CancellationToken ct) where T : class, IV4Record
    {
        var record = await repo.GetByIdAsync(id, ct);
        if (record is null)
            return false;

        if (!string.IsNullOrEmpty(record.LegacyId))
            return await _pipeline.DeleteByLegacyIdAsync<Treatment>(record.LegacyId, WriteOrigin.Live, ct) > 0;

        // Native row with no LegacyId (no correlated sibling to worry about): delete directly.
        await repo.DeleteAsync(id, WriteOrigin.Live, ct);
        return true;
    }

    /// <summary>Deletes the record inside a UUID prefix range (derived ObjectId) by its real UUID.</summary>
    private async Task<bool> DeleteByGuidRangeAsync(Guid low, Guid high, CancellationToken ct)
    {
        var bolus = await _bolusRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolus != null) { await _bolusRepo.DeleteAsync(bolus.Id, WriteOrigin.Live, ct); return true; }

        var carbIntake = await _carbIntakeRepo.GetByGuidRangeAsync(low, high, ct);
        if (carbIntake != null) { await _carbIntakeRepo.DeleteAsync(carbIntake.Id, WriteOrigin.Live, ct); return true; }

        var bgCheck = await _bgCheckRepo.GetByGuidRangeAsync(low, high, ct);
        if (bgCheck != null) { await _bgCheckRepo.DeleteAsync(bgCheck.Id, WriteOrigin.Live, ct); return true; }

        var note = await _noteRepo.GetByGuidRangeAsync(low, high, ct);
        if (note != null) { await _noteRepo.DeleteAsync(note.Id, WriteOrigin.Live, ct); return true; }

        var deviceEvent = await _deviceEventRepo.GetByGuidRangeAsync(low, high, ct);
        if (deviceEvent != null) { await _deviceEventRepo.DeleteAsync(deviceEvent.Id, WriteOrigin.Live, ct); return true; }

        var bolusCalc = await _bolusCalcRepo.GetByGuidRangeAsync(low, high, ct);
        if (bolusCalc != null) { await _bolusCalcRepo.DeleteAsync(bolusCalc.Id, WriteOrigin.Live, ct); return true; }

        var tempBasal = await _tempBasalRepo.GetByGuidRangeAsync(low, high, ct);
        if (tempBasal != null) { await _tempBasalRepo.DeleteAsync(tempBasal.Id, WriteOrigin.Live, ct); return true; }

        return false;
    }

    private async Task<Treatment?> FindProjectedTreatmentAsync(
        long mills, string treatmentId, CancellationToken ct)
    {
        var projected = await _projection.GetProjectedTreatmentsAsync(
            mills, mills, 100, nativeOnly: false, ct: ct);
        return projected.FirstOrDefault(t => t.Id == treatmentId);
    }

    #endregion

}
