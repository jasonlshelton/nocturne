using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Nightscout's PUT /api/v1/treatments is an upsert keyed on the body's <c>_id</c>: Loop edits a
/// carb entry or replaces an override that way, and an unknown <c>_id</c> inserts.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentsSaveTests
{
    private const string LoopId = "69F15FD2-8075-4DEB-AEA3-4352F455840D";

    private readonly Mock<ITreatmentService> _service = new();
    private readonly TreatmentsController _controller;

    public TreatmentsSaveTests()
    {
        _controller = new TreatmentsController(
            _service.Object,
            new DocumentProcessingService(NullLogger<DocumentProcessingService>.Instance),
            TimeProvider.System,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> t, CancellationToken _) =>
                new BulkWrite<Treatment>(t.ToList(), 0));
    }

    [Fact]
    public async Task Put_WithAStoredId_UpdatesThatRecord()
    {
        var stored = new Treatment { Id = LoopId, EventType = "Temporary Override", Duration = 30 };
        _service
            .Setup(s => s.UpdateTreatmentAsync(LoopId, It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = await _controller.SaveTreatments(Body(Override(duration: 30)));

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(stored);
        _service.Verify(s => s.UpdateTreatmentAsync(
            LoopId, It.Is<Treatment>(t => t.Duration == 30), It.IsAny<CancellationToken>()), Times.Once);
        VerifyNothingCreated();
    }

    [Fact]
    public async Task Put_WithAnUnknownId_InsertsUnderTheClientsId()
    {
        UnknownIds();

        var result = await _controller.SaveTreatments(Body(Override(duration: 30)));

        var saved = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<Treatment>().Subject;
        saved.Id.Should().Be(LoopId);
        saved.Duration.Should().Be(30);
        _service.Verify(s => s.CreateTreatmentsAsync(
            It.Is<IEnumerable<Treatment>>(t => t.Single().Id == LoopId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Put_WithAnArray_SavesEachAndAnswersAnArray()
    {
        const string storedId = "507f1f77bcf86cd799439011";
        var stored = new Treatment { Id = storedId, EventType = "Carb Correction", Carbs = 20 };
        UnknownIds();
        _service
            .Setup(s => s.UpdateTreatmentAsync(storedId, It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = await _controller.SaveTreatments(Body(new object[]
        {
            new { _id = storedId, eventType = "Carb Correction", carbs = 20, created_at = "2026-09-30T01:00:00.000Z" },
            Override(duration: 30),
        }));

        var saved = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<Treatment[]>().Subject;
        saved.Select(t => t.Id).Should().Equal(storedId, LoopId);
    }

    [Fact]
    public async Task Put_WithoutAnId_Creates()
    {
        var result = await _controller.SaveTreatments(
            Body(new { eventType = "Note", notes = "hello", created_at = "2026-09-30T01:00:00.000Z" }));

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<Treatment>();
        _service.Verify(s => s.UpdateTreatmentAsync(
            It.IsAny<string>(), It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(s => s.CreateTreatmentsAsync(
            It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PutById_WithAnUnknownId_InsertsUnderThatId()
    {
        UnknownIds();

        var result = await _controller.UpdateTreatment(
            LoopId, new Treatment { EventType = "Temporary Override", Duration = 30 });

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<Treatment>().Which.Id.Should().Be(LoopId);
    }

    private void UnknownIds() => _service
        .Setup(s => s.UpdateTreatmentAsync(It.IsAny<string>(), It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((Treatment?)null);

    private void VerifyNothingCreated() => _service.Verify(s => s.CreateTreatmentsAsync(
        It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()), Times.Never);

    private static object Override(double duration) => new
    {
        _id = LoopId,
        eventType = "Temporary Override",
        created_at = "2026-09-30T01:45:00.000Z",
        duration,
    };

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);
}
