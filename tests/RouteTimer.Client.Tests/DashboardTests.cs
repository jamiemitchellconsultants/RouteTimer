using System.Globalization;
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Pages;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Tests;

public sealed class DashboardTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public DashboardTests() => Services.AddSingleton<IRouteTimerApiClient>(api);

    [Fact]
    public void Dashboard_loads_each_section_independently()
    {
        var profileLoad = new TaskCompletionSource<ProfileResponse?>();
        api.OnGetProfileAsync = ct => profileLoad.Task.WaitAsync(ct);
        api.OnGetTrainingActivitiesAsync = _ => Task.FromException<IReadOnlyList<TrainingActivitySummaryResponse>>(
            new ApiProblemException(
                HttpStatusCode.BadRequest,
                "training-unavailable",
                "Training activities could not be loaded.",
                "Try refreshing the dashboard."));
        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
        api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([]);

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Loading rider profile", cut.Find("[data-testid=dashboard-profile-state]").TextContent, StringComparison.Ordinal);
            Assert.Contains("training-unavailable", cut.Find("[data-testid=dashboard-training-error]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Ready", cut.Find("[data-testid=dashboard-model-state]").TextContent, StringComparison.Ordinal);
            Assert.Contains("No saved predictions yet.", cut.Find("[data-testid=dashboard-predictions-empty]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Dashboard_shows_missing_profile_counts_model_summary_and_recent_predictions()
    {
        api.OnGetProfileAsync = _ => Task.FromResult<ProfileResponse?>(null);
        api.OnGetTrainingActivitiesAsync = _ => Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>(
        [
            TrainingSummary("eligible-1.fit", "Eligible"),
            TrainingSummary("eligible-2.fit", "Eligible"),
            TrainingSummary("excluded.fit", "InsufficientPower")
        ]);
        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
        api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>(
        [
            PredictionSummary(index: 0, confidence: "High"),
            PredictionSummary(index: 1, confidence: "Medium"),
            PredictionSummary(index: 2, confidence: null),
            PredictionSummary(index: 3, confidence: "Low"),
            PredictionSummary(index: 4, confidence: "High"),
            PredictionSummary(index: 5, confidence: "Medium")
        ]);

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var profileAction = cut.Find("[data-testid=dashboard-profile-action]");
            Assert.Equal("profile", profileAction.GetAttribute("href"));

            Assert.Contains("2 eligible of 3 rides", cut.Find("[data-testid=dashboard-training-summary]").TextContent, StringComparison.Ordinal);

            var modelText = cut.Find("[data-testid=dashboard-model-state]").TextContent;
            Assert.Contains("Ready", modelText, StringComparison.Ordinal);
            Assert.Contains("Validated", modelText, StringComparison.Ordinal);
            Assert.Contains("8.2%", modelText, StringComparison.Ordinal);
            Assert.Contains("15.6%", modelText, StringComparison.Ordinal);
            Assert.Contains("Building power model", modelText, StringComparison.Ordinal);

            var predictionLinks = cut.FindAll("[data-testid=dashboard-predictions-list] a");
            Assert.Equal(5, predictionLinks.Count);
            Assert.All(predictionLinks, link => Assert.StartsWith("predictions/", link.GetAttribute("href"), StringComparison.Ordinal));

            var predictionText = cut.Find("[data-testid=dashboard-predictions-list]").TextContent;
            Assert.Contains("High confidence", predictionText, StringComparison.Ordinal);
            Assert.DoesNotContain("%", predictionText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Dashboard_shows_not_ready_model_guidance()
    {
        api.OnGetProfileAsync = _ => Task.FromResult<ProfileResponse?>(new ProfileResponse(71.3, 8.4));
        api.OnGetTrainingActivitiesAsync = _ => Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>([]);
        api.OnGetModelStatusAsync = _ => Task.FromResult(new ModelStatusResponse(
            false,
            "Upload at least two eligible rides with power data to build a rider model.",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            0,
            0,
            null));
        api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([]);

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var modelText = cut.Find("[data-testid=dashboard-model-state]").TextContent;
            Assert.Contains("Not ready", modelText, StringComparison.Ordinal);
            Assert.Contains("Upload at least two eligible rides", modelText, StringComparison.Ordinal);
        });
    }

    private static TrainingActivitySummaryResponse TrainingSummary(string fileName, string eligibility) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        fileName,
        DateTimeOffset.Parse("2026-08-25T06:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T07:30:00Z", CultureInfo.InvariantCulture),
        "Garmin",
        "Edge 1040",
        42123.4,
        812.5,
        5021,
        eligibility,
        1,
        1,
        0.98,
        0.85,
        [],
        DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture));

    private static ModelStatusResponse ReadyModelStatus() => new(
        true,
        null,
        Guid.NewGuid(),
        "v1.0.0",
        DateTimeOffset.Parse("2026-08-20T12:00:00Z", CultureInfo.InvariantCulture),
        true,
        true,
        "Validated",
        0.082,
        0.156,
        new PhysicalCoefficientsResponse(0.97, 1.225, 0.0045, 0.31),
        [new PowerBandCoverageResponse("flat", "5m", 255, 2400, 8, 0.15, "High")],
        16,
        2,
        new JobResponse(
            Guid.NewGuid(),
            "BuildModel",
            Guid.NewGuid(),
            "Running",
            70,
            "building-power-model",
            1,
            DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-25T08:01:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-25T08:02:00Z", CultureInfo.InvariantCulture),
            null,
            DateTimeOffset.Parse("2026-08-25T08:03:00Z", CultureInfo.InvariantCulture),
            null,
            null));

    private static PredictionSummaryResponse PredictionSummary(int index, string? confidence) => new(
        Guid.NewGuid(),
        "Succeeded",
        28750 + index,
        420 + index,
        3610 + index,
        7.96,
        245.2,
        confidence,
        ["tailwind-estimated"],
        Guid.NewGuid(),
        "v1.0.0",
        true,
        "Validated",
        0.082,
        0.156,
        71.3,
        8.4,
        "dry-road",
        "calm",
        "temperate",
        true,
        DateTimeOffset.Parse("2026-08-25T09:30:00Z", CultureInfo.InvariantCulture).AddMinutes(-index),
        DateTimeOffset.Parse("2026-08-25T09:45:00Z", CultureInfo.InvariantCulture).AddMinutes(-index));
}
