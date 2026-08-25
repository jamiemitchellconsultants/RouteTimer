using System.Net;
using System.Globalization;
using Bunit;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components;
using RouteTimer.Client.Formatting;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;

namespace RouteTimer.Client.Tests.Components;

public sealed class SharedStatusComponentTests : BunitContext
{
    [Fact]
    public void ProblemMessage_renders_fallback_message_when_no_problem_is_present()
    {
        var cut = Render<ProblemMessage>(parameters => parameters
            .Add(component => component.Problem, null)
            .Add(component => component.FallbackMessage, "We could not load the latest status."));

        var alert = cut.Find("[role=alert]");
        Assert.Contains("We could not load the latest status.", alert.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemMessage_renders_safe_problem_details_and_field_errors()
    {
        var problem = new ApiProblemException(
            HttpStatusCode.BadRequest,
            "invalid-profile",
            "Profile is invalid.",
            "Review the highlighted profile fields.",
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["riderWeightKg"] = ["Rider weight must be between 30 and 250 kg."]
            });

        var cut = Render<ProblemMessage>(parameters => parameters
            .Add(component => component.Problem, problem)
            .Add(component => component.FallbackMessage, "Fallback"));

        Assert.Contains("invalid-profile", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Review the highlighted profile fields.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Rider weight must be between 30 and 250 kg.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void JobProgress_renders_progress_stage_text_and_safe_diagnostics()
    {
        var job = new JobResponse(
            Guid.NewGuid(),
            "PredictRoute",
            Guid.NewGuid(),
            "Running",
            45,
            "processing-route",
            2,
            DateTimeOffset.Parse("2026-08-25T09:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-25T09:01:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-25T09:02:00Z", CultureInfo.InvariantCulture),
            null,
            DateTimeOffset.Parse("2026-08-25T09:03:00Z", CultureInfo.InvariantCulture),
            null,
            null);

        var cut = Render<JobProgress>(parameters => parameters.Add(component => component.Job, job));

        var progress = cut.Find("progress");
        Assert.Equal("100", progress.GetAttribute("max"));
        Assert.Equal("45", progress.GetAttribute("value"));
        Assert.Contains("Processing route", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Running", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Attempt 2", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfidenceBadge_renders_text_and_class_for_confidence()
    {
        var cut = Render<ConfidenceBadge>(parameters => parameters.Add(component => component.Confidence, "High"));

        Assert.Contains("High confidence", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("confidence-high", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelStatus_renders_readiness_validation_and_rebuild_progress()
    {
        var status = new ModelStatusResponse(
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

        var cut = Render<ModelStatus>(parameters => parameters.Add(component => component.Status, status));

        Assert.Contains("Ready", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Validated", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Building power model", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("8.2%", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteTimerFormat_uses_metric_units_and_current_culture_numbers()
    {
        using var scope = new CultureScope("fr-FR");

        Assert.Equal("—", RouteTimerFormat.Distance(null));
        Assert.Equal("54,3 km", RouteTimerFormat.Distance(54321));
        Assert.Equal("91 min", RouteTimerFormat.Duration(5460));
        Assert.Equal("8,6 m/s", RouteTimerFormat.Speed(8.56));
        Assert.Equal("68,4 kg", RouteTimerFormat.Weight(68.4));
        Assert.Equal("8,2%", RouteTimerFormat.Percentage(0.082));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string cultureName)
            : this(new CultureInfo(cultureName))
        {
        }

        private CultureScope(CultureInfo culture)
        {
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
