using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Core.Contracts.Jobs;

/// <summary>
/// <c>job_ledger</c>: one row per job and tenant (or per job, for a platform job) holding when it
/// is next due, who holds its lease, how its last runs went and where it resumes.
/// </summary>
/// <remarks>
/// <para>
/// A platform table read and written across tenants: it has no tenant policy, and a row holds
/// scheduling state and a <see cref="JobCursor"/>, no health data.
/// </para>
/// <para>
/// A row is due when <c>next_due_at</c> has passed and no unexpired lease is on it; a null
/// <c>next_due_at</c> is never due again. Claiming a row takes its lease, so replicas share one
/// ledger without running a row twice, and a row whose holder died is claimable once the lease
/// expires. Each claim of a row stores a token of its own in <c>lease_holder</c>
/// (<see cref="JobLease.Holder"/>), and every write after the claim matches on it, so it changes
/// nothing once any other claim, the same process's included, has the row.
/// </para>
/// </remarks>
public interface IJobLedger
{
    /// <summary>
    /// Leases up to <paramref name="limit"/> due rows of <paramref name="jobs"/> until
    /// <paramref name="leaseUntil"/>, earliest due first, skipping rows another claim has locked. Each
    /// lease's token is <paramref name="holder"/> and a suffix unique to the claim.
    /// </summary>
    Task<IReadOnlyList<JobLease>> ClaimDueAsync(
        string holder, IReadOnlyCollection<string> jobs, int limit, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct);

    /// <summary>Extends <paramref name="lease"/> while it is still held; false when it is not.</summary>
    Task<bool> ExtendLeaseAsync(JobLease lease, DateTimeOffset leaseUntil, CancellationToken ct);

    /// <summary>
    /// Records a successful run of <paramref name="lease"/>: the cursor and next due time (both null
    /// for a finished job), with the failure count cleared and the lease released. A request made while
    /// the run held the row is kept when it is earlier than <paramref name="nextDueAt"/>.
    /// </summary>
    Task CompleteAsync(
        JobLease lease, DateTimeOffset completedAt, JobCursor? cursor, DateTimeOffset? nextDueAt, CancellationToken ct);

    /// <summary>
    /// Records a failed run: one more attempt, <paramref name="error"/>, due again at
    /// <paramref name="retryAt"/> (or sooner, for a request made during the run), cursor kept, lease
    /// released.
    /// </summary>
    Task FailAsync(JobLease lease, string error, DateTimeOffset retryAt, CancellationToken ct);

    /// <summary>Releases the lease and changes nothing else, so the row is claimable at once.</summary>
    Task ReleaseAsync(JobLease lease, CancellationToken ct);

    /// <summary>Adds a row due at <paramref name="dueAt"/> for every tenant that has none for <paramref name="job"/>.</summary>
    Task EnrolPerTenantAsync(string job, DateTimeOffset dueAt, CancellationToken ct);

    /// <summary>Adds the platform row of <paramref name="job"/>, due at <paramref name="dueAt"/>, if it has none.</summary>
    Task EnrolPlatformAsync(string job, DateTimeOffset dueAt, CancellationToken ct);

    /// <summary>
    /// Makes the row of <paramref name="job"/> for <paramref name="tenantId"/> (null for the platform
    /// row) due now, adding it if absent, a finished row included. A request made during a run is
    /// honoured when the run completes or fails.
    /// </summary>
    Task RequestAsync(string job, Guid? tenantId, CancellationToken ct);

    /// <summary>Every row, for the platform admin view.</summary>
    Task<IReadOnlyList<JobLedgerEntry>> ListAsync(CancellationToken ct);

    /// <summary>How many rows' last run failed.</summary>
    Task<int> CountFailingAsync(CancellationToken ct);
}

/// <summary>A row <see cref="IJobLedger.ClaimDueAsync"/> leased.</summary>
/// <param name="Tenant">The row's tenant, or null for a platform row.</param>
/// <param name="DueAt">The row's due time when it was claimed.</param>
/// <param name="Holder">The claim's token in <c>lease_holder</c>.</param>
public sealed record JobLease(
    Guid Id, string Job, TenantContext? Tenant, JobCursor? Cursor, int Attempts, DateTimeOffset DueAt, string Holder);

/// <summary>A <c>job_ledger</c> row, without its cursor.</summary>
public sealed record JobLedgerEntry(
    Guid Id,
    string Job,
    Guid? TenantId,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? NextDueAt,
    string? LeaseHolder,
    DateTimeOffset? LeaseUntil,
    int Attempts,
    string? LastError);
