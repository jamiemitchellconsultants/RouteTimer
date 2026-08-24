using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class RiderModelRepository(RouteTimerDbContext context) : IRiderModelRepository
{
    public async Task<Guid> SaveAsync(RiderModel model, RiderProfile profileSnapshot, ModelValidationSummary validation, CancellationToken cancellationToken)
    {
        RiderModelAggregateValidator.Validate(model, profileSnapshot, validation);

        var id = Guid.NewGuid();
        var entity = new RiderModelEntity
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            ProfileRiderWeightKg = profileSnapshot.RiderWeightKg,
            ProfileBikeWeightKg = profileSnapshot.BikeAndEquipmentWeightKg,
            AlgorithmVersion = model.AlgorithmVersion,
            DrivetrainEfficiency = model.Coefficients.DrivetrainEfficiency,
            AirDensity = model.Coefficients.AirDensity,
            Crr = model.Coefficients.Crr,
            CdA = model.Coefficients.CdA,
            WasCalibrated = model.WasCalibrated,
            DescentWasLearned = model.DescentLimits.WasLearned,
            GlobalTypicalWatts = model.PowerModel.GlobalTypicalWatts,
            ValidationStatus = validation.Status.ToString(),
            ValidationMedianApe = validation.MedianAbsolutePercentageError,
            ValidationP90Ape = validation.P90AbsolutePercentageError
        };

        foreach (var band in model.PowerModel.Bands)
        {
            entity.Bands.Add(new PowerBandEntity
            {
                ModelId = id,
                GradeKey = band.GradeKey,
                DurationKey = band.DurationKey,
                TypicalWatts = band.TypicalWatts,
                EvidenceSeconds = band.Evidence.TotalSeconds,
                ActivityCount = band.ActivityCount,
                ShrinkageWeight = band.ShrinkageWeight,
                Confidence = band.Confidence.ToString()
            });
        }

        foreach (var cell in model.DescentLimits.Cells)
        {
            entity.DescentLimits.Add(new RiderModelDescentLimitEntity
            {
                ModelId = id,
                GradeKey = cell.GradeKey,
                CurvatureKey = cell.CurvatureKey,
                SpeedCapMetresPerSecond = cell.SpeedCapMetresPerSecond,
                EvidenceSeconds = cell.Evidence.TotalSeconds,
                ActivityCount = cell.ActivityCount,
                Confidence = cell.Confidence.ToString(),
                IsFallback = cell.IsFallback
            });
        }

        context.RiderModels.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<RiderModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var entity = await context.RiderModels
            .Include(model => model.Bands)
            .Include(model => model.DescentLimits)
            .OrderByDescending(model => model.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<RiderModelSnapshot?> GetAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var entity = await context.RiderModels
            .Include(model => model.Bands)
            .Include(model => model.DescentLimits)
            .SingleOrDefaultAsync(model => model.Id == modelId, cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    private static RiderModelSnapshot ToSnapshot(RiderModelEntity entity)
    {
        try
        {
            return ReconstructSnapshot(entity);
        }
        catch (InvalidPersistedRiderModelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidPersistedRiderModelException("The persisted rider model is invalid.", exception);
        }
    }

    private static RiderModelSnapshot ReconstructSnapshot(RiderModelEntity entity)
    {
        var bands = entity.Bands
            .OrderBy(band => band.GradeKey, StringComparer.Ordinal)
            .ThenBy(band => band.DurationKey, StringComparer.Ordinal)
            .Select(band => new PowerBand(
                band.GradeKey,
                band.DurationKey,
                band.TypicalWatts,
                ParseDuration(band.EvidenceSeconds, "power-band evidence"),
                band.ActivityCount,
                band.ShrinkageWeight,
                ParseCanonicalEnum<ConfidenceLevel>(band.Confidence, "power-band confidence")))
            .ToList();

        var powerModel = new PowerModel(bands, entity.GlobalTypicalWatts);
        var coefficients = new PhysicalCoefficients(entity.DrivetrainEfficiency, entity.AirDensity, entity.Crr, entity.CdA);
        var descentLimits = ToDescentLimits(entity);
        if (entity.DescentWasLearned != descentLimits.WasLearned)
            throw new ArgumentException("Persisted descent provenance does not match the stored descent cells.");
        var riderModel = new RiderModel(powerModel, coefficients, descentLimits, entity.WasCalibrated, entity.AlgorithmVersion);
        var profileSnapshot = new RiderProfile(entity.ProfileRiderWeightKg, entity.ProfileBikeWeightKg);
        var validation = new ModelValidationSummary(
            ParseCanonicalEnum<ModelValidationStatus>(entity.ValidationStatus, "validation status"),
            entity.ValidationMedianApe,
            entity.ValidationP90Ape);
        RiderModelAggregateValidator.Validate(riderModel, profileSnapshot, validation);

        return new RiderModelSnapshot(entity.Id, entity.CreatedAt, profileSnapshot, riderModel, validation);
    }

    private static DescentLimitModel ToDescentLimits(RiderModelEntity entity)
    {
        var cells = new List<DescentLimitCell>(entity.DescentLimits.Count);
        foreach (var cell in entity.DescentLimits)
        {
            if (string.IsNullOrWhiteSpace(cell.GradeKey) || string.IsNullOrWhiteSpace(cell.CurvatureKey) ||
                !double.IsFinite(cell.SpeedCapMetresPerSecond) || cell.SpeedCapMetresPerSecond <= 0 || cell.SpeedCapMetresPerSecond > 20 ||
                !double.IsFinite(cell.EvidenceSeconds) || cell.EvidenceSeconds < 0 || cell.EvidenceSeconds > TimeSpan.MaxValue.TotalSeconds ||
                cell.ActivityCount < 0 ||
                !TryParseCanonicalEnum(cell.Confidence, out ConfidenceLevel confidence))
            {
                throw new InvalidOperationException("Persisted descent cell data is malformed.");
            }

            cells.Add(new DescentLimitCell(
                cell.GradeKey,
                cell.CurvatureKey,
                cell.SpeedCapMetresPerSecond,
                TimeSpan.FromSeconds(cell.EvidenceSeconds),
                cell.ActivityCount,
                confidence,
                cell.IsFallback));
        }

        try
        {
            return new DescentLimitModel(cells);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Persisted descent cell data is malformed.", exception);
        }
    }

    private static TimeSpan ParseDuration(double seconds, string field)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentException($"Persisted {field} is malformed.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static TEnum ParseCanonicalEnum<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (!TryParseCanonicalEnum(value, out TEnum parsed))
            throw new ArgumentException($"Persisted {field} is malformed.");
        return parsed;
    }

    private static bool TryParseCanonicalEnum<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        if (value is not null &&
            Enum.TryParse(value, ignoreCase: false, out parsed) &&
            Enum.IsDefined(parsed) &&
            string.Equals(value, Enum.GetName(parsed), StringComparison.Ordinal))
        {
            return true;
        }

        parsed = default;
        return false;
    }
}
