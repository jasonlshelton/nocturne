using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// A treatment create is announced as reads serve it, under the <c>_id</c> REST serves for it, and only
/// when something was written (#1809). Runs the service over the real read service and event sink.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentCreateBroadcastTests
{
    private const string ClientId = "65f0a1b2c3d4e5f601234567";
    private const string Connector = "nightscout-connector";
    private static readonly DateTime At = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITreatmentDecomposer> _decomposer = new();
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly Mock<IBolusRepository> _boluses = new();
    private readonly Mock<ICarbIntakeRepository> _carbs = new();
    private readonly Mock<IProfileActivationRepository> _activations = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly List<StorageDocumentEvent> _creates = [];
    private readonly TreatmentService _service;

    public TreatmentCreateBroadcastTests()
    {
        _broadcast
            .Setup(b => b.BroadcastStorageCreateAsync("treatments", It.IsAny<object>()))
            .Callback<string, object>((_, data) => _creates.Add((StorageDocumentEvent)data))
            .Returns(Task.CompletedTask);

        var store = new TreatmentReadService(
            _projection.Object,
            _decomposer.Object,
            Mock.Of<ITempBasalRepository>(),
            _boluses.Object,
            _carbs.Object,
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            _activations.Object,
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        _service = new TreatmentService(
            store,
            _decomposer.Object,
            Mock.Of<ITreatmentCache>(),
            new SignalRTreatmentEventSink(_broadcast.Object, NullLogger<SignalRTreatmentEventSink>.Instance),
            Mock.Of<IPatientInsulinRepository>(),
            NullLogger<TreatmentService>.Instance);
    }

    /// <summary>
    /// A connector's meal arrives as two treatments, one per half. The carbs half is stored as a carb
    /// intake paired with the bolus already stored, and reads serve the pair once, under the bolus's id.
    /// </summary>
    [Fact]
    public async Task ACreatedCarbHalfIsAnnouncedAsTheMealReadsServe()
    {
        var correlation = Guid.CreateVersion7();
        var bolus = new Bolus { Id = Guid.CreateVersion7(), Timestamp = At, Insulin = 4, CorrelationId = correlation };
        var carbs = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 40, CorrelationId = correlation, LegacyId = ClientId,
        };
        Decomposes(carbs);
        _carbs.Setup(r => r.GetByIdAsync(carbs.Id, It.IsAny<CancellationToken>())).ReturnsAsync(carbs);
        _boluses.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([bolus]);
        _carbs.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([carbs]);
        _boluses.Setup(r => r.GetByIdAsync(bolus.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        Serves(new Treatment
        {
            Id = bolus.Id.ToString(), EventType = "Meal Bolus", Insulin = 4, Carbs = 40, Mills = bolus.Mills,
        });

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Carb Correction", Carbs = 40, Mills = carbs.Mills }]);

        var served = await _service.GetTreatmentByIdAsync(created.Single().Id!);
        var announced = _creates.Should().ContainSingle().Subject;
        Wire(announced.Doc).GetProperty("_id").GetString()
            .Should().Be(Wire(served!).GetProperty("_id").GetString())
            .And.Be(MongoObjectId.FromGuid(bolus.Id));
        Wire(announced.Doc).GetProperty("insulin").GetDouble().Should().Be(4);
    }

    /// <summary>
    /// A client that keeps the create response's id reads the same treatment back by it, so the
    /// response names the meal by its bolus, as the event and every read do.
    /// </summary>
    [Fact]
    public async Task ACreatedCarbHalfIsAnsweredWithTheIdTheMealReadsServe()
    {
        var correlation = Guid.CreateVersion7();
        var bolus = new Bolus { Id = Guid.CreateVersion7(), Timestamp = At, Insulin = 4, CorrelationId = correlation };
        var carbs = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 40, CorrelationId = correlation, LegacyId = ClientId,
        };
        Decomposes(carbs);
        _carbs.Setup(r => r.GetByIdAsync(carbs.Id, It.IsAny<CancellationToken>())).ReturnsAsync(carbs);
        _boluses.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([bolus]);
        _carbs.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([carbs]);
        _boluses.Setup(r => r.GetByIdAsync(bolus.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        Serves(new Treatment
        {
            Id = bolus.Id.ToString(), EventType = "Meal Bolus", Insulin = 4, Carbs = 40, Mills = bolus.Mills,
        });

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Carb Correction", Carbs = 40, Mills = carbs.Mills }]);

        var served = await _service.GetTreatmentByIdAsync(created.Single().Id!);
        var servedByCarbId = await _service.GetTreatmentByIdAsync(carbs.Id.ToString());
        var announced = _creates.Should().ContainSingle().Subject;
        Wire(created.Single()).GetProperty("_id").GetString()
            .Should().Be(MongoObjectId.FromGuid(bolus.Id))
            .And.Be(Wire(served!).GetProperty("_id").GetString())
            .And.Be(Wire(servedByCarbId!).GetProperty("_id").GetString())
            .And.Be(Wire(announced.Doc).GetProperty("_id").GetString());
    }

    /// <summary>
    /// Only the largest carb intake of a correlation is half of its Meal Bolus; a smaller one is served
    /// as a Carb Correction under its own id, so a client that deletes by the response id deletes that
    /// carb and nothing else.
    /// </summary>
    [Fact]
    public async Task ACreatedExtraCarbIsAnsweredWithItsOwnIdAndDeletedAlone()
    {
        var correlation = Guid.CreateVersion7();
        var bolus = new Bolus { Id = Guid.CreateVersion7(), Timestamp = At, Insulin = 4, CorrelationId = correlation };
        var mealCarbs = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 40, CorrelationId = correlation,
        };
        var extra = new CarbIntake
        {
            Id = Guid.CreateVersion7(), Timestamp = At, Carbs = 10, CorrelationId = correlation, LegacyId = ClientId,
        };
        Decomposes(extra);
        _carbs.Setup(r => r.GetByIdAsync(extra.Id, It.IsAny<CancellationToken>())).ReturnsAsync(extra);
        _carbs.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>()))
            .ReturnsAsync([mealCarbs, extra]);
        _boluses.Setup(r => r.GetByCorrelationIdAsync(correlation, It.IsAny<CancellationToken>())).ReturnsAsync([bolus]);
        Serves(new Treatment
        {
            Id = extra.Id.ToString(), EventType = "Carb Correction", Carbs = 10, Mills = extra.Mills,
        });
        _decomposer
            .Setup(d => d.DeleteByLegacyIdAsync(ClientId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Carb Correction", Carbs = 10, Mills = extra.Mills }]);
        var deleted = await _service.DeleteTreatmentAsync(created.Single().Id!);

        Wire(created.Single()).GetProperty("_id").GetString().Should().Be(MongoObjectId.FromGuid(extra.Id));
        Wire(_creates.Should().ContainSingle().Subject.Doc).GetProperty("_id").GetString()
            .Should().Be(MongoObjectId.FromGuid(extra.Id));
        deleted.Should().BeTrue();
        _decomposer.Verify(
            d => d.DeleteByLegacyIdAsync(It.IsAny<string>(), WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
        _decomposer.Verify(
            d => d.DeleteByLegacyIdAsync(ClientId, WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The Nightscout write-back sink skips a treatment by its data source, so a connector's own
    /// temp target or profile switch announced without one would be posted back upstream.
    /// </summary>
    [Fact]
    public async Task AConnectorsTempTargetIsAnnouncedWithItsDataSource()
    {
        var spanId = Guid.CreateVersion7().ToString();
        Decomposes(new StateSpan { OriginalId = ClientId, Category = StateSpanCategory.TemporaryTarget });
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync(It.IsIn(ClientId, spanId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Treatment { Id = spanId, EventType = "Temporary Target", Mills = 1_790_000_000_000 });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Temporary Target", Mills = 1_790_000_000_000, DataSource = Connector }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().Be(Connector);
    }

    [Fact]
    public async Task AConnectorsProfileSwitchIsAnnouncedWithItsDataSource()
    {
        var activation = new ProfileActivation
        {
            Id = Guid.CreateVersion7(), Timestamp = At, LegacyId = ClientId, DataSource = Connector,
        };
        Decomposes(activation);
        _activations.Setup(r => r.GetByIdAsync(activation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(activation);
        Serves(new Treatment { Id = activation.Id.ToString(), EventType = "Profile Switch", Mills = activation.Mills });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Profile Switch", Mills = activation.Mills, DataSource = Connector }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().Be(Connector);
    }

    [Fact]
    public async Task ACareportalTempTargetIsAnnouncedWithoutADataSource()
    {
        var spanId = Guid.CreateVersion7().ToString();
        Decomposes(new StateSpan { OriginalId = ClientId, Category = StateSpanCategory.TemporaryTarget });
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync(It.IsIn(ClientId, spanId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Treatment { Id = spanId, EventType = "Temporary Target", Mills = 1_790_000_000_000 });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Temporary Target", Mills = 1_790_000_000_000, EnteredBy = "careportal" }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().BeNull();
    }

    [Fact]
    public async Task AServedDataSourceIsNotOverwrittenByTheUploadsOwn()
    {
        var activation = new ProfileActivation
        {
            Id = Guid.CreateVersion7(), Timestamp = At, LegacyId = ClientId, DataSource = "glooko-connector",
        };
        Decomposes(activation);
        _activations.Setup(r => r.GetByIdAsync(activation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(activation);
        Serves(new Treatment
        {
            Id = activation.Id.ToString(), EventType = "Profile Switch", Mills = activation.Mills, DataSource = "glooko-connector",
        });

        await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Profile Switch", Mills = activation.Mills, DataSource = Connector }]);

        _creates.Should().ContainSingle().Which.Doc.Should().BeOfType<Treatment>().Which.DataSource.Should().Be("glooko-connector");
    }

    [Fact]
    public async Task ATreatmentRefusedByAUserTombstoneIsNotAnnounced()
    {
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { SkippedDeleted = 1 });

        var created = await _service.CreateTreatmentsAsync(
            [new Treatment { Id = ClientId, EventType = "Correction Bolus", Insulin = 2, Mills = 1_790_000_000_000 }]);

        created.SkippedDeleted.Should().Be(1);
        _creates.Should().BeEmpty();
    }

    private void Decomposes(params object[] records)
    {
        var result = new DecompositionResult();
        result.CreatedRecords.AddRange(records);
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private void Serves(Treatment treatment) =>
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(
                treatment.Mills, treatment.Mills, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([treatment]);

    private static JsonElement Wire(object doc) => JsonSerializer.SerializeToElement(doc, doc.GetType());
}
