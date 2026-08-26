namespace RouteTimer.Contracts.Predictions;

public sealed record PredictionSubmissionResponse(Guid PredictionId, Guid JobId, Guid ModelId);

public sealed record PredictionSummaryResponse(
    Guid Id,
    string State,
    double? DistanceMetres,
    double? AscentMetres,
    double? MovingSeconds,
    double? AverageSpeedMetresPerSecond,
    double? AveragePowerWatts,
    string? Confidence,
    IReadOnlyList<string> Warnings,
    Guid ModelId,
    string ModelVersion,
    bool ModelWasCalibrated,
    string ValidationStatus,
    double? ValidationMedianAbsolutePercentageError,
    double? ValidationP90AbsolutePercentageError,
    double RiderWeightKg,
    double BikeWeightKg,
    string SurfaceAssumption,
    string WindAssumption,
    string WeatherAssumption,
    bool MovingOnlyAssumption,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    long? GarminCourseId = null,
    DateTimeOffset? GarminCourseUploadedAt = null);

public sealed record PredictionSegmentResponse(
    int Sequence,
    double Latitude,
    double Longitude,
    double ElevationMetres,
    double CumulativeDistanceMetres,
    double SegmentDistanceMetres,
    double Gradient,
    double CurvaturePerMetre,
    double PredictedPowerWatts,
    double PredictedSpeedMetresPerSecond,
    double SegmentMovingSeconds,
    double CumulativeMovingSeconds,
    string Confidence);

public sealed record PredictionDetailResponse(PredictionSummaryResponse Summary, IReadOnlyList<PredictionSegmentResponse> Segments);

public sealed record CreateGarminCourseRequest(string? Name, string? ActivityType);

public sealed record GarminCourseResponse(long CourseId, string CourseName, string CourseUrl);
