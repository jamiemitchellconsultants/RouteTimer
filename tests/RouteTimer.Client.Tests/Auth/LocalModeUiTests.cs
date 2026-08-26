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
    private void Arrange()
    {
        Services.AddSingleton<IRouteTimerApiClient>(new FakeRouteTimerApiClient());
        Services.AddSingleton(new ClientAuthConfig(new AuthConfigResponse("Local", false, null, null, null, null)));
        var auth = this.AddAuthorization();
        auth.SetAuthorized("rider");
        auth.SetRoles("rider");
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
}
