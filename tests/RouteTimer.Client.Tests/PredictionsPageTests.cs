using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Client.Api;
using RouteTimer.Client.Jobs;
using RouteTimer.Client.Logging;
using RouteTimer.Client.Pages;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Settings;

namespace RouteTimer.Client.Tests;

public sealed class PredictionsPageTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();
    private readonly FakeTimeProvider time = new();

    public PredictionsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton<TimeProvider>(time);
        Services.AddScoped<JobPoller>();

        var log = new ActionLog();
        Services.AddSingleton(log);
        // See GoogleMapsRouteInputTests: pre-built instances so bUnit's synchronous teardown
        // never has to await disposal of these IAsyncDisposable-only services.
        Services.AddSingleton(new DirectionsInterop(JSInterop.JSRuntime, log));
        Services.AddSingleton(new BrowserInterop(JSInterop.JSRuntime));
        Services.AddScoped<ShortLinkClient>();
    }

    [Fact]
    public void Switching_input_modes_does_not_discard_the_other_mode_state()
    {
        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
        api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([]);
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(true, "AIza…6789", true));

        var cut = Render<Predictions>();

        cut.WaitForElement("[data-testid=predictions-mode-maps]").Click();
        cut.WaitForElement("[data-testid=maps-url]");

        cut.Find("[data-testid=predictions-mode-upload]").Click();

        cut.WaitForAssertion(() => Assert.Equal(".gpx", cut.Find("input[type=file]").GetAttribute("accept")));
    }

    [Fact]
    public void Predictions_requires_a_single_gpx_and_disables_submission_until_the_model_is_ready()
    {
        api.OnGetModelStatusAsync = _ => Task.FromResult(NotReadyModelStatus());
        api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([]);

        var cut = Render<Predictions>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[type=file]");
            Assert.Equal(".gpx", input.GetAttribute("accept"));
            Assert.Contains("Select one GPX file to start a prediction.", cut.Find("[data-testid=predictions-upload-empty]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Upload at least two eligible rides", cut.Find("[data-testid=predictions-model-guidance]").TextContent, StringComparison.Ordinal);
        });

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("gpx", "first.gpx"),
            InputFileContent.CreateFromText("gpx", "second.gpx"));

        cut.WaitForAssertion(() =>
        {
            var guidance = cut.Find("[data-testid=predictions-too-many-files]");
            Assert.Contains("single GPX route", guidance.TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=predictions-submit]"));
        });

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("gpx", "alpine-loop.gpx"));

        cut.WaitForAssertion(() =>
        {
            var submit = cut.Find("[data-testid=predictions-submit]");
            Assert.True(submit.HasAttribute("disabled"));
            Assert.Contains("alpine-loop.gpx", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Predictions_submits_a_prediction_polls_its_job_and_navigates_to_detail_on_success()
    {
        var predictionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
        api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([]);
        api.OnSubmitPredictionAsync = (file, _) =>
            Task.FromResult(new PredictionSubmissionResponse(predictionId, jobId, modelId));
        api.Jobs.Enqueue(Job(jobId, "Running", 25, "processing-route"));
        api.Jobs.Enqueue(Job(jobId, "Succeeded", 100, "completed"));

        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Predictions>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("gpx", "alpine-loop.gpx"));
        cut.Find("[data-testid=predictions-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.SubmittedPredictions);
            Assert.Equal("alpine-loop.gpx", api.SubmittedPredictions[0].File.FileName);

            var history = cut.Find("[data-testid=predictions-history-list]").TextContent;
            Assert.Contains("Queued", history, StringComparison.Ordinal);
            Assert.Contains(predictionId.ToString(), history, StringComparison.Ordinal);
        });

        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(jobId, api.RequestedJobs[0].JobId);
            Assert.EndsWith($"/predictions/{predictionId}", navigation.Uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Predictions_lists_history_newest_first_shows_terminal_states_and_refreshes_after_delete()
    {
        var newestId = Guid.NewGuid();
        var olderId = Guid.NewGuid();
        var requests = 0;
        var deleteCompletion = new TaskCompletionSource<bool>();

        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
        api.OnGetPredictionsAsync = _ =>
        {
            requests++;
            return Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>(requests switch
            {
                1 => [
                    PredictionSummary(olderId, "Failed", DateTimeOffset.Parse("2026-08-25T09:00:00Z", CultureInfo.InvariantCulture)),
                    PredictionSummary(newestId, "Cancelled", DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture))
                ],
                _ => [
                    PredictionSummary(newestId, "Cancelled", DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture))
                ]
            });
        };
        api.OnDeletePredictionAsync = (id, _) =>
        {
            if (id != olderId)
            {
                throw new InvalidOperationException("Unexpected prediction delete target.");
            }

            return deleteCompletion.Task;
        };

        var cut = Render<Predictions>();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-testid=predictions-history-item]");
            Assert.Equal(2, items.Count);
            Assert.Contains(newestId.ToString(), items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Cancelled", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Failed", items[1].TextContent, StringComparison.Ordinal);
            Assert.Contains("31.2 km", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("490 m", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("71 min", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("High confidence", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Tailwind estimated", items[0].TextContent, StringComparison.Ordinal);
        });

        cut.Find($"[data-testid='prediction-delete-request-{olderId}']").Click();
        cut.WaitForAssertion(() =>
        {
            var confirmation = cut.Find("[data-testid=prediction-delete-confirmation]");
            Assert.Contains("Deleting this prediction removes its retained GPX and result. Training data and rider models will not change.", confirmation.TextContent, StringComparison.Ordinal);
        });

        cut.Find("[data-testid=prediction-delete-cancel]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=prediction-delete-confirmation]")));

        cut.Find($"[data-testid='prediction-delete-request-{olderId}']").Click();
        cut.Find("[data-testid=prediction-delete-confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.DeletedPredictions);
            Assert.True(cut.Find($"[data-testid='prediction-delete-request-{newestId}']").HasAttribute("disabled"));
        });

        deleteCompletion.SetResult(true);

        await Task.Yield();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-testid=predictions-history-item]");
            Assert.Single(items);
            Assert.Contains(newestId.ToString(), items[0].TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain(olderId.ToString(), cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Predictions_shows_problem_state_when_history_loading_fails()
    {
        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
        api.OnGetPredictionsAsync = _ => Task.FromException<IReadOnlyList<PredictionSummaryResponse>>(
            new ApiProblemException(
                System.Net.HttpStatusCode.BadRequest,
                "predictions-unavailable",
                "Predictions could not be loaded.",
                "Try refreshing the predictions page."));

        var cut = Render<Predictions>();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid=predictions-history-error]");
            Assert.Contains("predictions-unavailable", alert.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Predictions_cancels_an_in_flight_submission_when_the_page_is_disposed()
    {
        CancellationToken observed = default;
        var submitCompletion = new TaskCompletionSource<PredictionSubmissionResponse>();

        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
        api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([]);
        api.OnSubmitPredictionAsync = (_, ct) =>
        {
            observed = ct;
            ct.Register(() => submitCompletion.TrySetCanceled(ct));
            return submitCompletion.Task;
        };

        var cut = Render<Predictions>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("gpx", "pending-route.gpx"));
        cut.Find("[data-testid=predictions-submit]").Click();
        cut.WaitForAssertion(() => Assert.Single(api.SubmittedPredictions));

        ((IDisposable)cut.Instance).Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await submitCompletion.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(observed.IsCancellationRequested);
    }

    private static ModelStatusResponse NotReadyModelStatus() => new(
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
        null);

    private static ModelStatusResponse ReadyModelStatus() => new(
        true,
        null,
        Guid.NewGuid(),
        "v1.0.0",
        DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture),
        true,
        true,
        "Validated",
        0.082,
        0.156,
        new PhysicalCoefficientsResponse(0.97, 1.225, 0.0045, 0.31),
        [new PowerBandCoverageResponse("flat", "5m", 255, 2400, 8, 0.15, "High")],
        16,
        2,
        null);

    private static PredictionSummaryResponse PredictionSummary(Guid id, string state, DateTimeOffset createdAt) => new(
        id,
        state,
        31234,
        490,
        4230,
        8.52,
        244,
        "High",
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
        createdAt,
        state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
            ? createdAt.AddMinutes(12)
            : null);

    private static JobResponse Job(Guid jobId, string state, int progressPercent, string stage) => new(
        jobId,
        "PredictRoute",
        Guid.NewGuid(),
        state,
        progressPercent,
        stage,
        1,
        DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T10:01:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T10:02:00Z", CultureInfo.InvariantCulture),
        state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.Parse("2026-08-25T10:03:00Z", CultureInfo.InvariantCulture)
            : null,
        null,
        null,
        null);
}
