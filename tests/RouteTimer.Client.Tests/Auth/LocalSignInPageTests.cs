using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
        cut.Find("[data-testid=local-signin-passphrase]").Change("correct horse battery staple");
        cut.Find("[data-testid=local-signin-confirm]").Change("something else entirely");
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
        cut.Find("[data-testid=local-signin-passphrase]").Change("correct horse battery staple");
        cut.Find("[data-testid=local-signin-confirm]").Change("correct horse battery staple");
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
        cut.Find("[data-testid=local-signin-passphrase]").Change("correct horse battery staple");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() => Assert.Single(api.LocalLogins));
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
        cut.Find("[data-testid=local-signin-passphrase]").Change("wrong passphrase entirely");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "That passphrase was not recognised.",
                cut.Find("[data-testid=local-signin-error]").TextContent,
                StringComparison.Ordinal));
    }
}
