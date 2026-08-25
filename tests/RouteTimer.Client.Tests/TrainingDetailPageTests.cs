using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Formatting;
using RouteTimer.Client.Pages;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Tests;

public sealed class TrainingDetailPageTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public TrainingDetailPageTests() => Services.AddSingleton<IRouteTimerApiClient>(api);

    [Fact]
    public void TrainingDetail_shows_loading_while_the_activity_is_being_loaded()
    {
        var activityId = Guid.NewGuid();
        var load = new TaskCompletionSource<TrainingActivityDetailResponse?>();
        api.OnGetTrainingActivityAsync = (id, ct) => load.Task.WaitAsync(ct);

        var cut = Render<TrainingDetail>(parameters => parameters.Add(page => page.Id, activityId));

        Assert.Contains("Loading training activity", cut.Find("[data-testid=training-detail-loading]").TextContent, StringComparison.Ordinal);
        Assert.Single(api.RequestedTrainingActivityDetails);
        Assert.Equal(activityId, api.RequestedTrainingActivityDetails[0].ActivityId);
    }

    [Fact]
    public void TrainingDetail_renders_every_summary_field_sorted_reasons_and_sorted_exclusion_counts()
    {
        var activityId = Guid.NewGuid();
        var uploadId = Guid.NewGuid();
        api.OnGetTrainingActivityAsync = (id, _) => Task.FromResult<TrainingActivityDetailResponse?>(
            new TrainingActivityDetailResponse(
                new TrainingActivitySummaryResponse(
                    activityId,
                    uploadId,
                    "morning.fit",
                    DateTimeOffset.Parse("2026-08-25T06:15:00Z", CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("2026-08-25T07:45:00Z", CultureInfo.InvariantCulture),
                    "Garmin",
                    "Edge 1040",
                    54321,
                    987,
                    5460,
                    "InsufficientPower",
                    0.93,
                    0.94,
                    0.95,
                    0.56,
                    ["position-gap", "low-power-coverage"],
                    DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture)),
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["zeta-gap"] = 1,
                    ["alpha-gap"] = 3
                }));

        var cut = Render<TrainingDetail>(parameters => parameters.Add(page => page.Id, activityId));

        cut.WaitForAssertion(() =>
        {
            var details = cut.Find("[data-testid=training-detail-summary]").TextContent;
            Assert.Contains(activityId.ToString(), details, StringComparison.Ordinal);
            Assert.Contains(uploadId.ToString(), details, StringComparison.Ordinal);
            Assert.Contains("morning.fit", details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Timestamp(DateTimeOffset.Parse("2026-08-25T06:15:00Z", CultureInfo.InvariantCulture)), details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Timestamp(DateTimeOffset.Parse("2026-08-25T07:45:00Z", CultureInfo.InvariantCulture)), details, StringComparison.Ordinal);
            Assert.Contains("Garmin", details, StringComparison.Ordinal);
            Assert.Contains("Edge 1040", details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Distance(54321), details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Ascent(987), details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Duration(5460), details, StringComparison.Ordinal);
            Assert.Contains("Insufficient power", details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Percentage(0.93), details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Percentage(0.94), details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Percentage(0.95), details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Percentage(0.56), details, StringComparison.Ordinal);
            Assert.Contains(RouteTimerFormat.Timestamp(DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture)), details, StringComparison.Ordinal);

            var reasons = cut.FindAll("[data-testid=training-detail-reason]");
            Assert.Equal(["Low power coverage", "Position gap"], reasons.Select(item => item.TextContent.Trim()));

            var exclusions = cut.FindAll("[data-testid=training-detail-exclusion]");
            Assert.Equal(["Alpha gap 3", "Zeta gap 1"], exclusions.Select(item => item.TextContent.Trim()));

            Assert.DoesNotContain("<table", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sample", cut.Markup, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TrainingDetail_shows_unavailable_for_optional_metadata()
    {
        var activityId = Guid.NewGuid();
        api.OnGetTrainingActivityAsync = (id, _) => Task.FromResult<TrainingActivityDetailResponse?>(
            new TrainingActivityDetailResponse(
                new TrainingActivitySummaryResponse(
                    activityId,
                    Guid.NewGuid(),
                    "metadata-missing.fit",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    900,
                    "Eligible",
                    1,
                    1,
                    1,
                    1,
                    [],
                    DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture)),
                new Dictionary<string, int>()));

        var cut = Render<TrainingDetail>(parameters => parameters.Add(page => page.Id, activityId));

        cut.WaitForAssertion(() =>
        {
            var details = cut.Find("[data-testid=training-detail-summary]").TextContent;
            Assert.True(details.Contains("Unavailable", StringComparison.Ordinal));
            Assert.True(cut.Markup.Split("Unavailable", StringSplitOptions.None).Length >= 6);
        });
    }

    [Fact]
    public void TrainingDetail_shows_not_found_guidance_for_missing_activities()
    {
        var activityId = Guid.NewGuid();
        api.OnGetTrainingActivityAsync = (id, _) => Task.FromResult<TrainingActivityDetailResponse?>(null);

        var cut = Render<TrainingDetail>(parameters => parameters.Add(page => page.Id, activityId));

        cut.WaitForAssertion(() =>
        {
            var notFound = cut.Find("[data-testid=training-detail-not-found]");
            Assert.Contains("was not found", notFound.TextContent, StringComparison.Ordinal);
            Assert.Contains("training", notFound.QuerySelector("a")!.GetAttribute("href"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TrainingDetail_shows_problem_state_when_loading_fails()
    {
        var activityId = Guid.NewGuid();
        api.OnGetTrainingActivityAsync = (id, _) => Task.FromException<TrainingActivityDetailResponse?>(
            new ApiProblemException(
                System.Net.HttpStatusCode.BadRequest,
                "training-detail-unavailable",
                "Training detail could not be loaded.",
                "Try refreshing the activity detail."));

        var cut = Render<TrainingDetail>(parameters => parameters.Add(page => page.Id, activityId));

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid=training-detail-error]");
            Assert.Contains("training-detail-unavailable", alert.TextContent, StringComparison.Ordinal);
        });
    }
}
