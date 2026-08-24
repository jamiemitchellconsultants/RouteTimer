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
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(validation);

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
        var bands = entity.Bands
            .OrderBy(band => band.GradeKey, StringComparer.Ordinal)
            .ThenBy(band => band.DurationKey, StringComparer.Ordinal)
            .Select(band => new PowerBand(
                band.GradeKey,
                band.DurationKey,
                band.TypicalWatts,
                TimeSpan.FromSeconds(band.EvidenceSeconds),
                band.ActivityCount,
                band.ShrinkageWeight,
                Enum.Parse<ConfidenceLevel>(band.Confidence)))
            .ToList();

        var powerModel = new PowerModel(bands, entity.GlobalTypicalWatts);
        var coefficients = new PhysicalCoefficients(entity.DrivetrainEfficiency, entity.AirDensity, entity.Crr, entity.CdA);
        var descentLimits = ToDescentLimits(entity);
        if (entity.DescentWasLearned != descentLimits.WasLearned)
            throw new InvalidOperationException("Persisted descent provenance does not match the stored descent cells.");
        var riderModel = new RiderModel(powerModel, coefficients, descentLimits, entity.WasCalibrated, entity.AlgorithmVersion);
        var profileSnapshot = new RiderProfile(entity.ProfileRiderWeightKg, entity.ProfileBikeWeightKg);
        var validation = new ModelValidationSummary(
            Enum.Parse<ModelValidationStatus>(entity.ValidationStatus),
            entity.ValidationMedianApe,
            entity.ValidationP90Ape);

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
                !Enum.TryParse<ConfidenceLevel>(cell.Confidence, out var confidence) || !Enum.IsDefined(confidence))
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
}
