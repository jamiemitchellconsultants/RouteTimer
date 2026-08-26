using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Garmin;

namespace RouteTimer.Client.Tests.Components;

public sealed class GarminConnectionTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public GarminConnectionTests()
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
    }

    [Fact]
    public void Connection_shows_loading_then_credentials_without_rendering_connection_secrets()
    {
        var completion = new TaskCompletionSource<GarminConnectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnGetGarminConnectionAsync = _ => completion.Task;

        var cut = Render<GarminConnection>();

        Assert.Contains("Checking Garmin connection", cut.Find("[data-testid=garmin-connection-loading]").TextContent, StringComparison.Ordinal);

        completion.SetResult(NotConnected());

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=garmin-email]"));
            Assert.NotNull(cut.Find("[data-testid=garmin-password]"));
            Assert.Contains("not saved", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("access-token", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Connection_switches_from_credentials_to_mfa_and_redacts_submitted_credentials_and_challenge()
    {
        api.OnGetGarminConnectionAsync = _ => Task.FromResult(NotConnected());
        api.OnLoginGarminAsync = (_, _) => Task.FromResult(MfaRequired("challenge-secret"));
        var cut = Render<GarminConnection>();

        cut.Find("[data-testid=garmin-email]").Change("rider@example.com");
        cut.Find("[data-testid=garmin-password]").Change("password-secret");
        cut.Find("[data-testid=garmin-login]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=garmin-mfa-code]"));
            Assert.DoesNotContain("rider@example.com", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("password-secret", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("challenge-secret", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Connection_renders_safe_invalid_credentials_problem_and_clears_the_password()
    {
        api.OnGetGarminConnectionAsync = _ => Task.FromResult(NotConnected());
        api.OnLoginGarminAsync = (_, _) => Task.FromException<GarminConnectionResponse>(new ApiProblemException(
            HttpStatusCode.BadRequest,
            ErrorCodes.GarminCredentialsRejected,
            "Garmin credentials were rejected.",
            "Check the email and password, then try again."));
        var cut = Render<GarminConnection>();

        cut.Find("[data-testid=garmin-email]").Change("rider@example.com");
        cut.Find("[data-testid=garmin-password]").Change("password-secret");
        cut.Find("[data-testid=garmin-login]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(ErrorCodes.GarminCredentialsRejected, cut.Find("[data-testid=garmin-connection-error]").TextContent, StringComparison.Ordinal);
            Assert.Equal(string.Empty, cut.Find("[data-testid=garmin-password]").GetAttribute("value"));
            Assert.DoesNotContain("password-secret", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Connection_returns_to_credentials_when_the_mfa_challenge_expires_and_clears_the_code()
    {
        api.OnGetGarminConnectionAsync = _ => Task.FromResult(NotConnected());
        api.OnLoginGarminAsync = (_, _) => Task.FromResult(MfaRequired("challenge-secret"));
        api.OnCompleteGarminMfaAsync = (_, _) => Task.FromException<GarminConnectionResponse>(new ApiProblemException(
            HttpStatusCode.Conflict,
            ErrorCodes.GarminChallengeExpired,
            "Garmin challenge expired.",
            "Enter your credentials to start again."));
        var cut = Render<GarminConnection>();

        cut.Find("[data-testid=garmin-email]").Change("rider@example.com");
        cut.Find("[data-testid=garmin-password]").Change("password-secret");
        cut.Find("[data-testid=garmin-login]").Click();
        cut.WaitForElement("[data-testid=garmin-mfa-code]").Change("mfa-secret");
        cut.Find("[data-testid=garmin-mfa-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=garmin-email]"));
            Assert.Contains(ErrorCodes.GarminChallengeExpired, cut.Find("[data-testid=garmin-connection-error]").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("mfa-secret", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("challenge-secret", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Connection_completes_mfa_shows_safe_identity_and_clears_the_code()
    {
        api.OnGetGarminConnectionAsync = _ => Task.FromResult(NotConnected());
        api.OnLoginGarminAsync = (_, _) => Task.FromResult(MfaRequired("challenge-secret"));
        api.OnCompleteGarminMfaAsync = (_, _) => Task.FromResult(Connected("garmin-user-42", "Sunday Rider"));
        var cut = Render<GarminConnection>();

        cut.Find("[data-testid=garmin-email]").Change("rider@example.com");
        cut.Find("[data-testid=garmin-password]").Change("password-secret");
        cut.Find("[data-testid=garmin-login]").Click();
        cut.WaitForElement("[data-testid=garmin-mfa-code]").Change("mfa-secret");
        cut.Find("[data-testid=garmin-mfa-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            var identity = cut.Find("[data-testid=garmin-connected-identity]").TextContent;
            Assert.Contains("Sunday Rider", identity, StringComparison.Ordinal);
            Assert.Contains("garmin-user-42", identity, StringComparison.Ordinal);
            Assert.DoesNotContain("mfa-secret", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("challenge-secret", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Connection_disables_duplicate_login_submissions_while_the_request_is_active()
    {
        var completion = new TaskCompletionSource<GarminConnectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnGetGarminConnectionAsync = _ => Task.FromResult(NotConnected());
        api.OnLoginGarminAsync = (_, _) => completion.Task;
        var cut = Render<GarminConnection>();

        cut.Find("[data-testid=garmin-email]").Change("rider@example.com");
        cut.Find("[data-testid=garmin-password]").Change("password-secret");
        cut.Find("[data-testid=garmin-login]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.GarminLoginRequests);
            Assert.True(cut.Find("[data-testid=garmin-login]").HasAttribute("disabled"));
        });

        cut.Find("[data-testid=garmin-login]").Click();
        Assert.Single(api.GarminLoginRequests);

        completion.SetResult(NotConnected());
    }

    [Fact]
    public void Connection_requires_confirmation_before_disconnect_and_disables_duplicate_disconnects()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnGetGarminConnectionAsync = _ => Task.FromResult(Connected("garmin-user-42", "Sunday Rider"));
        api.OnDisconnectGarminAsync = _ => completion.Task;
        var cut = Render<GarminConnection>();

        cut.Find("[data-testid=garmin-disconnect-request]").Click();
        Assert.Empty(api.DisconnectedGarminConnections);
        Assert.NotNull(cut.Find("[data-testid=garmin-disconnect-confirmation]"));

        cut.Find("[data-testid=garmin-disconnect-cancel]").Click();
        Assert.Empty(cut.FindAll("[data-testid=garmin-disconnect-confirmation]"));

        cut.Find("[data-testid=garmin-disconnect-request]").Click();
        cut.Find("[data-testid=garmin-disconnect-confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.DisconnectedGarminConnections);
            Assert.True(cut.Find("[data-testid=garmin-disconnect-confirm]").HasAttribute("disabled"));
            Assert.True(cut.Find("[data-testid=garmin-disconnect-cancel]").HasAttribute("disabled"));
        });

        cut.Find("[data-testid=garmin-disconnect-confirm]").Click();
        Assert.Single(api.DisconnectedGarminConnections);

        completion.SetResult();
        cut.WaitForElement("[data-testid=garmin-email]");
    }

    [Fact]
    public async Task Connection_cancels_an_in_flight_request_when_disposed()
    {
        CancellationToken observed = default;
        var completion = new TaskCompletionSource<GarminConnectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnGetGarminConnectionAsync = ct =>
        {
            observed = ct;
            ct.Register(() => completion.TrySetCanceled(ct));
            return completion.Task;
        };

        var cut = Render<GarminConnection>();
        cut.WaitForAssertion(() => Assert.Single(api.RequestedGarminConnections));

        ((IDisposable)cut.Instance).Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(observed.IsCancellationRequested);
    }

    private static GarminConnectionResponse NotConnected() => new("not-connected", null, null, null);

    private static GarminConnectionResponse MfaRequired(string challengeId) => new("mfa-required", null, null, challengeId);

    private static GarminConnectionResponse Connected(string userId, string displayName) => new("connected", userId, displayName, null);
}
