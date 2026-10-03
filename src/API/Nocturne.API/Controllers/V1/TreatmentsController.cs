using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Helpers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Treatments controller that provides 1:1 compatibility with Nightscout treatments endpoints.
/// Implements the /api/v1/treatments/* endpoints from the legacy JavaScript implementation.
/// </summary>
/// <seealso cref="ITreatmentService"/>
/// <seealso cref="IDocumentProcessingService"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class TreatmentsController : ControllerBase
{
    private readonly ITreatmentService _treatmentService;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TreatmentsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TreatmentsController"/>.
    /// </summary>
    /// <param name="treatmentService">Service handling treatment CRUD operations.</param>
    /// <param name="treatmentProcessingService">Service for async document ingestion and processing.</param>
    /// <param name="timeProvider">Clock for the legacy default find window.</param>
    /// <param name="logger">Logger instance.</param>
    public TreatmentsController(
        ITreatmentService treatmentService,
        IDocumentProcessingService treatmentProcessingService,
        TimeProvider timeProvider,
        ILogger<TreatmentsController> logger
    )
    {
        _treatmentService = treatmentService;
        _documentProcessingService = treatmentProcessingService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Get treatments with optional filtering and pagination
    /// </summary>
    /// <param name="find">MongoDB-style query filter for date range filtering</param>
    /// <param name="count">Maximum number of treatments to return (default: 10)</param>
    /// <param name="skip">Number of treatments to skip for pagination (default: 0)</param>
    /// <param name="format">Output format (json, csv, tsv, txt)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of treatments ordered by most recent first</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.TreatmentsRead)]
    public async Task<ActionResult> GetTreatments(
        [FromQuery] string? find = null,
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        var findQuery = LegacyFindQueryString.Resolve(HttpContext?.Request, find);

        _logger.LogDebug(
            "Treatments endpoint requested with count: {Count}, skip: {Skip}, findQuery: {FindQuery} from {RemoteIpAddress}",
            count,
            skip,
            findQuery,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Handle count parameter for Nightscout compatibility:
            // - 0 or negative: return empty array (Nightscout behavior)
            if (count <= 0)
            {
                _logger.LogDebug("Returning empty array for count={Count}", count);
                return Ok(Array.Empty<Treatment>());
            }

            // Validate skip parameter (negative is not valid)
            if (skip < 0)
            {
                skip = 0; // Normalize to 0 for Nightscout compatibility
            }

            var treatments = await _treatmentService.GetTreatmentsAsync(
                find: LegacyTreatmentDateWindow.Apply(findQuery, _timeProvider.GetUtcNow()),
                count: LegacyReadLimits.ClampCount(count),
                skip: skip,
                cancellationToken: cancellationToken
            );
            var treatmentArray = treatments.ToArray();

            _logger.LogDebug("Successfully retrieved {Count} treatments", treatmentArray.Length);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(treatmentArray);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving treatments");
            return StatusCode(500, "Internal server error while retrieving treatments");
        }
    }

    /// <summary>
    /// Get a specific treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The treatment with the specified ID</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.TreatmentsRead)]
    public async Task<ActionResult<Treatment>> GetTreatmentById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Treatment by ID endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            var treatment = await _treatmentService.GetTreatmentByIdAsync(id, cancellationToken);

            if (treatment == null)
            {
                _logger.LogDebug("Treatment not found with ID: {Id}", id);
                return NotFound($"Treatment with ID '{id}' not found");
            }

            _logger.LogDebug("Successfully retrieved treatment with ID: {Id}", id);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(treatment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while retrieving treatment {id}");
        }
    }

    /// <summary>
    /// Create new treatments
    /// </summary>
    /// <param name="treatments">Treatments to create (can be single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created treatments with assigned IDs</returns>
    [HttpPost]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment[]), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment[]>> CreateTreatments(
        [FromBody] JsonElement treatments,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Create treatments endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var treatmentsToCreate = ReadTreatments(treatments);
            if (treatmentsToCreate is null)
            {
                _logger.LogWarning("Invalid JSON format for treatments");
                return BadRequest("Invalid JSON format. Expected object or array of treatments.");
            }
            if (treatmentsToCreate.Count == 0)
            {
                _logger.LogWarning("No treatments provided for creation");
                return BadRequest("No treatments provided");
            }

            var createdTreatments = await _treatmentService.CreateTreatmentsAsync(
                PrepareTreatments(treatmentsToCreate),
                cancellationToken
            );
            var resultArray = createdTreatments.ToArray();

            _logger.LogDebug("Successfully created {Count} treatments", resultArray.Length);

            // Return 200 OK to match Nightscout behavior (not 201 Created)
            return Ok(resultArray);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in create treatments request");
            return BadRequest("Invalid JSON format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating treatments");
            return StatusCode(500, "Internal server error while creating treatments");
        }
    }

    /// <summary>
    /// Save treatments identified by the <c>_id</c> in the body, inserting any not already stored
    /// </summary>
    /// <param name="treatments">Treatment to save (can be single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved treatment, or an array of them when an array was sent</returns>
    [HttpPut]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> SaveTreatments(
        [FromBody] JsonElement treatments,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var treatmentsToSave = ReadTreatments(treatments);
            if (treatmentsToSave is null)
                return BadRequest("Invalid JSON format. Expected object or array of treatments.");
            if (treatmentsToSave.Count == 0)
                return BadRequest("No treatments provided");

            return await SaveAsync(
                treatmentsToSave,
                treatments.ValueKind == JsonValueKind.Array,
                cancellationToken
            );
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in save treatments request");
            return BadRequest("Invalid JSON format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving treatments");
            return StatusCode(500, "Internal server error while saving treatments");
        }
    }

    /// <summary>
    /// Save a treatment by ID, inserting it when the ID is not already stored
    /// </summary>
    /// <param name="id">Treatment ID to save</param>
    /// <param name="treatment">Treatment data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Saved treatment</returns>
    [HttpPut("{id}")]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(Treatment), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Treatment>> UpdateTreatment(
        string id,
        [FromBody] Treatment treatment,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Update treatment endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            if (treatment == null)
            {
                _logger.LogWarning("Treatment data is null for update");
                return BadRequest("Treatment data cannot be null");
            }

            treatment.Id = id;
            return await SaveAsync([treatment], asArray: false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while updating treatment {id}");
        }
    }

    /// <summary>
    /// Delete a treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [NightscoutEndpoint("/api/v1/treatments/:id")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteTreatment(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Delete treatment endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid treatment ID: {Id}", id);
                return BadRequest("Treatment ID cannot be null or empty");
            }

            if (id == LegacyDeleteStatus.AnyId)
            {
                return HttpContext?.HasScope(Scope.FullAccess) == true
                    ? await BulkDeleteTreatments(cancellationToken)
                    : Forbid();
            }

            var deleted = await _treatmentService.DeleteTreatmentAsync(id, cancellationToken);

            _logger.LogDebug("Deleted treatment with ID {Id}: {Deleted}", id, deleted);

            return Ok(LegacyDeleteStatus.For(deleted ? 1 : 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting treatment with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while deleting treatment {id}");
        }
    }

    /// <summary>
    /// Bulk delete treatments using query parameters
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of treatments deleted</returns>
    [HttpDelete]
    [Authorize]
    [RequireScope(Scope.FullAccess)]
    [NightscoutEndpoint("/api/v1/treatments")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> BulkDeleteTreatments(
        CancellationToken cancellationToken = default
    )
    {
        // Parse the full query string to reconstruct the find query
        var queryString = HttpContext?.Request?.QueryString.ToString() ?? string.Empty;

        // Strip the leading '?' if present
        if (queryString.StartsWith("?"))
        {
            queryString = queryString.Substring(1);
        }

        _logger.LogDebug(
            "Bulk delete treatments endpoint requested with query: {QueryString} from {RemoteIpAddress}",
            queryString,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Check if there are any find parameters in the query string
            if (string.IsNullOrWhiteSpace(queryString) || !queryString.Contains("find["))
            {
                _logger.LogWarning("Bulk delete requested without find query - this is dangerous");
                return BadRequest(
                    "Find query parameter is required for bulk delete to prevent accidental data loss"
                );
            }

            var deletedCount = await _treatmentService.DeleteTreatmentsAsync(
                LegacyTreatmentDateWindow.Apply(queryString, _timeProvider.GetUtcNow()),
                cancellationToken
            );

            _logger.LogDebug("Successfully deleted {Count} treatments", deletedCount);

            return Ok(LegacyDeleteStatus.For(deletedCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error bulk deleting treatments with query: {QueryString}",
                queryString
            );
            return StatusCode(500, "Internal server error while bulk deleting treatments");
        }
    }

    /// <summary>
    /// Nightscout's save is an upsert: a treatment whose id resolves to a stored record replaces it,
    /// and any other goes through the create path, which keeps the client's id.
    /// </summary>
    private async Task<ActionResult> SaveAsync(
        List<Treatment> treatments,
        bool asArray,
        CancellationToken cancellationToken
    )
    {
        var saved = new List<Treatment>();
        foreach (var treatment in PrepareTreatments(treatments))
        {
            if (!string.IsNullOrWhiteSpace(treatment.Id)
                && await _treatmentService.UpdateTreatmentAsync(
                    treatment.Id, treatment, cancellationToken) is { } updated)
            {
                saved.Add(updated);
                continue;
            }

            saved.AddRange(
                await _treatmentService.CreateTreatmentsAsync([treatment], cancellationToken));
        }

        if (asArray)
            return Ok(saved.ToArray());

        return saved.Count > 0
            ? Ok(saved[0])
            : StatusCode(500, "Internal server error while saving treatment");
    }

    private static List<Treatment>? ReadTreatments(JsonElement body) => body.ValueKind switch
    {
        JsonValueKind.Array => JsonSerializer.Deserialize<List<Treatment>>(body.GetRawText()) ?? [],
        JsonValueKind.Object => JsonSerializer.Deserialize<Treatment>(body.GetRawText()) is { } single
            ? [single]
            : [],
        _ => null,
    };

    private List<Treatment> PrepareTreatments(List<Treatment> treatments)
    {
        var processed = _documentProcessingService.ProcessDocuments(treatments).ToList();

        foreach (var treatment in processed)
        {
            if (string.IsNullOrWhiteSpace(treatment.EventType))
            {
                if (treatment.Insulin.HasValue && treatment.Carbs.HasValue)
                {
                    treatment.EventType = "Meal Bolus";
                }
                else if (treatment.Insulin.HasValue)
                {
                    treatment.EventType = "Correction Bolus";
                }
                else if (treatment.Carbs.HasValue)
                {
                    treatment.EventType = "Carb Correction";
                }
                else
                {
                    treatment.EventType = "Note";
                }
            }
        }

        return processed;
    }
}
