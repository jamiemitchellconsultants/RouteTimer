using System.Globalization;
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components;
using RouteTimer.Client.Jobs;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Garmin;
using RouteTimer.Contracts.Jobs;

namespace RouteTimer.Client.Tests.Components;

public sealed class GarminActivityPickerTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();
    private readonly FakeTimeProvider time = new();

    public GarminActivityPickerTests()
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton<TimeProvider>(time);
        Services.AddScoped<JobPoller>();
    }

    [Fact]
    public void Picker_shows_loading_then_an_empty_state()
    {
        var completion = new TaskCompletionSource<GarminActivityPageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnGetGarminActivitiesAsync = (_, _) => completion.Task;

        var cut = Render<GarminActivityPicker>();

        Assert.Contains("Loading Garmin activities", cut.Find("[data-testid=garmin-activities-loading]").TextContent, StringComparison.Ordinal);

        completion.SetResult(Page([], null));

        cut.WaitForAssertion(() =>
            Assert.Contains("No road or gravel activities", cut.Find("[data-testid=garmin-activities-empty]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Picker_shows_a_safe_load_failure_and_retries_the_first_page()
    {
        var requests = 0;
        api.OnGetGarminActivitiesAsync = (_, _) =>
        {
            requests++;
            return requests == 1
                ? Task.FromException<GarminActivityPageResponse>(new ApiProblemException(
                    HttpStatusCode.ServiceUnavailable,
                    "garmin-unavailable",
                    "Garmin is unavailable.",
                    "Try again shortly."))
                : Task.FromResult(Page([Activity("retry-1", "Retry ride")], null));
        };

        var cut = Render<GarminActivityPicker>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("garmin-unavailable", cut.Find("[data-testid=garmin-activities-error]").TextContent, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("[data-testid=garmin-activities-retry]"));
        });

        cut.Find("[data-testid=garmin-activities-retry]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, requests);
            Assert.Contains("Retry ride", cut.Find("[data-testid=garmin-activity-row]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Picker_orders_newest_first_and_formats_only_available_metrics_with_canonical_labels()
    {
        using var culture = new CultureScope("en-GB");
        api.OnGetGarminActivitiesAsync = (_, _) => Task.FromResult(Page(
        [
            Activity(
                "older",
                "Older road ride",
                startedAt: DateTimeOffset.Parse("2026-08-24T08:00:00Z", CultureInfo.InvariantCulture),
                activityType: "road-cycling",
                distanceMetres: null,
                durationSeconds: null,
                ascentMetres: null,
                averagePowerWatts: null),
            Activity(
                "newer",
                "New gravel ride",
                startedAt: DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture),
                activityType: "gravel-cycling",
                distanceMetres: 54321,
                durationSeconds: 5460,
                ascentMetres: 987,
                averagePowerWatts: 251)
        ], null));

        var cut = Render<GarminActivityPicker>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=garmin-activity-row]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("New gravel ride", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Gravel cycling", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("54.3 km", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("91 min", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("987 m", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("251 W", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Older road ride", rows[1].TextContent, StringComparison.Ordinal);
            Assert.Contains("Road cycling", rows[1].TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Distance:", rows[1].TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Duration:", rows[1].TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Ascent:", rows[1].TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Average power:", rows[1].TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Picker_disables_only_imported_rows_enforces_ten_selections_and_appends_load_more()
    {
        var pages = new Queue<GarminActivityPageResponse>();
        pages.Enqueue(Page(
        [
            Activity("imported", "Imported ride", alreadyImported: true),
            .. Enumerable.Range(1, 11).Select(index => Activity($"activity-{index}", $"Ride {index}"))
        ], "NTA"));
        pages.Enqueue(Page([Activity("activity-12", "Ride 12")], null));
        api.OnGetGarminActivitiesAsync = (_, _) => Task.FromResult(pages.Dequeue());

        var cut = Render<GarminActivityPicker>();

        cut.WaitForAssertion(() => Assert.Equal(12, cut.FindAll("[data-testid=garmin-activity-row]").Count));
        var imported = cut.Find("[data-activity-id=imported] [data-testid=garmin-activity-select]");
        Assert.True(imported.HasAttribute("disabled"));
        Assert.Contains("Already imported", cut.Find("[data-activity-id=imported]").TextContent, StringComparison.Ordinal);

        foreach (var index in Enumerable.Range(1, 10))
        {
            cut.Find($"[data-activity-id=activity-{index}] [data-testid=garmin-activity-select]").Change(true);
        }

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("10 of 10 selected", cut.Find("[data-testid=garmin-selected-count]").TextContent, StringComparison.Ordinal);
            Assert.True(cut.Find("[data-activity-id=activity-11] [data-testid=garmin-activity-select]").HasAttribute("disabled"));
        });

        cut.Find("[data-testid=garmin-load-more]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(13, cut.FindAll("[data-testid=garmin-activity-row]").Count);
            Assert.Equal("NTA", api.RequestedGarminActivities[1].Cursor);
            Assert.True(cut.Find("[data-activity-id=activity-12] [data-testid=garmin-activity-select]").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("[data-testid=garmin-load-more]"));
        });
    }

    [Fact]
    public void Picker_sends_selection_order_once_and_renders_every_import_outcome()
    {
        var acceptedJobId = Guid.NewGuid();
        var activities = Enumerable.Range(1, 5)
            .Select(index => Activity($"activity-{index}", $"Ride {index}", startedAt: DateTimeOffset.Parse($"2026-08-{20 + index:00}T08:00:00Z", CultureInfo.InvariantCulture)))
            .ToArray();
        var importCompletion = new TaskCompletionSource<GarminImportBatchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnGetGarminActivitiesAsync = (_, _) => Task.FromResult(Page(activities, null));
        api.OnImportGarminActivitiesAsync = (_, _) => importCompletion.Task;
        api.Jobs.Enqueue(Job(acceptedJobId, "Succeeded", 100, "completed"));
        var cut = Render<GarminActivityPicker>();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid=garmin-activity-row]").Count));
        foreach (var id in new[] { "activity-3", "activity-1", "activity-5", "activity-2", "activity-4" })
        {
            cut.Find($"[data-activity-id={id}] [data-testid=garmin-activity-select]").Change(true);
        }

        cut.Find("[data-testid=garmin-import-selected]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.GarminImportRequests);
            Assert.Equal(["activity-3", "activity-1", "activity-5", "activity-2", "activity-4"], api.GarminImportRequests[0].Request.ActivityIds);
            Assert.True(cut.Find("[data-testid=garmin-import-selected]").HasAttribute("disabled"));
        });

        cut.Find("[data-testid=garmin-import-selected]").Click();
        Assert.Single(api.GarminImportRequests);

        importCompletion.SetResult(new GarminImportBatchResponse(
        [
            Result("activity-3", "Ride 3", "accepted", acceptedJobId),
            Result("activity-1", "Ride 1", "already-imported"),
            Result("activity-5", "Ride 5", "duplicate"),
            Result("activity-2", "Ride 2", "invalid-fit", errorCode: "invalid-fit-upload"),
            Result("activity-4", "Ride 4", "download-failed", errorCode: "garmin-download-failed")
        ]));

        cut.WaitForAssertion(() =>
        {
            var outcomes = cut.Find("[data-testid=garmin-import-results]").TextContent;
            Assert.Contains("Download accepted", outcomes, StringComparison.Ordinal);
            Assert.Contains("Already imported", outcomes, StringComparison.Ordinal);
            Assert.Contains("Duplicate FIT", outcomes, StringComparison.Ordinal);
            Assert.Contains("Invalid FIT", outcomes, StringComparison.Ordinal);
            Assert.Contains("Garmin download failed", outcomes, StringComparison.Ordinal);
            Assert.Contains("invalid-fit-upload", outcomes, StringComparison.Ordinal);
            Assert.Contains("garmin-download-failed", outcomes, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Picker_polls_accepted_jobs_and_reports_later_parse_failure()
    {
        var jobId = Guid.NewGuid();
        var completed = 0;
        api.OnGetGarminActivitiesAsync = (_, _) => Task.FromResult(Page([Activity("accepted", "Accepted ride")], null));
        api.OnImportGarminActivitiesAsync = (_, _) => Task.FromResult(new GarminImportBatchResponse(
        [
            Result("accepted", "Accepted ride", "accepted", jobId)
        ]));
        api.Jobs.Enqueue(Job(jobId, "Running", 25, "decoding-fit"));
        api.Jobs.Enqueue(Job(jobId, "Failed", 25, "decoding-fit", "fit-parse-failed"));
        var cut = Render<GarminActivityPicker>(parameters => parameters
            .Add(component => component.AcceptedImportsCompleted, () => completed++));

        cut.WaitForElement("[data-activity-id=accepted] [data-testid=garmin-activity-select]").Change(true);
        cut.Find("[data-testid=garmin-import-selected]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.RequestedJobs);
            Assert.Contains("Decoding FIT", cut.Find("[data-testid=garmin-import-results]").TextContent, StringComparison.Ordinal);
        });

        time.Advance(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.RequestedJobs.Count);
            Assert.Contains("Failed", cut.Find("[data-testid=garmin-import-results]").TextContent, StringComparison.Ordinal);
            Assert.Contains("fit-parse-failed", cut.Find("[data-testid=garmin-import-results]").TextContent, StringComparison.Ordinal);
            Assert.Equal(1, completed);
        });
    }

    [Fact]
    public async Task Picker_cancels_an_in_flight_load_when_disposed()
    {
        CancellationToken observed = default;
        var completion = new TaskCompletionSource<GarminActivityPageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnGetGarminActivitiesAsync = (_, ct) =>
        {
            observed = ct;
            ct.Register(() => completion.TrySetCanceled(ct));
            return completion.Task;
        };
        var cut = Render<GarminActivityPicker>();
        cut.WaitForAssertion(() => Assert.Single(api.RequestedGarminActivities));

        ((IDisposable)cut.Instance).Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(observed.IsCancellationRequested);
    }

    private static GarminActivityPageResponse Page(
        IReadOnlyList<GarminActivitySummaryResponse> activities,
        string? nextCursor) => new(activities, nextCursor);

    private static GarminActivitySummaryResponse Activity(
        string id,
        string name,
        DateTimeOffset? startedAt = null,
        string activityType = "road-cycling",
        double? distanceMetres = 42123,
        double? durationSeconds = 5021,
        double? ascentMetres = 812,
        double? averagePowerWatts = 243,
        bool alreadyImported = false) => new(
        id,
        name,
        startedAt ?? DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture),
        activityType,
        distanceMetres,
        durationSeconds,
        ascentMetres,
        averagePowerWatts,
        alreadyImported);

    private static GarminImportResultResponse Result(
        string activityId,
        string name,
        string outcome,
        Guid? jobId = null,
        string? errorCode = null) => new(
        activityId,
        name,
        outcome,
        outcome is "accepted" or "already-imported" or "duplicate" ? Guid.NewGuid() : null,
        jobId,
        errorCode);

    private static JobResponse Job(
        Guid jobId,
        string state,
        int progressPercent,
        string stage,
        string? errorCode = null) => new(
        jobId,
        "ParseTraining",
        Guid.NewGuid(),
        state,
        progressPercent,
        stage,
        1,
        DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T10:01:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T10:02:00Z", CultureInfo.InvariantCulture),
        state.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.Parse("2026-08-25T10:03:00Z", CultureInfo.InvariantCulture)
            : null,
        state.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.Parse("2026-08-25T10:03:00Z", CultureInfo.InvariantCulture)
            : null,
        errorCode,
        errorCode is null ? null : "The FIT activity could not be parsed.");

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string cultureName)
        {
            var culture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
