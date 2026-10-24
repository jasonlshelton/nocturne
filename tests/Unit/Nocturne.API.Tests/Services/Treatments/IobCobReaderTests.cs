using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class IobCobReaderTests
{
    private readonly List<ApsSnapshot> _snapshots = [];
    private readonly Mock<IBolusRepository> _boluses = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<ICarbIntakeRepository> _carbs = new();
    private readonly List<Bolus> _storedBoluses = [];
    private readonly List<CarbIntake> _storedCarbs = [];

    public IobCobReaderTests()
    {
        _boluses
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _storedBoluses.ToList());
        _tempBasals
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TempBasal>());
        _carbs
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _storedCarbs.ToList());
    }

    private IobCobReader NewReader(string? partition = "full", ICobCalculator? cobCalculator = null)
    {
        var aps = new Mock<IApsSnapshotRepository>();
        aps.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int limit, int _, bool _, CancellationToken _) =>
                _snapshots
                    .Where(s => s.Timestamp >= from && s.Timestamp <= to)
                    .OrderByDescending(s => s.Timestamp)
                    .Take(limit)
                    .ToList()
                    .AsEnumerable());
        var pump = new Mock<IPumpSnapshotRepository>();
        pump.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        var therapy = new Mock<ITherapySettingsResolver>();
        therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapy.Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(4.0);
        therapy.Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(30.0);
        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(50.0);
        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio.Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(10.0);
        var basal = new Mock<IBasalRateResolver>();
        basal.Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(1.0);

        var iob = new IobCalculator(therapy.Object, sensitivity.Object, basal.Object, aps.Object, pump.Object);
        var cob = new CobCalculator(
            NullLogger<CobCalculator>.Instance, iob, sensitivity.Object, carbRatio.Object, therapy.Object, aps.Object);

        return new IobCobReader(
            iob, cobCalculator ?? cob, _boluses.Object, _tempBasals.Object, _carbs.Object,
            TestTenantCache.Create(partition: partition), NullLogger<IobCobReader>.Instance);
    }

    private void Snapshot(AidAlgorithm algorithm, int minutesAgo, double? iob, double? cob) =>
        _snapshots.Add(new ApsSnapshot
        {
            AidAlgorithm = algorithm,
            Timestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
            Iob = iob,
            Cob = cob,
        });

    private static long NowMills => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Task<IobCobReading> Live(IobCobReader reader) => reader.ReadTotalAsync(NowMills, live: true);

    private void VerifyBolusReads(Times times) =>
        _boluses.Verify(
            r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            times);

    [Theory]
    [InlineData(AidAlgorithm.AndroidAps)]
    [InlineData(AidAlgorithm.Trio)]
    [InlineData(AidAlgorithm.OpenAps)]
    public async Task RecentOrefSnapshot_SuppliesIobAndCob_WithNoTreatments(AidAlgorithm algorithm)
    {
        Snapshot(algorithm, 5, iob: 1.254, cob: 23);

        var current = await Live(NewReader());

        current.Iob.Iob.Should().Be(1.254);
        IobResult.FormatDisplay(current.Iob.Iob).Should().Be("1.25");
        current.Iob.Source.Should().Be("OpenAPS");
        current.Cob.Cob.Should().Be(23);
        current.Cob.Source.Should().Be("OpenAPS");
    }

    [Fact]
    public async Task RecentLoopSnapshot_SuppliesItsValuesAndNamesLoop()
    {
        Snapshot(AidAlgorithm.Loop, 3, iob: 0.8, cob: 12);

        var current = await Live(NewReader());

        current.Iob.Iob.Should().Be(0.8);
        current.Iob.Source.Should().Be("Loop");
        current.Cob.Cob.Should().Be(12);
        current.Cob.Source.Should().Be("Loop");
    }

    [Fact]
    public async Task SnapshotOlderThanThirtyMinutes_FallsBackToTreatments()
    {
        Snapshot(AidAlgorithm.AndroidAps, 45, iob: 9.99, cob: 99);
        _storedBoluses.Add(new Bolus { Timestamp = DateTime.UtcNow.AddMinutes(-30), Insulin = 2 });

        var current = await Live(NewReader());

        current.Iob.Source.Should().NotBe("OpenAPS");
        current.Iob.Iob.Should().BeInRange(0.01, 2);
        current.Cob.Cob.Should().Be(0);
    }

    [Fact]
    public async Task NoSnapshotAndNoTreatments_IsZero()
    {
        var current = await Live(NewReader());

        current.Iob.Iob.Should().Be(0);
        IobResult.FormatDisplay(current.Iob.Iob).Should().Be("0");
        current.Cob.Cob.Should().Be(0);
    }

    [Fact]
    public async Task ReadsTheLookbacks_EndingAtTheInstant()
    {
        var at = NowMills - 3 * 60 * 60 * 1000;
        var atUtc = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime;

        await NewReader().ReadInputsAsync(at);

        var insulinFrom = atUtc.AddHours(-IIobCobReader.InsulinLookbackHours);
        _boluses.Verify(r => r.GetAsync(
            insulinFrom, atUtc, null, null, It.IsAny<int>(), 0, false, It.IsAny<bool>(),
            It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        _tempBasals.Verify(r => r.GetAsync(
            insulinFrom, atUtc, null, null, It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()), Times.Once);
        _carbs.Verify(r => r.GetAsync(
            atUtc.AddHours(-IIobCobReader.CarbLookbackHours), atUtc, null, null, It.IsAny<int>(), 0, false, It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecondLiveReadWithinAMinute_IsServedFromTheCache()
    {
        var reader = NewReader();

        await Live(reader);
        await Live(reader);

        VerifyBolusReads(Times.Once());
    }

    [Fact]
    public async Task ReaderWithoutAPartition_NeverTouchesTheCache()
    {
        var reader = NewReader(partition: null);

        await Live(reader);
        await Live(reader);

        VerifyBolusReads(Times.Exactly(2));
    }

    [Fact]
    public async Task AReadAtAPastInstant_IsNeverServedFromTheLiveCache()
    {
        var reader = NewReader();
        Snapshot(AidAlgorithm.AndroidAps, 5, iob: 1.5, cob: 10);
        await Live(reader);

        var past = await reader.ReadTotalAsync(NowMills - 3 * 60 * 60 * 1000, live: false);

        past.Iob.Iob.Should().Be(0);
        VerifyBolusReads(Times.Exactly(2));
    }

    [Fact]
    public async Task ACobFailure_LeavesTheIob()
    {
        Snapshot(AidAlgorithm.AndroidAps, 5, iob: 1.5, cob: 10);
        var failing = new Mock<ICobCalculator>();
        failing
            .Setup(c => c.CalculateTotalAsync(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cob"));

        var current = await Live(NewReader(cobCalculator: failing.Object));

        current.Iob.Iob.Should().Be(1.5);
        current.Cob.Should().BeNull();
    }
}
