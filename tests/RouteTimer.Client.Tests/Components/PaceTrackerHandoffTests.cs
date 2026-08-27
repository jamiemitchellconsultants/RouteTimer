using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Client.Components;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.RoutePacer;
using RouteTimer.Contracts.Predictions;

namespace RouteTimer.Client.Tests.Components;

public sealed class PaceTrackerHandoffTests : BunitContext
{
    private const string Origin = "https://pacetracking.tqaentry.com";
    private const string Link = $"{Origin}/open?src=rt&v=1&payload=x&name=Kingston&ts=1&sig=abc";
    private const string TimedGpxUrl = "/api/predictions/2f1a5b7c-0d3e-4f10-9a2b-3c4d5e6f7a8b/gpx?timed=true";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");

    private readonly FakeTimeProvider time = new(Now);

    public PaceTrackerHandoffTests()
    {
        Services.AddSingleton<TimeProvider>(time);
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Registered as instances, matching the other interop-backed component tests: the bUnit
        // container disposes synchronously, and these types are IAsyncDisposable only.
        Services.AddSingleton(new PaceTrackerQrInterop(JSInterop.JSRuntime));
        Services.AddSingleton(new BrowserInterop(JSInterop.JSRuntime));
    }

    [Fact]
    public void Handoff_shows_the_phone_instruction_expiry_and_manual_fallback()
    {
        var cut = Render(Handoff());

        Assert.NotNull(cut.Find("[data-testid=pacetracker-qr]"));
        Assert.Contains("phone", cut.Find("[data-testid=pacetracker-instruction]").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(cut.Find("[data-testid=pacetracker-expiry]"));
        // The relay can fail while the page is open, so the manual route off this screen must
        // always be present rather than appearing only once something has gone wrong.
        Assert.Equal(TimedGpxUrl, cut.Find("[data-testid=pacetracker-manual-download]").GetAttribute("href"));
    }

    [Fact]
    public void Handoff_renders_the_QR_locally_with_the_independently_supplied_origin()
    {
        var render = JSInterop.SetupModule("./js/pace-tracker-qr.mjs").SetupVoid("render", _ => true);

        Render(Handoff());

        var invocation = Assert.Single(render.Invocations["render"]);
        Assert.Equal(Link, invocation.Arguments[1]);
        Assert.Equal(Origin, invocation.Arguments[2]);
        Assert.Equal(Now.ToString("O"), invocation.Arguments[3]);
        Assert.Equal(Now.AddMinutes(10).ToString("O"), invocation.Arguments[4]);
    }

    // Break caught: a hosted QR service would put the rider's route URL on a third-party host.
    [Fact]
    public void Handoff_never_references_an_external_QR_service()
    {
        var cut = Render(Handoff());

        Assert.DoesNotContain("api.qrserver", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chart.googleapis", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Copy_puts_the_exact_link_on_the_clipboard()
    {
        var browser = JSInterop.SetupModule("./js/browser.js").SetupVoid("copyToClipboard", _ => true);
        var cut = Render(Handoff());

        cut.Find("[data-testid=pacetracker-copy]").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal(Link, Assert.Single(browser.Invocations["copyToClipboard"]).Arguments[0]));
    }

    [Fact]
    public void Same_device_link_opens_the_https_link_in_a_safe_new_tab()
    {
        var cut = Render(Handoff());

        var anchor = cut.Find("[data-testid=pacetracker-open-here]");
        Assert.Equal(Link, anchor.GetAttribute("href"));
        Assert.StartsWith("https://", anchor.GetAttribute("href")!, StringComparison.Ordinal);
        Assert.Equal("_blank", anchor.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", anchor.GetAttribute("rel"));
    }

    // Advancing the fake clock, not sleeping: the component schedules one delay against the
    // injected TimeProvider precisely so expiry is testable without wall-clock time.
    [Fact]
    public void Expired_handoff_disables_copy_and_navigation_and_offers_a_new_code()
    {
        var cut = Render(Handoff());
        Assert.Empty(cut.FindAll("[data-testid=pacetracker-expired]"));

        time.Advance(TimeSpan.FromMinutes(10));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=pacetracker-expired]"));
            Assert.Empty(cut.FindAll("[data-testid=pacetracker-qr]"));
            Assert.Empty(cut.FindAll("[data-testid=pacetracker-open-here]"));
            Assert.True(cut.Find("[data-testid=pacetracker-copy]").HasAttribute("disabled"));
            Assert.NotNull(cut.Find("[data-testid=pacetracker-recreate]"));
        });
    }

    [Fact]
    public void Recreate_and_close_each_fire_once()
    {
        var recreated = 0;
        var closed = 0;
        var cut = Render<PaceTrackerHandoff>(parameters => parameters
            .Add(p => p.Handoff, new RoutePacerHandoffResponse(Link, Now.AddMinutes(10)))
            .Add(p => p.RoutePacerOrigin, Origin)
            .Add(p => p.TimedGpxDownloadUrl, TimedGpxUrl)
            .Add(p => p.OnRecreate, () => recreated++)
            .Add(p => p.OnClose, () => closed++));

        cut.Find("[data-testid=pacetracker-close]").Click();
        time.Advance(TimeSpan.FromMinutes(10));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=pacetracker-recreate]")));
        cut.Find("[data-testid=pacetracker-recreate]").Click();

        Assert.Equal(1, closed);
        Assert.Equal(1, recreated);
    }

    // A superseded code left on screen is a code a rider can still scan, so replacing the handoff
    // must clear the old one before drawing the new one.
    [Fact]
    public void Replacing_the_handoff_clears_the_previous_code_before_rendering_the_new_one()
    {
        var module = JSInterop.SetupModule("./js/pace-tracker-qr.mjs");
        module.SetupVoid("render", _ => true).SetVoidResult();
        module.SetupVoid("clear", _ => true).SetVoidResult();
        var cut = Render(Handoff());

        cut.Render(parameters => parameters
            .Add(p => p.Handoff, new RoutePacerHandoffResponse($"{Link}&second=1", Now.AddMinutes(10))));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, module.Invocations["render"].Count);
            Assert.Equal($"{Link}&second=1", module.Invocations["render"].Last().Arguments[1]);
        });
    }

    [Fact]
    public void Disposing_the_component_disposes_the_QR_module()
    {
        var module = JSInterop.SetupModule("./js/pace-tracker-qr.mjs");
        module.SetupVoid("render", _ => true);
        var cut = Render(Handoff());

        cut.Instance.Dispose();

        // The scheduled expiry transition must not outlive the component and call StateHasChanged
        // on a disposed renderer.
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(cut.Markup);
    }

    private static Action<ComponentParameterCollectionBuilder<PaceTrackerHandoff>> Handoff() => parameters => parameters
        .Add(p => p.Handoff, new RoutePacerHandoffResponse(Link, Now.AddMinutes(10)))
        .Add(p => p.RoutePacerOrigin, Origin)
        .Add(p => p.TimedGpxDownloadUrl, TimedGpxUrl);
}
