using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Auth;
using RouteTimer.Client.Layout;
using RouteTimer.Client.Pages;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Tests.Auth;

public sealed class LocalModeUiTests : BunitContext
{
    private void Arrange(bool authorized = true)
    {
        Services.AddSingleton<IRouteTimerApiClient>(new FakeRouteTimerApiClient());
        Services.AddSingleton(new ClientAuthConfig(new AuthConfigResponse("Local", false, null, null, null, null)));
        var auth = this.AddAuthorization();
        if (authorized)
        {
            auth.SetAuthorized("rider");
            auth.SetRoles("rider");
        }
        else
        {
            auth.SetNotAuthorized();
        }
    }

    private void ArrangeKeycloak(bool authorized)
    {
        Services.AddSingleton<IRouteTimerApiClient>(new FakeRouteTimerApiClient());
        Services.AddSingleton(new ClientAuthConfig(new AuthConfigResponse(
            "Keycloak", false, "https://kc.invalid/realms/routetimer", "routetimer-web",
            "authentication/login-callback", "authentication/logout-callback")));
        var auth = this.AddAuthorization();
        if (authorized)
        {
            auth.SetAuthorized("rider");
            auth.SetRoles("rider");
        }
        else
        {
            auth.SetNotAuthorized();
        }
    }

    [Fact]
    public void MainLayout_renders_in_local_mode_without_throwing()
    {
        // Before this component branched on ClientAuthConfig.IsLocal, it always rendered the
        // Keycloak-only "authentication/logout" link, which routes to a page that throws in local
        // mode -- see Authentication_page_does_not_throw_in_local_mode below. Confirmed by reverting
        // this file to its pre-fix content: the link was present and this assertion failed.
        Arrange();
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "x"))));
        Assert.DoesNotContain("authentication/logout", cut.Markup);
    }

    [Fact]
    public void Authentication_page_does_not_throw_in_local_mode()
    {
        // Local mode registers no OIDC services at all (see Program.cs), so RemoteAuthenticatorView
        // throws InvalidOperationException resolving IRemoteAuthenticationService the moment this
        // page renders. Confirmed by reverting this file: rendering threw exactly that exception.
        // Any local-mode rider reaching /authentication/{action} -- a stale bookmark, a search
        // engine cache, a link from an old email -- hit this before the guard existed.
        Arrange();
        var cut = Render<Authentication>(p => p.Add(c => c.Action, "login"));
        Assert.Contains("does not use single sign-on", cut.Markup);
    }

    [Fact]
    public void MainLayout_shows_a_local_sign_out_button_when_authorized_in_local_mode()
    {
        Arrange(authorized: true);
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "x"))));
        Assert.Single(cut.FindAll("button.account-links__signout"));
        Assert.DoesNotContain("authentication/login", cut.Markup);
    }

    [Fact]
    public void MainLayout_shows_nothing_in_the_account_area_when_anonymous_in_local_mode()
    {
        // Unauthenticated riders in local mode reach sign-in via the automatic redirect, not a
        // header link, so nothing should render here at all.
        Arrange(authorized: false);
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "x"))));
        Assert.Empty(cut.FindAll("nav.account-links"));
    }

    // A change that made ClientAuthConfig.IsLocal always evaluate true would silently break every
    // Keycloak-mode deployment's header, and none of the local-mode tests above would notice --
    // they only prove local mode looks right, not that Keycloak mode still does too.
    [Fact]
    public void MainLayout_still_shows_the_keycloak_account_links_when_authorized_in_keycloak_mode()
    {
        ArrangeKeycloak(authorized: true);
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "x"))));
        Assert.Contains("authentication/profile", cut.Markup);
        Assert.Contains("authentication/logout", cut.Markup);
        Assert.Empty(cut.FindAll("button.account-links__signout"));
    }

    [Fact]
    public void MainLayout_still_shows_the_keycloak_login_link_when_anonymous_in_keycloak_mode()
    {
        ArrangeKeycloak(authorized: false);
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "x"))));
        Assert.Contains("authentication/login", cut.Markup);
    }
}
