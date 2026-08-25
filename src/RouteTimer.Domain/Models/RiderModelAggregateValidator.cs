using RouteTimer.Domain.Profile;

namespace RouteTimer.Domain.Models;

public static class RiderModelAggregateValidator
{
    public const string CurrentAlgorithmVersion = "route-model-v2";
    private const double MinimumReleaseCrr = .002;
    private const double MaximumReleaseCrr = .012;
    private const double MinimumReleaseCdA = .15;
    private const double MaximumReleaseCdA = .60;
    private const double ReleaseDrivetrainEfficiency = .97;
    private const double ReleaseAirDensity = 1.225;

    public static void Validate(
        RiderModel? model,
        RiderProfile? profileSnapshot,
        ModelValidationSummary? validation)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profileSnapshot);
        ArgumentNullException.ThrowIfNull(validation);

        if (string.IsNullOrWhiteSpace(model.AlgorithmVersion) ||
            !string.Equals(model.AlgorithmVersion, model.AlgorithmVersion.Trim(), StringComparison.Ordinal))
        {
            throw Invalid("The rider-model algorithm version is invalid.");
        }

        if (!IsPositiveFinite(profileSnapshot.RiderWeightKg) ||
            !IsPositiveFinite(profileSnapshot.BikeAndEquipmentWeightKg))
        {
            throw Invalid("Rider-model profile weights must be positive finite values.");
        }

        ValidateCoefficients(model);
        ValidatePowerModel(model.PowerModel);
        ValidateDescentModel(model.DescentLimits);
        ValidateValidation(validation);
    }

    private static void ValidateCoefficients(RiderModel model)
    {
        var coefficients = model.Coefficients ?? throw Invalid("Rider-model coefficients are required.");
        if (!double.IsFinite(coefficients.DrivetrainEfficiency) ||
            coefficients.DrivetrainEfficiency <= 0 ||
            coefficients.DrivetrainEfficiency > 1 ||
            !IsPositiveFinite(coefficients.AirDensity) ||
            !double.IsFinite(coefficients.Crr) ||
            coefficients.Crr < 0 ||
            !double.IsFinite(coefficients.CdA) ||
            coefficients.CdA < 0)
        {
            throw Invalid("Rider-model coefficients are invalid.");
        }

        var isCurrentAlgorithm = string.Equals(model.AlgorithmVersion, CurrentAlgorithmVersion, StringComparison.Ordinal);
        if ((model.WasCalibrated || isCurrentAlgorithm) &&
            (coefficients.Crr < MinimumReleaseCrr || coefficients.Crr > MaximumReleaseCrr ||
             coefficients.CdA < MinimumReleaseCdA || coefficients.CdA > MaximumReleaseCdA))
        {
            throw Invalid("Rider-model calibrated coefficients are outside release bounds.");
        }

        if (isCurrentAlgorithm &&
            (coefficients.DrivetrainEfficiency != ReleaseDrivetrainEfficiency ||
             coefficients.AirDensity != ReleaseAirDensity))
        {
            throw Invalid("The current rider-model algorithm requires the release efficiency and air density.");
        }
    }

    private static void ValidatePowerModel(PowerModel? powerModel)
    {
        if (powerModel is null || powerModel.Bands is null)
            throw Invalid("The rider power model and band collection are required.");
        if (!double.IsFinite(powerModel.GlobalTypicalWatts) || powerModel.GlobalTypicalWatts < 0)
            throw Invalid("Global rider power must be finite and non-negative.");

        var keys = new HashSet<(string GradeKey, string DurationKey)>();
        foreach (var band in powerModel.Bands)
        {
            if (band is null || band.GradeKey is null || band.DurationKey is null)
                throw Invalid("Power bands and their keys cannot be null.");
            if (!keys.Add((band.GradeKey, band.DurationKey)))
                throw Invalid("Power-band keys must be unique.");
            if (!double.IsFinite(band.TypicalWatts) || band.TypicalWatts < 0 ||
                band.Evidence < TimeSpan.Zero ||
                band.ActivityCount < 0 ||
                !double.IsFinite(band.ShrinkageWeight) ||
                band.ShrinkageWeight < 0 ||
                band.ShrinkageWeight > 1 ||
                !Enum.IsDefined(band.Confidence))
            {
                throw Invalid("Power-band values are invalid.");
            }
        }
    }

    private static void ValidateDescentModel(DescentLimitModel? descentModel)
    {
        if (descentModel?.Cells is null || descentModel.Cells.Count != 9)
            throw Invalid("The descent-limit model must contain nine cells.");
        if (descentModel.Cells.Any(cell => !DescentLimitModel.SatisfiesCellInvariants(cell)) ||
            descentModel.Cells.Select(cell => (cell.GradeKey, cell.CurvatureKey)).Distinct().Count() != descentModel.Cells.Count ||
            descentModel.WasLearned != descentModel.Cells.Any(cell => !cell.IsFallback))
        {
            throw Invalid("The descent-limit model is contradictory.");
        }
    }

    private static void ValidateValidation(ModelValidationSummary validation)
    {
        if (!Enum.IsDefined(validation.Status) ||
            !IsNullableNonNegativeFinite(validation.MedianAbsolutePercentageError) ||
            !IsNullableNonNegativeFinite(validation.P90AbsolutePercentageError))
        {
            throw Invalid("The model-validation summary is invalid.");
        }
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static bool IsNullableNonNegativeFinite(double? value) =>
        value is null || double.IsFinite(value.Value) && value.Value >= 0;

    private static ArgumentException Invalid(string message) => new(message, "model");
}
