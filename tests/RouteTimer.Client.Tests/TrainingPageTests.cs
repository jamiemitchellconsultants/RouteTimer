using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Client.Api;
using RouteTimer.Client.Jobs;
using RouteTimer.Client.Pages;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Tests;

public sealed class TrainingPageTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();
    private readonly FakeTimeProvider time = new();

    public TrainingPageTests()
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton<TimeProvider>(time);
        Services.AddScoped<JobPoller>();
    }

    [Fact]
    public void Training_accepts_multiple_fit_files_and_starts_with_no_file_state()
    {
        api.OnGetTrainingActivitiesAsync = _ => Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>([]);
        api.OnGetModelStatusAsync = _ => Task.FromResult(NotReadyModelStatus());

        var cut = Render<Training>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[type=file]");
            Assert.Equal(".fit", input.GetAttribute("accept"));
            Assert.NotNull(input.GetAttribute("multiple"));
            Assert.Contains("Select one or more FIT files to upload.", cut.Find("[data-testid=training-upload-empty]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Training_shows_too_many_files_guidance_when_the_selection_exceeds_the_limit()
    {
        api.OnGetTrainingActivitiesAsync = _ => Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>([]);
        api.OnGetModelStatusAsync = _ => Task.FromResult(NotReadyModelStatus());

        var cut = Render<Training>();

        cut.FindComponent<InputFile>().UploadFiles(
            Enumerable.Range(0, 11)
                .Select(index => InputFileContent.CreateFromText("fit", $"ride-{index}.fit"))
                .ToArray());

        cut.WaitForAssertion(() =>
        {
            var guidance = cut.Find("[data-testid=training-too-many-files]");
            Assert.Contains("maximum of 10", guidance.TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=training-upload-submit]"));
        });
    }

    [Fact]
    public async Task Training_upload_renders_per_file_outcomes_polls_accepted_jobs_and_refreshes_activities_and_model()
    {
        var acceptedJobId = Guid.NewGuid();
        var rebuildJobId = Guid.NewGuid();
        var initialActivities = Array.Empty<TrainingActivitySummaryResponse>();
        var refreshedActivities = new[]
        {
            TrainingSummary(
                fileName: "accepted.fit",
                createdAt: DateTimeOffset.Parse("2026-08-25T11:00:00Z", CultureInfo.InvariantCulture),
                reasonCodes: ["steady-effort"])
        };
        var modelRequests = 0;

        api.OnGetTrainingActivitiesAsync = _ => Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>(
            api.RequestedTrainingActivities.Count < 2 ? initialActivities : refreshedActivities);
        api.OnGetModelStatusAsync = _ =>
        {
            modelRequests++;
            return Task.FromResult(modelRequests switch
            {
                1 => NotReadyModelStatus(),
                2 => ModelStatusWithRebuild(rebuildJobId, "Running", 40, "building-power-model"),
                _ => ReadyModelStatus()
            });
        };
        api.OnUploadTrainingActivitiesAsync = (files, _) =>
        {
            return Task.FromResult(new TrainingUploadBatchResponse(
            [
                new TrainingUploadFileResponse(files[0].FileName, "accepted", Guid.NewGuid(), acceptedJobId, null),
                new TrainingUploadFileResponse(files[1].FileName, "duplicate", null, null, "duplicate-upload"),
                new TrainingUploadFileResponse(files[2].FileName, "invalid", null, null, "invalid-fit-upload")
            ]));
        };
        api.Jobs.Enqueue(Job(acceptedJobId, "Running", 20, "decoding-fit"));
        api.Jobs.Enqueue(Job(acceptedJobId, "Succeeded", 100, "completed"));
        api.Jobs.Enqueue(Job(rebuildJobId, "Running", 40, "building-power-model"));
        api.Jobs.Enqueue(Job(rebuildJobId, "Succeeded", 100, "completed"));

        var cut = Render<Training>();

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("fit", "accepted.fit"),
            InputFileContent.CreateFromText("fit", "duplicate.fit"),
            InputFileContent.CreateFromText("plain", "notes.txt"));

        cut.Find("[data-testid=training-upload-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.UploadedTrainingActivities);
            Assert.Equal(["accepted.fit", "duplicate.fit", "notes.txt"], api.UploadedTrainingActivities[0].Files.Select(file => file.FileName));
            var outcomes = cut.Find("[data-testid=training-upload-results]").TextContent;
            Assert.Contains("accepted.fit", outcomes, StringComparison.Ordinal);
            Assert.Contains("Accepted", outcomes, StringComparison.Ordinal);
            Assert.Contains("Decoding FIT", outcomes, StringComparison.Ordinal);
            Assert.Contains("duplicate.fit", outcomes, StringComparison.Ordinal);
            Assert.Contains("Duplicate", outcomes, StringComparison.Ordinal);
            Assert.Contains("duplicate-upload", outcomes, StringComparison.Ordinal);
            Assert.Contains("notes.txt", outcomes, StringComparison.Ordinal);
            Assert.Contains("Invalid", outcomes, StringComparison.Ordinal);
            Assert.Contains("invalid-fit-upload", outcomes, StringComparison.Ordinal);
        });

        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.RequestedTrainingActivities.Count);
            Assert.True(api.RequestedModelStatuses.Count >= 3);
            var activities = cut.Find("[data-testid=training-activities-list]").TextContent;
            Assert.Contains("accepted.fit", activities, StringComparison.Ordinal);
            Assert.Contains("Steady effort", activities, StringComparison.Ordinal);
            Assert.DoesNotContain("Building power model", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Training_lists_newest_first_with_eligibility_reason_text_detail_link_and_empty_state()
    {
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();
        api.OnGetTrainingActivitiesAsync = _ => Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>(
        [
            TrainingSummary(
                id: olderId,
                fileName: "older.fit",
                eligibility: "Eligible",
                reasonCodes: [],
                createdAt: DateTimeOffset.Parse("2026-08-25T09:00:00Z", CultureInfo.InvariantCulture)),
            TrainingSummary(
                id: newerId,
                fileName: "newer.fit",
                eligibility: "InsufficientPower",
                reasonCodes: ["low-power-coverage", "position-gap"],
                createdAt: DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture))
        ]);
        api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());

        var cut = Render<Training>();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-testid=training-activity-item]");
            Assert.Equal(2, items.Count);
            Assert.Contains("newer.fit", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Insufficient power", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Low power coverage", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Position gap", items[0].TextContent, StringComparison.Ordinal);
            Assert.Contains($"training/{newerId}", items[0].QuerySelector("a")!.GetAttribute("href"), StringComparison.Ordinal);
            Assert.Contains("older.fit", items[1].TextContent, StringComparison.Ordinal);
            Assert.Contains("Eligible", items[1].TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Training_cancels_an_in_flight_upload_when_the_page_is_disposed()
    {
        CancellationToken observed = default;
        var uploadCompletion = new TaskCompletionSource<TrainingUploadBatchResponse>();

        api.OnGetTrainingActivitiesAsync = _ => Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>([]);
        api.OnGetModelStatusAsync = _ => Task.FromResult(NotReadyModelStatus());
        api.OnUploadTrainingActivitiesAsync = (_, ct) =>
        {
            observed = ct;
            ct.Register(() => uploadCompletion.TrySetCanceled(ct));
            return uploadCompletion.Task;
        };

        var cut = Render<Training>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("fit", "ride.fit"));
        cut.Find("[data-testid=training-upload-submit]").Click();
        cut.WaitForAssertion(() => Assert.Single(api.UploadedTrainingActivities));

        ((IDisposable)cut.Instance).Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await uploadCompletion.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task Training_uses_inline_delete_confirmation_allows_cancel_and_refreshes_after_confirmed_delete()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var rebuildJobId = Guid.NewGuid();
        var deleteCompletion = new TaskCompletionSource<bool>();
        var trainingRequests = 0;
        var modelRequests = 0;

        api.OnGetTrainingActivitiesAsync = _ =>
        {
            trainingRequests++;
            return Task.FromResult<IReadOnlyList<TrainingActivitySummaryResponse>>(trainingRequests switch
            {
                1 => [
                    TrainingSummary(id: firstId, fileName: "first.fit", createdAt: DateTimeOffset.Parse("2026-08-25T09:00:00Z", CultureInfo.InvariantCulture)),
                    TrainingSummary(id: secondId, fileName: "second.fit", createdAt: DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture))
                ],
                _ => [
                    TrainingSummary(id: secondId, fileName: "second.fit", createdAt: DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture))
                ]
            });
        };
        api.OnGetModelStatusAsync = _ =>
        {
            modelRequests++;
            return Task.FromResult(modelRequests switch
            {
                1 => ReadyModelStatus(),
                2 => ModelStatusWithRebuild(rebuildJobId, "Running", 35, "building-power-model"),
                _ => ReadyModelStatus()
            });
        };
        api.OnDeleteTrainingActivityAsync = (id, _) =>
        {
            if (id != firstId)
            {
                throw new InvalidOperationException("Unexpected delete target.");
            }

            return deleteCompletion.Task;
        };
        api.Jobs.Enqueue(Job(rebuildJobId, "Running", 35, "building-power-model"));
        api.Jobs.Enqueue(Job(rebuildJobId, "Succeeded", 100, "completed"));

        var cut = Render<Training>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=training-activity-item]").Count));

        cut.Find($"[data-testid='training-delete-request-{firstId}']").Click();
        cut.WaitForAssertion(() =>
        {
            var confirmation = cut.Find("[data-testid=training-delete-confirmation]");
            Assert.Contains("Deleting this activity removes its retained training evidence and queues a new rider-model build. Historical predictions will not change.", confirmation.TextContent, StringComparison.Ordinal);
        });

        cut.Find("[data-testid=training-delete-cancel]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=training-delete-confirmation]")));

        cut.Find($"[data-testid='training-delete-request-{firstId}']").Click();
        cut.Find("[data-testid=training-delete-confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.DeletedTrainingActivities);
            Assert.True(cut.Find($"[data-testid='training-delete-request-{secondId}']").HasAttribute("disabled"));
        });

        deleteCompletion.SetResult(true);

        cut.WaitForAssertion(() =>
        {
            var activities = cut.Find("[data-testid=training-activities-list]").TextContent;
            Assert.DoesNotContain("first.fit", activities, StringComparison.Ordinal);
            Assert.Contains("second.fit", activities, StringComparison.Ordinal);
            Assert.Contains("Building power model", cut.Markup, StringComparison.Ordinal);
        });

        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Yield();

        cut.WaitForAssertion(() =>
        {
            Assert.True(api.RequestedModelStatuses.Count >= 3);
            Assert.DoesNotContain("Building power model", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Training_shows_problem_state_when_loading_activities_fails()
    {
        api.OnGetTrainingActivitiesAsync = _ => Task.FromException<IReadOnlyList<TrainingActivitySummaryResponse>>(
            new ApiProblemException(
                System.Net.HttpStatusCode.BadRequest,
                "training-unavailable",
                "Training activities could not be loaded.",
                "Try refreshing the training page."));
        api.OnGetModelStatusAsync = _ => Task.FromResult(NotReadyModelStatus());

        var cut = Render<Training>();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid=training-activities-error]");
            Assert.Contains("training-unavailable", alert.TextContent, StringComparison.Ordinal);
        });
    }

    private static TrainingActivitySummaryResponse TrainingSummary(
        Guid? id = null,
        string fileName = "ride.fit",
        string eligibility = "Eligible",
        IReadOnlyList<string>? reasonCodes = null,
        DateTimeOffset? createdAt = null) => new(
        id ?? Guid.NewGuid(),
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
        reasonCodes ?? [],
        createdAt ?? DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture));

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

    private static ModelStatusResponse ModelStatusWithRebuild(Guid jobId, string state, int progressPercent, string stage) => new(
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
        Job(jobId, state, progressPercent, stage));

    private static JobResponse Job(Guid jobId, string state, int progressPercent, string stage) => new(
        jobId,
        "BuildModel",
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
