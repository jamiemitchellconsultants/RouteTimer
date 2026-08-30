using System.Text.Json;
using RouteTimer.Api.Adjustments;
using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Contracts.Errors;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Endpoints;

public static class PredictionAdjustmentEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapPredictionAdjustmentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/pacing-strategies", GetCapabilitiesAsync);
        routes.MapPost("/api/predictions/{predictionId:guid}/adjustments", CreateAdjustmentAsync);
        routes.MapGet("/api/predictions/{predictionId:guid}/adjustments", GetAdjustmentsAsync);
        routes.MapGet("/api/predictions/{predictionId:guid}/adjustments/{adjustmentId:guid}", GetAdjustmentAsync);
        routes.MapDelete("/api/predictions/{predictionId:guid}/adjustments/{adjustmentId:guid}", DeleteAdjustmentAsync);
        return routes;
    }

    private static IResult GetCapabilitiesAsync(PacingStrategyOptions options) =>
        TypedResults.Ok(new PacingStrategyCapabilityResponse(
            options.Enabled, options.SegmentSpecificGains, options.NpIfTarget, options.TimeTarget,
            options.RpeZoneShift, options.VariableMatchBurning, options.MaximumDefinitionBytes,
            options.MaximumRules, options.MaximumPhases));

    private static async Task<IResult> CreateAdjustmentAsync(
        Guid predictionId,
        HttpRequest request,
        PacingStrategyOptions options,
        PredictionAdjustmentService adjustments,
        CancellationToken cancellationToken)
    {
        PacingStrategyRequest strategyRequest;
        try
        {
            strategyRequest = await JsonSerializer.DeserializeAsync<PacingStrategyRequest>(request.Body, RequestJsonOptions, cancellationToken)
                ?? throw new JsonException("The strategy request body was empty.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // System.Text.Json throws NotSupportedException (not JsonException) specifically when a
            // polymorphic root's discriminator is missing or unrecognized - both are a malformed request.
            return ApiProblems.BadRequest(ErrorCodes.PacingStrategyInvalid, "The pacing strategy request is malformed or names an unknown strategy type.");
        }

        var type = ResolveType(strategyRequest);
        if (!options.IsEnabled(type))
        {
            return ApiProblems.Conflict(ErrorCodes.PacingStrategyDisabled, $"The {type} pacing strategy is disabled.");
        }

        try
        {
            var definition = MapDefinition(strategyRequest);
            var created = await adjustments.CreateAsync(predictionId, definition, cancellationToken);
            return TypedResults.Accepted(
                $"/api/predictions/{predictionId}/adjustments/{created.AdjustmentId}",
                new PredictionAdjustmentSubmissionResponse(created.AdjustmentId, created.JobId, predictionId));
        }
        catch (PredictionAdjustmentException exception)
        {
            return exception.Code switch
            {
                ErrorCodes.PredictionNotFound => ApiProblems.NotFound(exception.Code, exception.Message),
                ErrorCodes.AdjustmentBaselineNotReady or ErrorCodes.PacingStrategyDisabled => ApiProblems.Conflict(exception.Code, exception.Message),
                _ => ApiProblems.BadRequest(exception.Code, exception.Message),
            };
        }
    }

    private static async Task<IResult> GetAdjustmentsAsync(
        Guid predictionId,
        PredictionAdjustmentQueryService adjustments,
        CancellationToken cancellationToken)
    {
        var summaries = await adjustments.GetSummariesAsync(predictionId, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<PredictionAdjustmentSummaryResponse>>(summaries.Select(ToSummary).ToList());
    }

    private static async Task<IResult> GetAdjustmentAsync(
        Guid predictionId,
        Guid adjustmentId,
        PredictionAdjustmentQueryService adjustments,
        CancellationToken cancellationToken) =>
        (await adjustments.GetAsync(predictionId, adjustmentId, cancellationToken)) is { } detail
            ? TypedResults.Ok(ToDetail(detail))
            : ApiProblems.NotFound(ErrorCodes.AdjustmentNotFound, "The adjustment was not found.");

    private static async Task<IResult> DeleteAdjustmentAsync(
        Guid predictionId,
        Guid adjustmentId,
        PredictionAdjustmentDeletionService deletions,
        CancellationToken cancellationToken) =>
        await deletions.DeleteAsync(predictionId, adjustmentId, cancellationToken)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(ErrorCodes.AdjustmentNotFound, "The adjustment was not found.");

    /// <summary>
    /// Every strategy discriminator is handled here because the type is always knowable from the
    /// request alone; below in <see cref="MapDefinition"/>, only strategies with a delivered domain
    /// type can actually be constructed; the rest are unreachable while their strategy stays disabled
    /// (see <see cref="PacingStrategyOptions"/>, which defaults every strategy to disabled) and are
    /// completed in that strategy's own delivery task.
    /// </summary>
    private static PacingStrategyType ResolveType(PacingStrategyRequest request) => request switch
    {
        SegmentSpecificGainsRequest => PacingStrategyType.SegmentSpecificGains,
        NpIfTargetRequest => PacingStrategyType.NpIfTarget,
        TimeTargetRequest => PacingStrategyType.TimeTarget,
        RpeZoneShiftRequest => PacingStrategyType.RpeZoneShift,
        VariableMatchBurningRequest => PacingStrategyType.VariableMatchBurning,
        _ => throw new InvalidOperationException($"Unhandled pacing strategy request type {request.GetType()}."),
    };

    private static PacingStrategyDefinition MapDefinition(PacingStrategyRequest request) => request switch
    {
        SegmentSpecificGainsRequest => throw new NotImplementedException("Segment-specific gains mapping is delivered in its own task."),
        NpIfTargetRequest => throw new NotImplementedException("NP/IF target mapping is delivered in its own task."),
        TimeTargetRequest => throw new NotImplementedException("Time target mapping is delivered in its own task."),
        RpeZoneShiftRequest => throw new NotImplementedException("RPE/zone shift mapping is delivered in its own task."),
        VariableMatchBurningRequest => throw new NotImplementedException("Variable match-burning mapping is delivered in its own task."),
        _ => throw new InvalidOperationException($"Unhandled pacing strategy request type {request.GetType()}."),
    };

    private static PredictionAdjustmentSummaryResponse ToSummary(PredictionAdjustmentSummary summary) => new(
        summary.Id, summary.PredictionId, summary.StrategyType.ToString(), summary.State.ToString(),
        summary.MovingTime?.TotalSeconds, summary.AverageSpeedMetresPerSecond, summary.AveragePowerWatts,
        summary.Confidence?.ToString(), summary.Warnings, summary.StrategyAlgorithmVersion, summary.CreatedAt, summary.CompletedAt);

    private static PredictionAdjustmentDetailResponse ToDetail(PredictionAdjustmentDetail detail) => new(
        new PredictionAdjustmentSummaryResponse(
            detail.Id, detail.PredictionId, detail.StrategyType.ToString(), detail.State.ToString(),
            detail.MovingTime?.TotalSeconds, detail.AverageSpeedMetresPerSecond, detail.AveragePowerWatts,
            detail.Confidence?.ToString(), detail.Warnings, detail.StrategyAlgorithmVersion, detail.CreatedAt, detail.CompletedAt),
        ParseElement(detail.StrategyJson),
        detail.ResultJson is { } reportJson ? ParseElement(reportJson) : null,
        detail.Segments.Select(ToSegment).ToList());

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static PredictionAdjustmentSegmentResponse ToSegment(PersistedAdjustmentSegment segment) => new(
        segment.Sequence, segment.PowerWatts, segment.SpeedMetresPerSecond, segment.SegmentMovingTime.TotalSeconds,
        segment.CumulativeMovingTime.TotalSeconds, segment.Confidence.ToString(), segment.ZoneNumber, segment.StrategyPhase, segment.WPrimeBalanceJoules);
}
