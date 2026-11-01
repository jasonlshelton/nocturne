using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using NpgsqlTypes;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IJobLedger"/> over <c>job_ledger</c>, one platform context per call, each write one
/// statement.
/// </summary>
/// <remarks>Registered transient, like the factory it holds.</remarks>
public class JobLedgerRepository : IJobLedger
{
    /// <summary>
    /// A UUIDv7 built in SQL, for rows inserted from a <c>SELECT</c>: PostgreSQL 17 has no
    /// <c>uuidv7()</c>.
    /// </summary>
    private const string UuidV7 = """
        encode(set_bit(set_bit(
            overlay(uuid_send(gen_random_uuid())
                    placing substring(int8send((extract(epoch from clock_timestamp()) * 1000)::bigint) from 3)
                    from 1 for 6),
            52, 1), 53, 1), 'hex')::uuid
        """;

    private const string ClaimSql = """
        WITH claimed AS (
            UPDATE job_ledger
            SET lease_holder = @holder || ':' || gen_random_uuid(), lease_until = @until, last_started_at = @now
            WHERE id IN (
                SELECT id FROM job_ledger
                WHERE next_due_at <= @now AND (lease_until IS NULL OR lease_until < @now) AND job = ANY(@jobs)
                ORDER BY next_due_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED)
            RETURNING id, job, tenant_id, cursor, attempts, next_due_at, lease_holder)
        SELECT c.id, c.job, c.tenant_id, c.cursor::text, c.attempts, c.next_due_at, c.lease_holder,
               t.slug, t.display_name, t.is_active, t.is_demo
        FROM claimed c LEFT JOIN tenants t ON t.id = c.tenant_id
        """;

    /// <remarks>
    /// A row whose <c>next_due_at</c> is not the one it was claimed at was requested while the run
    /// held it (<see cref="RequestSql"/>), and keeps that time unless the outcome is due sooner.
    /// </remarks>
    private const string CompleteSql = """
        UPDATE job_ledger
        SET last_completed_at = @completed,
            next_due_at = CASE
                WHEN next_due_at IS DISTINCT FROM @claimed_due AND (@next IS NULL OR next_due_at < @next) THEN next_due_at
                ELSE @next END,
            cursor = @cursor::jsonb,
            attempts = 0, last_error = NULL, lease_holder = NULL, lease_until = NULL
        WHERE id = @id AND lease_holder = @holder
        """;

    /// <remarks>A request made during the run is not held back by the backoff; see <see cref="CompleteSql"/>.</remarks>
    private const string FailSql = """
        UPDATE job_ledger
        SET attempts = attempts + 1, last_error = @error,
            next_due_at = CASE
                WHEN next_due_at IS DISTINCT FROM @claimed_due THEN LEAST(next_due_at, @retry)
                ELSE @retry END,
            lease_holder = NULL, lease_until = NULL
        WHERE id = @id AND lease_holder = @holder
        """;

    private const string ExtendSql = """
        UPDATE job_ledger SET lease_until = @until WHERE id = @id AND lease_holder = @holder
        """;

    private const string ReleaseSql = """
        UPDATE job_ledger SET lease_holder = NULL, lease_until = NULL WHERE id = @id AND lease_holder = @holder
        """;

    private static readonly string EnrolPerTenantSql = $"""
        INSERT INTO job_ledger (id, job, tenant_id, next_due_at, attempts)
        SELECT {UuidV7}, @job, t.id, @due, 0
        FROM tenants t
        WHERE NOT EXISTS (SELECT 1 FROM job_ledger j WHERE j.job = @job AND j.tenant_id = t.id)
        ON CONFLICT DO NOTHING
        """;

    private const string EnrolPlatformSql = """
        INSERT INTO job_ledger (id, job, tenant_id, next_due_at, attempts)
        VALUES (@id, @job, NULL, @due, 0)
        ON CONFLICT DO NOTHING
        """;

    /// <remarks>
    /// A row whose lease still runs takes the request's time as it is, which marks it for
    /// <see cref="CompleteSql"/> and <see cref="FailSql"/>; any other row, one whose holder died
    /// included, keeps the earlier of the two.
    /// </remarks>
    private const string RequestSql = """
        INSERT INTO job_ledger (id, job, tenant_id, next_due_at, attempts)
        VALUES (@id, @job, @tenant, @due, 0)
        ON CONFLICT (job, tenant_id) DO UPDATE
        SET next_due_at = CASE
            WHEN job_ledger.lease_until > EXCLUDED.next_due_at THEN EXCLUDED.next_due_at
            ELSE LEAST(COALESCE(job_ledger.next_due_at, 'infinity'), EXCLUDED.next_due_at) END
        """;

    private static readonly JsonSerializerOptions CursorJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly INocturneDbContextFactory _contexts;
    private readonly TimeProvider _time;

    public JobLedgerRepository(INocturneDbContextFactory contexts, TimeProvider time)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobLease>> ClaimDueAsync(
        string holder, IReadOnlyCollection<string> jobs, int limit, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct)
    {
        if (limit <= 0 || jobs.Count == 0)
            return [];

        await using var db = await _contexts.CreatePlatformAsync(ct);
        await using var command = await CommandAsync(db, ClaimSql, ct);
        command.Parameters.Add(Text("holder", holder));
        command.Parameters.Add(Timestamp("until", leaseUntil));
        command.Parameters.Add(Timestamp("now", now));
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });
        command.Parameters.Add(new NpgsqlParameter("jobs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = jobs.ToArray() });

        var leases = new List<JobLease>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tenant = reader.IsDBNull(2)
                ? null
                : new TenantContext(
                    reader.GetGuid(2), reader.GetString(7), reader.GetString(8), reader.GetBoolean(9), reader.GetBoolean(10));
            leases.Add(new JobLease(
                reader.GetGuid(0),
                reader.GetString(1),
                tenant,
                reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<JobCursor>(reader.GetString(3), CursorJson),
                reader.GetInt32(4),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
                reader.GetString(6)));
        }

        return leases;
    }

    /// <inheritdoc/>
    public async Task<bool> ExtendLeaseAsync(JobLease lease, DateTimeOffset leaseUntil, CancellationToken ct) =>
        await ExecuteAsync(ExtendSql, ct, Id(lease.Id), Text("holder", lease.Holder), Timestamp("until", leaseUntil)) > 0;

    /// <inheritdoc/>
    public Task CompleteAsync(
        JobLease lease, DateTimeOffset completedAt, JobCursor? cursor, DateTimeOffset? nextDueAt, CancellationToken ct) =>
        ExecuteAsync(
            CompleteSql,
            ct,
            Id(lease.Id),
            Text("holder", lease.Holder),
            Timestamp("claimed_due", lease.DueAt),
            Timestamp("completed", completedAt),
            Timestamp("next", nextDueAt),
            Text("cursor", cursor is null ? null : JsonSerializer.Serialize(cursor, CursorJson)));

    /// <inheritdoc/>
    public Task FailAsync(JobLease lease, string error, DateTimeOffset retryAt, CancellationToken ct) =>
        ExecuteAsync(
            FailSql,
            ct,
            Id(lease.Id),
            Text("holder", lease.Holder),
            Timestamp("claimed_due", lease.DueAt),
            Text("error", error),
            Timestamp("retry", retryAt));

    /// <inheritdoc/>
    public Task ReleaseAsync(JobLease lease, CancellationToken ct) =>
        ExecuteAsync(ReleaseSql, ct, Id(lease.Id), Text("holder", lease.Holder));

    /// <inheritdoc/>
    public Task EnrolPerTenantAsync(string job, DateTimeOffset dueAt, CancellationToken ct) =>
        ExecuteAsync(EnrolPerTenantSql, ct, Text("job", job), Timestamp("due", dueAt));

    /// <inheritdoc/>
    public Task EnrolPlatformAsync(string job, DateTimeOffset dueAt, CancellationToken ct) =>
        ExecuteAsync(EnrolPlatformSql, ct, Id(Guid.CreateVersion7()), Text("job", job), Timestamp("due", dueAt));

    /// <inheritdoc/>
    public Task RequestAsync(string job, Guid? tenantId, CancellationToken ct) =>
        ExecuteAsync(
            RequestSql,
            ct,
            Id(Guid.CreateVersion7()),
            Text("job", job),
            new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)tenantId ?? DBNull.Value },
            Timestamp("due", _time.GetUtcNow()));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobLedgerEntry>> ListAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreatePlatformAsync(ct);
        var rows = await db.JobLedger.AsNoTracking()
            .OrderBy(j => j.Job).ThenBy(j => j.TenantId)
            .ToListAsync(ct);
        return rows.Select(j => new JobLedgerEntry(
                j.Id,
                j.Job,
                j.TenantId,
                Utc(j.LastStartedAt),
                Utc(j.LastCompletedAt),
                Utc(j.NextDueAt),
                j.LeaseHolder,
                Utc(j.LeaseUntil),
                j.Attempts,
                j.LastError))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<int> CountFailingAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreatePlatformAsync(ct);
        return await db.JobLedger.CountAsync(j => j.Attempts > 0, ct);
    }

    private static DateTimeOffset? Utc(DateTime? at) =>
        at is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var db = await _contexts.CreatePlatformAsync(ct);
        await using var command = await CommandAsync(db, sql, ct);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <remarks>
    /// Plain ADO.NET on the context's connection, opened through EF so its interceptors run: the
    /// claim's data-modifying CTE cannot sit in the subquery EF wraps raw SQL in.
    /// </remarks>
    private static async Task<NpgsqlCommand> CommandAsync(NocturneDbContext db, string sql, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        return new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
    }

    private static NpgsqlParameter Id(Guid id) => new("id", NpgsqlDbType.Uuid) { Value = id };

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset? at) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = (object?)at?.UtcDateTime ?? DBNull.Value };
}
