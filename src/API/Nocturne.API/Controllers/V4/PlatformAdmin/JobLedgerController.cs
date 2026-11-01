using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Jobs;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>Platform-admin view of the job ledger: every scheduled job's row, failing ones included.</summary>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/jobs")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class JobLedgerController(IJobLedger ledger) : ControllerBase
{
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<JobLedgerEntry>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<JobLedgerEntry>>> List(CancellationToken ct) =>
        Ok(await ledger.ListAsync(ct));
}
