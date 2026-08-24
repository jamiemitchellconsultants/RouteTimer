using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Repositories;

namespace RouteTimer.Persistence.Tests;

public sealed class RiderModelRepositoryValidationTests
{
    public static TheoryData<string> InvalidAggregates => new()
    {
        "null-power-model", "null-bands", "null-band", "null-coefficients", "null-descents", "null-profile", "null-validation",
        "null-version", "blank-version", "noncanonical-version",
        "rider-weight-zero", "rider-weight-nan", "bike-weight-zero", "bike-weight-infinity",
        "efficiency-zero", "efficiency-over-one", "efficiency-nan", "density-zero", "crr-negative", "cda-infinity",
        "calibrated-crr-low", "calibrated-crr-high", "calibrated-cda-low", "calibrated-cda-high",
        "route-v2-efficiency", "route-v2-density", "route-v2-crr", "route-v2-cda",
        "global-watts-negative", "global-watts-nan", "band-watts-negative", "band-watts-nan", "band-evidence-negative",
        "band-activities-negative", "band-shrinkage-negative", "band-shrinkage-high", "band-shrinkage-nan", "band-confidence",
        "band-grade-null", "band-duration-null", "duplicate-band",
        "validation-status", "validation-median-negative", "validation-median-nan", "validation-p90-negative", "validation-p90-infinity",
        "descent-contradiction",
    };

    // Break caught: repository save accepts aggregates that cannot safely be reconstructed or predicted.
    [Theory]
    [MemberData(nameof(InvalidAggregates))]
    public async Task Save_rejects_invalid_whole_model_aggregates_before_persistence(string kind)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var (model, profile, validation) = InvalidAggregate(kind);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.SaveAsync(model, profile, validation, CancellationToken.None));

        Assert.Empty(context.RiderModels);
    }

    [Fact]
    public async Task Save_accepts_uncalibrated_legacy_coefficients_and_foreign_band_keys()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var model = ValidModel() with
        {
            Coefficients = new PhysicalCoefficients(.5, .75, 0, 0),
            PowerModel = new PowerModel(
                [new PowerBand("foreign-grade", "foreign-duration", 123, TimeSpan.Zero, 0, 0, ConfidenceLevel.Low)],
                123),
        };

        var id = await repository.SaveAsync(model, ValidProfile(), ValidValidation(), CancellationToken.None);
        var loaded = await repository.GetAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(model.Coefficients, loaded.Model.Coefficients);
        Assert.Equal(model.PowerModel.Bands, loaded.Model.PowerModel.Bands);
    }

    // Break caught: malformed persisted fields either parse permissively, escape heterogeneous exceptions, or reconstruct invalid snapshots.
    [Theory]
    [InlineData("version")]
    [InlineData("profile")]
    [InlineData("coefficients")]
    [InlineData("route-v2")]
    [InlineData("power-global")]
    [InlineData("power-band")]
    [InlineData("power-confidence-numeric")]
    [InlineData("power-confidence-case")]
    [InlineData("validation-status-numeric")]
    [InlineData("validation-status-case")]
    [InlineData("validation-ape")]
    [InlineData("descent")]
    public async Task Get_wraps_every_malformed_persisted_aggregate_in_a_dedicated_exception(string kind)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var id = await repository.SaveAsync(ValidModel(), ValidProfile(), ValidValidation(), CancellationToken.None);
        var entity = await context.RiderModels.Include(model => model.Bands).Include(model => model.DescentLimits).SingleAsync();
        Corrupt(entity, kind);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<RouteTimer.Services.Persistence.InvalidPersistedRiderModelException>(
            () => repository.GetAsync(id, CancellationToken.None));
    }

    private static (RiderModel Model, RiderProfile Profile, ModelValidationSummary Validation) InvalidAggregate(string kind)
    {
        var model = ValidModel();
        var profile = ValidProfile();
        var validation = ValidValidation();
        var band = model.PowerModel.Bands[0];

        switch (kind)
        {
            case "null-power-model": model = model with { PowerModel = null! }; break;
            case "null-bands": model = model with { PowerModel = new PowerModel(null!, 200) }; break;
            case "null-band": model = model with { PowerModel = new PowerModel([null!], 200) }; break;
            case "null-coefficients": model = model with { Coefficients = null! }; break;
            case "null-descents": model = model with { DescentLimits = null! }; break;
            case "null-profile": profile = null!; break;
            case "null-validation": validation = null!; break;
            case "null-version": model = model with { AlgorithmVersion = null! }; break;
            case "blank-version": model = model with { AlgorithmVersion = "  " }; break;
            case "noncanonical-version": model = model with { AlgorithmVersion = " legacy-v1 " }; break;
            case "rider-weight-zero": profile = profile with { RiderWeightKg = 0 }; break;
            case "rider-weight-nan": profile = profile with { RiderWeightKg = double.NaN }; break;
            case "bike-weight-zero": profile = profile with { BikeAndEquipmentWeightKg = 0 }; break;
            case "bike-weight-infinity": profile = profile with { BikeAndEquipmentWeightKg = double.PositiveInfinity }; break;
            case "efficiency-zero": model = model with { Coefficients = model.Coefficients with { DrivetrainEfficiency = 0 } }; break;
            case "efficiency-over-one": model = model with { Coefficients = model.Coefficients with { DrivetrainEfficiency = 1.01 } }; break;
            case "efficiency-nan": model = model with { Coefficients = model.Coefficients with { DrivetrainEfficiency = double.NaN } }; break;
            case "density-zero": model = model with { Coefficients = model.Coefficients with { AirDensity = 0 } }; break;
            case "crr-negative": model = model with { Coefficients = model.Coefficients with { Crr = -.001 } }; break;
            case "cda-infinity": model = model with { Coefficients = model.Coefficients with { CdA = double.PositiveInfinity } }; break;
            case "calibrated-crr-low": model = CalibratedLegacy(model.Coefficients with { Crr = .0019 }); break;
            case "calibrated-crr-high": model = CalibratedLegacy(model.Coefficients with { Crr = .0121 }); break;
            case "calibrated-cda-low": model = CalibratedLegacy(model.Coefficients with { CdA = .149 }); break;
            case "calibrated-cda-high": model = CalibratedLegacy(model.Coefficients with { CdA = .601 }); break;
            case "route-v2-efficiency": model = RouteV2(model.Coefficients with { DrivetrainEfficiency = .96 }); break;
            case "route-v2-density": model = RouteV2(model.Coefficients with { AirDensity = 1.2 }); break;
            case "route-v2-crr": model = RouteV2(model.Coefficients with { Crr = .0019 }); break;
            case "route-v2-cda": model = RouteV2(model.Coefficients with { CdA = .601 }); break;
            case "global-watts-negative": model = model with { PowerModel = model.PowerModel with { GlobalTypicalWatts = -1 } }; break;
            case "global-watts-nan": model = model with { PowerModel = model.PowerModel with { GlobalTypicalWatts = double.NaN } }; break;
            case "band-watts-negative": model = WithBand(model, band with { TypicalWatts = -1 }); break;
            case "band-watts-nan": model = WithBand(model, band with { TypicalWatts = double.NaN }); break;
            case "band-evidence-negative": model = WithBand(model, band with { Evidence = TimeSpan.FromSeconds(-1) }); break;
            case "band-activities-negative": model = WithBand(model, band with { ActivityCount = -1 }); break;
            case "band-shrinkage-negative": model = WithBand(model, band with { ShrinkageWeight = -.01 }); break;
            case "band-shrinkage-high": model = WithBand(model, band with { ShrinkageWeight = 1.01 }); break;
            case "band-shrinkage-nan": model = WithBand(model, band with { ShrinkageWeight = double.NaN }); break;
            case "band-confidence": model = WithBand(model, band with { Confidence = (ConfidenceLevel)99 }); break;
            case "band-grade-null": model = WithBand(model, band with { GradeKey = null! }); break;
            case "band-duration-null": model = WithBand(model, band with { DurationKey = null! }); break;
            case "duplicate-band": model = model with { PowerModel = model.PowerModel with { Bands = [band, band] } }; break;
            case "validation-status": validation = validation with { Status = (ModelValidationStatus)99 }; break;
            case "validation-median-negative": validation = validation with { MedianAbsolutePercentageError = -.01 }; break;
            case "validation-median-nan": validation = validation with { MedianAbsolutePercentageError = double.NaN }; break;
            case "validation-p90-negative": validation = validation with { P90AbsolutePercentageError = -.01 }; break;
            case "validation-p90-infinity": validation = validation with { P90AbsolutePercentageError = double.PositiveInfinity }; break;
            case "descent-contradiction": model = model with { DescentLimits = MalformedDescent() }; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return (model, profile, validation);

        RiderModel CalibratedLegacy(PhysicalCoefficients coefficients) => model with { Coefficients = coefficients, WasCalibrated = true };
        RiderModel RouteV2(PhysicalCoefficients coefficients) => model with { Coefficients = coefficients, AlgorithmVersion = "route-model-v2" };
    }

    private static RiderModel WithBand(RiderModel model, PowerBand band) =>
        model with { PowerModel = model.PowerModel with { Bands = [band] } };

    private static RiderModel ValidModel() => new(
        new PowerModel([new PowerBand("foreign-grade", "foreign-duration", 200, TimeSpan.FromMinutes(5), 2, .25, ConfidenceLevel.Medium)], 200),
        PhysicalCoefficients.Default,
        DescentLimitModel.Conservative,
        false,
        "legacy-v1");

    private static RiderProfile ValidProfile() => new(75, 10);

    private static ModelValidationSummary ValidValidation() => new(ModelValidationStatus.Passed, .05, .08);

    private static DescentLimitModel MalformedDescent()
    {
        var cells = DescentLimitModel.Conservative.Cells.ToArray();
        cells[0] = cells[0] with { Confidence = ConfidenceLevel.High };
        var model = (DescentLimitModel)RuntimeHelpers.GetUninitializedObject(typeof(DescentLimitModel));
        typeof(DescentLimitModel).GetField("<Cells>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, Array.AsReadOnly(cells));
        return model;
    }

    internal static void Corrupt(RiderModelEntity entity, string kind)
    {
        switch (kind)
        {
            case "version": entity.AlgorithmVersion = " "; break;
            case "profile": entity.ProfileRiderWeightKg = 0; break;
            case "coefficients": entity.WasCalibrated = true; entity.Crr = .001; break;
            case "route-v2": entity.AlgorithmVersion = "route-model-v2"; entity.DrivetrainEfficiency = .96; break;
            case "power-global": entity.GlobalTypicalWatts = -1; break;
            case "power-band": entity.Bands[0].ShrinkageWeight = 1.01; break;
            case "power-confidence-numeric": entity.Bands[0].Confidence = "1"; break;
            case "power-confidence-case": entity.Bands[0].Confidence = "medium"; break;
            case "validation-status-numeric": entity.ValidationStatus = "2"; break;
            case "validation-status-case": entity.ValidationStatus = "passed"; break;
            case "validation-ape": entity.ValidationMedianApe = double.NaN; break;
            case "descent": entity.DescentLimits[0].Confidence = "High"; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}
