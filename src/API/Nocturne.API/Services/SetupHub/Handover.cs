using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.SetupHub;

/// <summary>
/// A tenant a helper set up for someone else. Only <see cref="TenantEntity.HandedOverAt"/> is
/// stored; who the helper is and whether the recipient joined are read from the members.
/// </summary>
public static class Handover
{
    /// <summary>
    /// The onboarder, which is the tenant's first person member. Not filtered by role: a helper
    /// who stepped down is still the helper.
    /// </summary>
    /// <remarks>
    /// Read from the current members, not stored: once the helper's membership is removed or their
    /// subject deactivated, the next earliest person member reads as the helper.
    /// </remarks>
    public static Task<TenantMemberEntity?> HelperAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct, bool withSubjectAndRoles = false)
    {
        var people = db.TenantMembers.PeopleOf(tenantId);
        if (withSubjectAndRoles)
            people = people
                .Include(m => m.Subject)
                .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole);
        return people.InJoinOrder().FirstOrDefaultAsync(ct);
    }

    public static Task<bool> RecipientJoinedAsync(
        NocturneDbContext db, Guid tenantId, TenantMemberEntity? helper, CancellationToken ct) =>
        helper is null
            ? Task.FromResult(false)
            : db.TenantMembers.OwnersOf(tenantId).AnyAsync(m => m.Id != helper.Id, ct);

    public static async Task<SetupHubHandover?> DescribeAsync(
        NocturneDbContext db,
        Guid tenantId,
        PatientRelationship? relationship,
        DateTime? handedOverAt,
        Guid? callerId,
        CancellationToken ct)
    {
        if (relationship != PatientRelationship.Helper && handedOverAt is null)
            return null;

        if (await HelperAsync(db, tenantId, ct, withSubjectAndRoles: true) is not { } helper)
            return null;

        var callerIsHelper = callerId == helper.SubjectId;
        var recipientJoined = await RecipientJoinedAsync(db, tenantId, helper, ct);
        var helperOwns = helper.MemberRoles.Any(mr => mr.TenantRole?.Slug == RoleSeeds.Owner);

        return new SetupHubHandover(
            helper.Subject!.Name,
            callerIsHelper,
            recipientJoined,
            relationship != PatientRelationship.Helper,
            callerIsHelper && helperOwns && recipientJoined);
    }
}
