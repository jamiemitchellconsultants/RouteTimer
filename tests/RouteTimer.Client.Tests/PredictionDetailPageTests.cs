using System.Globalization;
using Bunit;
using Bunit.JSInterop;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Pages;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Contracts.Predictions;

namespace RouteTimer.Client.Tests;

public sealed class PredictionDetailPageTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public PredictionDetailPageTests()
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MapTiles:Url"] = "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
                ["MapTiles:Attribution"] = "&copy; OpenStreetMap contributors"
            })
            .Build());
        Services.AddSingleton(new BrowserInterop(JSInterop.JSRuntime));
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        var qr = JSInterop.SetupModule("./js/pace-tracker-qr.mjs");
        qr.SetupVoid("render", _ => true).SetVoidResult();
        qr.SetupVoid("clear", _ => true).SetVoidResult();

        var module = JSInterop.SetupModule("./js/route-visualization.js");
        module.SetupVoid("initializeMap", _ => true).SetVoidResult();
        module.SetupVoid("initializeProfiles", _ => true).SetVoidResult();
        module.SetupVoid("selectMapSequence", _ => true).SetVoidResult();
        module.SetupVoid("selectProfileSequence", _ => true).SetVoidResult();
        module.SetupVoid("disposeMap", _ => true).SetVoidResult();
        module.SetupVoid("disposeProfiles", _ => true).SetVoidResult();

        // Every succeeded-prediction test triggers a pacing-adjustment load; default to "nothing
        // enabled, no children yet" so tests unrelated to adjustments don't each need to configure it.
        api.OnGetPacingStrategiesAsync = (_) => Task.FromResult(new PacingStrategyCapabilityResponse(false, false, false, false, false, false, 65536, 10, 10));
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>([]);
    }

    [Fact]
    public void PredictionDetail_shows_loading_while_the_prediction_is_being_loaded()
    {
        var predictionId = Guid.NewGuid();
        var load = new TaskCompletionSource<PredictionDetailResponse?>();
        api.OnGetPredictionAsync = (id, ct) => load.Task.WaitAsync(ct);

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        Assert.Contains("Loading prediction", cut.Find("[data-testid=prediction-detail-loading]").TextContent, StringComparison.Ordinal);
        Assert.Single(api.RequestedPredictionDetails);
        Assert.Equal(predictionId, api.RequestedPredictionDetails[0].PredictionId);
    }

    [Fact]
    public void PredictionDetail_renders_every_snapshot_field_and_sorts_segments_for_visualization()
    {
        var predictionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        api.OnGetPredictionAsync = (id, _) => Task.FromResult<PredictionDetailResponse?>(
            new PredictionDetailResponse(
                new PredictionSummaryResponse(
                    predictionId,
                    "Succeeded",
                    54321,
                    987,
                    5460,
                    8.56,
                    248,
                    "Medium",
                    ["temperature-estimated", "tailwind-estimated"],
                    modelId,
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
                    DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("2026-08-25T10:01:00Z", CultureInfo.InvariantCulture)),
                [
                    new PredictionSegmentResponse(2, 51.51, -0.12, 132, 1000, 500, 0.03, 0.001, 250, 8.9, 62, 122, "High"),
                    new PredictionSegmentResponse(1, 51.5, -0.11, 126, 500, 500, 0.02, 0.001, 246, 8.2, 60, 60, "Medium")
                ]));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForAssertion(() =>
        {
            var summary = cut.Find("[data-testid=prediction-detail-summary]").TextContent;
            Assert.Contains(predictionId.ToString(), summary, StringComparison.Ordinal);
            Assert.Contains(modelId.ToString(), summary, StringComparison.Ordinal);
            Assert.Contains("Succeeded", summary, StringComparison.Ordinal);
            Assert.Contains("54.3 km", summary, StringComparison.Ordinal);
            Assert.Contains("987 m", summary, StringComparison.Ordinal);
            Assert.Contains("1:31:00", summary, StringComparison.Ordinal);
            Assert.Contains("30.8 km/h", summary, StringComparison.Ordinal);
            Assert.Contains("248 W", summary, StringComparison.Ordinal);
            Assert.Contains("Validated", summary, StringComparison.Ordinal);
            Assert.Contains("8.2%", summary, StringComparison.Ordinal);
            Assert.Contains("15.6%", summary, StringComparison.Ordinal);
            Assert.Contains("71.3 kg", summary, StringComparison.Ordinal);
            Assert.Contains("8.4 kg", summary, StringComparison.Ordinal);
            Assert.Contains("Dry road", summary, StringComparison.Ordinal);
            Assert.Contains("Calm", summary, StringComparison.Ordinal);
            Assert.Contains("Temperate", summary, StringComparison.Ordinal);
            Assert.Contains("Yes", summary, StringComparison.Ordinal);

            var warnings = cut.Find("[data-testid=prediction-detail-warnings]").TextContent;
            Assert.Contains("Temperature estimated", warnings, StringComparison.Ordinal);
            Assert.Contains("Tailwind estimated", warnings, StringComparison.Ordinal);

            Assert.Contains("Medium confidence", cut.Markup, StringComparison.Ordinal);

            var visualization = cut.Find("[data-testid=prediction-detail-visualization]");
            Assert.Contains("Selected segment 1", visualization.TextContent, StringComparison.Ordinal);
            Assert.Contains("0.5 km", visualization.TextContent, StringComparison.Ordinal);
            Assert.Contains("29.5 km/h", visualization.TextContent, StringComparison.Ordinal);
            Assert.Contains("sorted for visualization", cut.Find("[data-testid=prediction-detail-order-warning]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Offers_both_gpx_variants_for_a_completed_prediction()
    {
        var predictionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        api.OnGetPredictionAsync = (id, _) => Task.FromResult<PredictionDetailResponse?>(
            new PredictionDetailResponse(
                new PredictionSummaryResponse(
                    predictionId, "Succeeded", 54321, 987, 5460, 8.56, 248, "Medium",
                    ["temperature-estimated"], modelId, "v1.0.0", true, "Validated", 0.082, 0.156,
                    71.3, 8.4, "dry-road", "calm", "temperate", true,
                    DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("2026-08-25T10:01:00Z", CultureInfo.InvariantCulture)),
                [
                    new PredictionSegmentResponse(1, 51.5, -0.11, 126, 500, 500, 0.02, 0.001, 246, 8.2, 60, 60, "Medium"),
                    new PredictionSegmentResponse(2, 51.51, -0.12, 132, 1000, 500, 0.03, 0.001, 250, 8.9, 62, 122, "High")
                ]));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                $"/api/predictions/{predictionId}/gpx",
                cut.Find("[data-testid=prediction-download-gpx]").GetAttribute("href"));
            Assert.Equal(
                $"/api/predictions/{predictionId}/gpx?timed=true",
                cut.Find("[data-testid=prediction-download-gpx-timed]").GetAttribute("href"));
        });
    }

    [Fact]
    public void PredictionDetail_keeps_visualization_hidden_for_non_terminal_or_segment_free_results()
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (id, _) => Task.FromResult<PredictionDetailResponse?>(
            new PredictionDetailResponse(
                new PredictionSummaryResponse(
                    predictionId,
                    "Failed",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
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
                    DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture),
                    null),
                []));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=prediction-detail-visualization]"));
            Assert.Contains("Visualization becomes available after the prediction succeeds with stored route segments.", cut.Find("[data-testid=prediction-detail-visualization-pending]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PredictionDetail_shows_not_found_guidance_for_missing_predictions()
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (id, _) => Task.FromResult<PredictionDetailResponse?>(null);

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForAssertion(() =>
        {
            var notFound = cut.Find("[data-testid=prediction-detail-not-found]");
            Assert.Contains("was not found", notFound.TextContent, StringComparison.Ordinal);
            Assert.Contains("predictions", notFound.QuerySelector("a")!.GetAttribute("href"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PredictionDetail_shows_problem_state_when_loading_fails()
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (id, _) => Task.FromException<PredictionDetailResponse?>(
            new ApiProblemException(
                System.Net.HttpStatusCode.BadRequest,
                "prediction-detail-unavailable",
                "Prediction detail could not be loaded.",
                "Try refreshing the prediction detail."));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid=prediction-detail-error]");
            Assert.Contains("prediction-detail-unavailable", alert.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Offers_to_send_a_completed_prediction_to_garmin()
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(CompletedDetail(predictionId));
        api.OnCreateGarminCourseAsync = (_, _, _) => Task.FromResult(
            new GarminCourseResponse(4242, "Kingston to Dorking", "https://connect.garmin.com/modern/course/4242"));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForElement("[data-testid=prediction-send-to-garmin]").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "https://connect.garmin.com/modern/course/4242",
            cut.Find("[data-testid=prediction-garmin-course-link]").GetAttribute("href")));
    }

    [Fact]
    public void Links_to_an_already_pushed_course_instead_of_offering_to_push_again()
    {
        var predictionId = Guid.NewGuid();
        var detail = CompletedDetail(predictionId);
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(
            detail with
            {
                Summary = detail.Summary with
                {
                    GarminCourseId = 4242,
                    GarminCourseUploadedAt = DateTimeOffset.UnixEpoch
                }
            });

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=prediction-garmin-course-link]");
            Assert.Empty(cut.FindAll("[data-testid=prediction-send-to-garmin]"));
            cut.Find("[data-testid=prediction-send-to-garmin-again]");
        });
    }

    [Fact]
    public void Confirms_before_pushing_a_second_time()
    {
        var predictionId = Guid.NewGuid();
        var pushes = 0;
        var detail = CompletedDetail(predictionId);
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(
            detail with
            {
                Summary = detail.Summary with { GarminCourseId = 4242 }
            });
        api.OnCreateGarminCourseAsync = (_, _, _) =>
        {
            pushes++;
            return Task.FromResult(new GarminCourseResponse(4243, "R", "https://connect.garmin.com/modern/course/4243"));
        };

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.WaitForElement("[data-testid=prediction-send-to-garmin-again]").Click();
        Assert.Equal(0, pushes);

        cut.Find("[data-testid=prediction-send-to-garmin-confirm]").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, pushes));
    }

    private static PredictionDetailResponse CompletedDetail(Guid predictionId)
    {
        var modelId = Guid.NewGuid();
        return new PredictionDetailResponse(
            new PredictionSummaryResponse(
                predictionId, "Succeeded", 54321, 987, 5460, 8.56, 248, "Medium",
                ["temperature-estimated"], modelId, "v1.0.0", true, "Validated", 0.082, 0.156,
                71.3, 8.4, "dry-road", "calm", "temperate", true,
                DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-08-25T10:01:00Z", CultureInfo.InvariantCulture)),
            [
                new PredictionSegmentResponse(1, 51.5, -0.11, 126, 500, 500, 0.02, 0.001, 246, 8.2, 60, 60, "Medium"),
                new PredictionSegmentResponse(2, 51.51, -0.12, 132, 1000, 500, 0.03, 0.001, 250, 8.9, 62, 122, "High")
            ]);
    }
}
