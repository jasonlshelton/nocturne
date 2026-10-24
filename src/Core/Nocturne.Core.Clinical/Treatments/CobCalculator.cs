using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>
/// V4-native COB calculator operating on <see cref="CarbIntake"/> records.
/// Implements the decay algorithm on V4 domain types directly.
/// </summary>
/// <seealso cref="ICobCalculator"/>
public class CobCalculator(
    ILogger<CobCalculator> logger,
    IIobCalculator iobCalculator,
    ISensitivityResolver sensitivityResolver,
    ICarbRatioResolver carbRatioResolver,
    ITherapySettingsResolver therapySettingsResolver,
    IApsSnapshotRepository apsSnapshotRepo
) : ICobCalculator
{
    // Constants from legacy implementation - exact values required
    private const double LIVER_SENS_RATIO = 8.0; // Legacy: var liverSensRatio = 8;
    private const int DELAY_MINUTES = 20; // Legacy: const delay = 20;

    // Default profile values to use when resolver data is unavailable
    private const double DEFAULT_CARB_ABSORPTION_RATE = 30.0;
    private const double DEFAULT_SENSITIVITY = 95.0;
    private const double DEFAULT_CARB_RATIO = 18.0;

    /// <inheritdoc />
    public async Task<CobResult> CalculateTotalAsync(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        long time,
        bool live = false,
        CancellationToken ct = default
    )
    {
        var hasData = await therapySettingsResolver.HasDataAsync(ct);

        if (hasData)
        {
            try
            {
                var sens = GetSensitivityOrDefault(time);
                var carbRatio = GetCarbRatioOrDefault(time);
                if (sens <= 0 || carbRatio <= 0)
                {
                    logger.LogWarning(
                        "For the COB plugin to function your treatment profile must have both sens and carbratio fields. Using defaults."
                    );
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "For the COB plugin to function your treatment profile must have both sens and carbratio fields. Using defaults."
                );
            }
        }

        // A reported COB of exactly zero is a real value ("no carbs on board"), not a missing one.
        if (await GetDeviceCobAsync(time, live, ct) is { } deviceCob)
        {
            return AddDisplay(deviceCob);
        }

        // Fall back to carb-intake-based COB calculation
        var treatmentCOB =
            carbIntakes?.Any() == true
                ? FromCarbIntakes(carbIntakes, boluses, tempBasals, time)
                : new CobResult();

        var result = new CobResult
        {
            Cob = treatmentCOB.Cob,
            Activity = treatmentCOB.Activity,
            DecayedBy = treatmentCOB.DecayedBy,
            IsDecaying = treatmentCOB.IsDecaying,
            CarbsHr = treatmentCOB.CarbsHr,
            RawCarbImpact = treatmentCOB.RawCarbImpact,
            LastCarbs = treatmentCOB.LastCarbs,
            Source = "Care Portal",
            TreatmentCOB = treatmentCOB,
        };

        return AddDisplay(result);
    }

    /// <inheritdoc />
    public CobResult FromCarbIntakes(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        long time
    )
    {
        // Insulin activity for the dynamic carb-delay calc comes from the full IOB total
        // (boluses + temp basals + device snapshots). Carbs still absorbing ask for times after the
        // requested one, and a temp still running has delivered nothing past it.
        double ActivityAt(long t) =>
            iobCalculator
                .CalculateTotalAsync(boluses ?? [], tempBasals, t, deliveredUntil: time, ct: default)
                .GetAwaiter()
                .GetResult()
                ?.Activity ?? double.NaN;

        return FromCarbIntakesCore(
            carbIntakes,
            time,
            GetCarbAbsorptionRateOrDefault,
            GetSensitivityOrDefault,
            GetCarbRatioOrDefault,
            ActivityAt
        );
    }

    /// <inheritdoc />
    public CobResult FromCarbIntakes(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        TherapySnapshot snapshot,
        Func<long, double> scheduledBasalAt,
        long time
    )
    {
        // All resolutions come from the in-memory snapshot — no DB round trips in the tick loop.
        // Profile-absent defaults match the legacy GetXOrDefault helpers.
        double CarbAbsorptionAt(long _) =>
            snapshot.CarbsPerHour > 0 ? snapshot.CarbsPerHour : DEFAULT_CARB_ABSORPTION_RATE;
        double SensitivityAt(long t) =>
            snapshot.SensitivityAt(t) is var s && s > 0 ? s : DEFAULT_SENSITIVITY;
        double CarbRatioAt(long t) =>
            snapshot.CarbRatioAt(t) is var c && c > 0 ? c : DEFAULT_CARB_RATIO;

        // The bolus plus net basal activity the async IOB total gives, computed in-memory.
        double ActivityAt(long t) =>
            (iobCalculator.FromBoluses(boluses ?? [], snapshot, t).Activity ?? double.NaN)
            + (iobCalculator.FromTempBasals(tempBasals ?? [], snapshot, scheduledBasalAt, t, deliveredUntil: time).Activity ?? 0);

        return FromCarbIntakesCore(
            carbIntakes,
            time,
            CarbAbsorptionAt,
            SensitivityAt,
            CarbRatioAt,
            ActivityAt
        );
    }

    private CobResult FromCarbIntakesCore(
        List<CarbIntake> carbIntakes,
        long currentTime,
        Func<long, double> carbAbsorptionAt,
        Func<long, double> sensitivityAt,
        Func<long, double> carbRatioAt,
        Func<long, double> activityAt
    )
    {
        var totalCOB = 0.0;
        CarbIntake? lastCarbs = null;
        var isDecaying = 0.0;
        var lastDecayedBy = 0L;

        var sortedIntakes = (carbIntakes ?? new List<CarbIntake>()).OrderBy(c => c.Mills).ToList();

        foreach (var carbIntake in sortedIntakes)
        {
            var carbAbsorptionRateFromProfile = carbAbsorptionAt(carbIntake.Mills);

            if (carbIntake.Carbs > 0 && carbIntake.Mills < currentTime)
            {
                lastCarbs = carbIntake;
                var cCalc = CobCalc(carbIntake, lastDecayedBy, currentTime);
                if (cCalc == null)
                    continue;

                var decaysinHr =
                    (cCalc.DecayedBy.ToUnixTimeMilliseconds() - currentTime) / 1000.0 / 60.0 / 60.0;

                if (decaysinHr > -10)
                {
                    var actStart = activityAt(lastDecayedBy);
                    var actEnd = activityAt(cCalc.DecayedBy.ToUnixTimeMilliseconds());

                    var avgActivity = (actStart + actEnd) / 2.0;

                    var sensFromProfile = sensitivityAt(carbIntake.Mills);
                    var carbRatioFromProfile = carbRatioAt(carbIntake.Mills);

                    var delayedCarbs =
                        carbRatioFromProfile * ((avgActivity * LIVER_SENS_RATIO) / sensFromProfile);
                    var delayMinutes = Math.Round(
                        (delayedCarbs / carbAbsorptionRateFromProfile) * 60
                    );

                    if (delayMinutes > 0)
                    {
                        cCalc.DecayedBy = cCalc.DecayedBy.AddMinutes(delayMinutes);
                        decaysinHr =
                            (cCalc.DecayedBy.ToUnixTimeMilliseconds() - currentTime)
                            / 1000.0
                            / 60.0
                            / 60.0;
                    }
                }

                lastDecayedBy = cCalc.DecayedBy.ToUnixTimeMilliseconds();

                if (decaysinHr > 0)
                {
                    totalCOB += Math.Min(
                        carbIntake.Carbs,
                        decaysinHr * carbAbsorptionRateFromProfile
                    );
                    isDecaying = cCalc.IsDecaying;
                }
                else
                {
                    totalCOB = 0;
                }
            }
        }

        var sens = sensitivityAt(currentTime);
        var carbRatio = carbRatioAt(currentTime);
        var carbAbsorptionRate = carbAbsorptionAt(currentTime);

        var rawCarbImpact = (((isDecaying * sens) / carbRatio) * carbAbsorptionRate) / 60.0;

        return new CobResult
        {
            DecayedBy = lastDecayedBy,
            IsDecaying = isDecaying,
            CarbsHr = carbAbsorptionRate,
            RawCarbImpact = rawCarbImpact,
            Cob = totalCOB,
            LastCarbs = lastCarbs,
        };
    }

    /// <inheritdoc />
    public CarbCobContribution CalcCarbIntake(CarbIntake carbIntake, long time)
    {
        var currentTime = time;

        var hasData = therapySettingsResolver.HasDataAsync().GetAwaiter().GetResult();
        if (!hasData)
        {
            logger.LogWarning("For the COB plugin to function you need a treatment profile");
            return new CarbCobContribution();
        }

        try
        {
            var sens = GetSensitivityOrDefault(currentTime);
            var carbRatio = GetCarbRatioOrDefault(currentTime);
            if (sens <= 0 || carbRatio <= 0)
            {
                logger.LogWarning(
                    "For the COB plugin to function your treatment profile must have both sens and carbratio fields"
                );
                return new CarbCobContribution();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "For the COB plugin to function your treatment profile must have both sens and carbratio fields"
            );
            return new CarbCobContribution();
        }

        var cobContrib = 0.0;
        var activityContrib = 0.0;
        long? decayedBy = null;
        var isDecaying = false;
        if (carbIntake.Carbs > 0 && carbIntake.Mills < currentTime)
        {
            var cCalc = CobCalc(carbIntake, 0, currentTime);
            if (cCalc != null)
            {
                var decayedByTime = cCalc.DecayedBy.ToUnixTimeMilliseconds();
                var decaysinHr = (decayedByTime - currentTime) / 1000.0 / 60.0 / 60.0;
                if (decaysinHr > 0)
                {
                    var carbAbsorptionRate = carbIntake.AbsorptionTime.HasValue
                        ? (carbIntake.Carbs / (carbIntake.AbsorptionTime.Value / 60.0))
                        : GetCarbAbsorptionRateOrDefault(carbIntake.Mills);

                    cobContrib = Math.Min(
                        carbIntake.Carbs,
                        decaysinHr * carbAbsorptionRate
                    );
                }
                else
                {
                    cobContrib = 0;
                }

                decayedBy = decayedByTime;
                isDecaying = cCalc.IsDecaying > 0;
            }
        }

        if (cobContrib > 0)
        {
            var carbRatio = GetCarbRatioOrDefault(currentTime);
            if (carbRatio > 0)
            {
                activityContrib = cobContrib / carbRatio;
            }
        }

        return new CarbCobContribution
        {
            CobContrib = cobContrib,
            ActivityContrib = activityContrib,
            DecayedBy = decayedBy,
            IsDecaying = isDecaying,
        };
    }

    #region Private Helpers

    private CobCalcResult? CobCalc(
        CarbIntake carbIntake,
        long lastDecayedBy,
        long time
    )
    {
        if (carbIntake.Carbs <= 0)
        {
            return null;
        }

        const int delay = DELAY_MINUTES;
        var carbTime = DateTimeOffset.FromUnixTimeMilliseconds(carbIntake.Mills);

        var carbsHr = carbIntake.AbsorptionTime.HasValue
            ? (carbIntake.Carbs / (carbIntake.AbsorptionTime.Value / 60.0))
            : GetCarbAbsorptionRateOrDefault(carbIntake.Mills);

        // No ApplyAdvancedAbsorptionAdjustments — absorption rate comes purely from
        // carbIntake.AbsorptionTime or the profile default.

        var carbsMin = carbsHr / 60.0;
        var decayedBy = carbTime;
        var minutesleft =
            lastDecayedBy > 0 ? (lastDecayedBy - carbIntake.Mills) / 1000.0 / 60.0 : 0.0;

        var additionalMinutes = Math.Max(delay, minutesleft) + (carbIntake.Carbs / carbsMin);
        decayedBy = decayedBy.AddMinutes(additionalMinutes);

        var initialCarbs =
            delay > minutesleft
                ? Convert.ToInt32(carbIntake.Carbs)
                : Convert.ToInt32(carbIntake.Carbs) + (minutesleft * carbsMin);

        var startDecay = carbTime.AddMinutes(delay);
        var isDecaying =
            time < lastDecayedBy || time > startDecay.ToUnixTimeMilliseconds() ? 1.0 : 0.0;

        return new CobCalcResult
        {
            InitialCarbs = initialCarbs,
            DecayedBy = decayedBy,
            IsDecaying = isDecaying,
            CarbTime = carbTime,
        };
    }

    /// <summary>
    /// The device COB that holds at <paramref name="time"/> by <see cref="DeviceReadings.At"/>, read
    /// from the newest APS snapshot in its window; a pump reports no COB. Null where the calculation holds.
    /// </summary>
    internal async Task<CobResult?> GetDeviceCobAsync(long time, bool live, CancellationToken ct = default)
    {
        var (from, to) = DeviceReportedValues.Window(time, live);
        var aps = (await apsSnapshotRepo.GetAsync(
            from: DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            to: DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            device: null, source: null, limit: 1, offset: 0, descending: true, ct: ct))
            .Select(s => new DeviceReading(
                DeviceReading.MillsOf(s.Timestamp), DeviceSource.Aps, s.Iob, s.Cob,
                Name: s.AidAlgorithm == AidAlgorithm.Loop ? "Loop" : "OpenAPS", Device: s.Device));

        if (new DeviceReadings(aps, []).At(time, live).Cob is not { } reading)
            return null;

        return new CobResult
        {
            Cob = reading.Cob!.Value,
            Source = reading.Name,
            Device = reading.Device,
            Mills = reading.Mills,
        };
    }

    private static CobResult AddDisplay(CobResult cob)
    {
        if (cob.Cob <= 0)
            return cob;

        // Invariant: these strings go out over the API, so they must not pick up the
        // server's decimal separator.
        var display = (Math.Round(cob.Cob * 10) / 10).ToString(CultureInfo.InvariantCulture);
        cob.Display = display;
        cob.DisplayLine = $"COB: {display}g";

        return cob;
    }

    private double GetCarbAbsorptionRateOrDefault(long time)
    {
        try
        {
            var value = therapySettingsResolver.GetCarbAbsorptionRateAsync(time, null).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_CARB_ABSORPTION_RATE;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve carb absorption rate at {Time}; using default.", time);
            return DEFAULT_CARB_ABSORPTION_RATE;
        }
    }

    private double GetSensitivityOrDefault(long time)
    {
        try
        {
            var value = sensitivityResolver.GetSensitivityAsync(time, null).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_SENSITIVITY;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve sensitivity at {Time}; using default.", time);
            return DEFAULT_SENSITIVITY;
        }
    }

    private double GetCarbRatioOrDefault(long time)
    {
        try
        {
            var value = carbRatioResolver.GetCarbRatioAsync(time, null).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_CARB_RATIO;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve carb ratio at {Time}; using default.", time);
            return DEFAULT_CARB_RATIO;
        }
    }

    #endregion
}
