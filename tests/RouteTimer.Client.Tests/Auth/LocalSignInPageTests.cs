using System.Net;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client;
using RouteTimer.Client.Api;
using RouteTimer.Client.Auth;
using RouteTimer.Client.Pages;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Tests.Auth;

public sealed class LocalSignInPageTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    private void Arrange(bool setupRequired)
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton(new ClientAuthConfig(
            new AuthConfigResponse("Local", setupRequired, null, null, null, null)));
    }

    private void ArrangeKeycloak()
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton(new ClientAuthConfig(new AuthConfigResponse(
            "Keycloak", false, "https://kc.invalid/realms/routetimer", "routetimer-web",
            "authentication/login-callback", "authentication/logout-callback")));
    }

    [Fact]
    public void First_run_shows_setup_wording_and_a_confirmation_field()
    {
        Arrange(setupRequired: true);

        var cut = Render<LocalSignIn>();

        Assert.Contains("Choose a passphrase", cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=local-signin-confirm]"));
    }

    [Fact]
    public void Returning_visit_shows_sign_in_wording_and_no_confirmation_field()
    {
        Arrange(setupRequired: false);

        var cut = Render<LocalSignIn>();

        Assert.Contains("Sign in", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=local-signin-confirm]"));
    }

    [Fact]
    public void Setup_refuses_to_submit_when_the_two_passphrases_differ()
    {
        Arrange(setupRequired: true);

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Input("correct horse battery staple");
        cut.Find("[data-testid=local-signin-confirm]").Input("something else entirely");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("do not match", cut.Find("[data-testid=local-signin-error]").TextContent, StringComparison.Ordinal);
            Assert.Empty(api.SetupLocalCredentials);
        });
    }

    [Fact]
    public void Setup_submits_the_passphrase_when_both_fields_match()
    {
        Arrange(setupRequired: true);
        api.OnSetupLocalCredentialAsync = (_, _) => Task.FromResult(true);

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Input("correct horse battery staple");
        cut.Find("[data-testid=local-signin-confirm]").Input("correct horse battery staple");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.SetupLocalCredentials);
            Assert.Equal("correct horse battery staple", api.SetupLocalCredentials[0].Passphrase);
        });
    }

    [Fact]
    public void Sign_in_submits_the_passphrase()
    {
        Arrange(setupRequired: false);
        api.OnLocalLoginAsync = (_, _) => Task.FromResult(true);

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Input("correct horse battery staple");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() => Assert.Single(api.LocalLogins));
    }

    [Fact]
    public void A_timed_out_submission_shows_an_error_and_re_enables_the_button()
    {
        // TaskCanceledException is what a timeout looks like here -- CancellationToken.None is
        // always what SubmitAsync passes, so this can only be a timeout, never a caller-initiated
        // cancellation. Removing the finally block that used to reset isSubmitting on this path left
        // the button disabled forever with no error shown; this pins the replacement catch.
        Arrange(setupRequired: false);
        api.OnLocalLoginAsync = (_, _) => Task.FromException<bool>(new TaskCanceledException());

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Input("correct horse battery staple");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("did not respond in time", cut.Find("[data-testid=local-signin-error]").TextContent, StringComparison.Ordinal);
            Assert.False(cut.Find("[data-testid=local-signin-submit]").HasAttribute("disabled"));
        });
    }

    [Fact]
    public void Submit_is_a_no_op_once_a_submission_is_already_in_flight()
    {
        // A future refactor that moves the isSubmitting=true assignment after an await, or adds
        // one before it, would silently reopen the double-submit window this test pins closed.
        Arrange(setupRequired: false);
        var gate = new TaskCompletionSource<bool>();
        api.OnLocalLoginAsync = (_, _) => gate.Task;

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Input("correct horse battery staple");
        var submit = cut.Find("[data-testid=local-signin-submit]");
        submit.Click();
        submit.Click();
        submit.Click();

        gate.SetResult(true);

        Assert.Single(api.LocalLogins);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_rejects_an_empty_or_whitespace_passphrase_without_a_round_trip(string passphrase)
    {
        // An accidental empty or whitespace-only submit must not reach the server: on the login path
        // it would count as a wrong guess against the rider's own lockout budget. Whitespace-only
        // used to slip past an IsNullOrEmpty check and spend that attempt for real.
        Arrange(setupRequired: false);

        var cut = Render<LocalSignIn>();
        if (!string.IsNullOrEmpty(passphrase))
        {
            cut.Find("[data-testid=local-signin-passphrase]").Input(passphrase);
        }

        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Enter a passphrase", cut.Find("[data-testid=local-signin-error]").TextContent, StringComparison.Ordinal);
            Assert.Empty(api.LocalLogins);
        });
    }

    [Fact]
    public void Renders_a_placeholder_instead_of_the_form_in_keycloak_mode()
    {
        // /signin only makes sense in local mode. Before this guard existed, the form rendered and
        // functioned in Keycloak mode too, posting to an endpoint that only exists in local mode.
        ArrangeKeycloak();

        var cut = Render<LocalSignIn>();

        Assert.Contains("does not use a local passphrase", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=local-signin-submit]"));
    }

    [Fact]
    public void A_rejected_passphrase_shows_the_api_problem_detail()
    {
        Arrange(setupRequired: false);
        api.OnLocalLoginAsync = (_, _) => Task.FromException<bool>(
            new ApiProblemException(
                HttpStatusCode.Unauthorized,
                "local-credential-rejected",
                "Sign-in failed",
                "That passphrase was not recognised."));

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Input("wrong passphrase entirely");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "That passphrase was not recognised.",
                cut.Find("[data-testid=local-signin-error]").TextContent,
                StringComparison.Ordinal));
    }
}

public sealed class RiderPageAuthorizationTests
{
    // Deleting [Authorize] from a page compiles cleanly and every existing test for that page still
    // passes, because those tests render the component directly rather than through the router --
    // AuthorizeRouteView never gets involved, so nothing else would notice the page had quietly
    // become reachable by an anonymous caller.
    [Theory]
    [InlineData(typeof(Home))]
    [InlineData(typeof(Profile))]
    [InlineData(typeof(Training))]
    [InlineData(typeof(TrainingDetail))]
    [InlineData(typeof(Predictions))]
    [InlineData(typeof(PredictionDetail))]
    public void Rider_pages_carry_the_authorize_attribute(Type page)
    {
        Assert.True(
            page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Length > 0,
            $"{page.Name} is missing [Authorize] and would render for an anonymous caller.");
    }

    [Theory]
    [InlineData(typeof(LocalSignIn))]
    [InlineData(typeof(Authentication))]
    [InlineData(typeof(NotFound))]
    public void Anonymous_pages_do_not_carry_the_authorize_attribute(Type page)
    {
        Assert.Empty(page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
    }
}

public sealed class RedirectToLoginTests : BunitContext
{
    [Fact]
    public void Redirects_to_the_local_sign_in_page_in_local_mode()
    {
        // This is the exact bug an earlier task fixed: reaching authentication/login in local mode
        // hits a page that throws, because local mode registers no OIDC services at all.
        Services.AddSingleton(new ClientAuthConfig(new AuthConfigResponse("Local", false, null, null, null, null)));

        Render<RedirectToLogin>();

        Assert.EndsWith("/signin", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Redirects_to_the_oidc_login_page_in_keycloak_mode()
    {
        Services.AddSingleton(new ClientAuthConfig(new AuthConfigResponse(
            "Keycloak", false, "https://kc.invalid/realms/routetimer", "routetimer-web",
            "authentication/login-callback", "authentication/logout-callback")));

        Render<RedirectToLogin>();

        Assert.Contains("authentication/login?returnUrl=", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }
}
