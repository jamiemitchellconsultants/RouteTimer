using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;

namespace RouteTimer.Api.Security;

/// <summary>
/// Rejects any request -- other than GET, HEAD, or OPTIONS -- whose <c>Sec-Fetch-Site</c> header
/// is present and not <c>same-origin</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>SameSite=Strict</c> on the local-mode session cookie stops the classic cross-site CSRF case
/// -- a page on evil.com cannot attach the cookie. But <c>SameSite</c> is site-scoped and ports are
/// not part of a site: a page served from <c>http://localhost:&lt;any other port&gt;</c> is
/// same-site to this app, so its cookie-bearing POSTs still carry the session. That is a poor fit
/// for a deployment whose entire network control is "only localhost can reach it" -- riders
/// routinely run other localhost web content (dev servers, Grafana, Jupyter, other Compose
/// stacks), and any of it can silently post to this API on the rider's behalf.
/// </para>
/// <para>
/// <c>Sec-Fetch-Site</c> is sent by every modern browser on every request and cannot be set or
/// overridden by page script, so it is a reliable same-origin signal -- unlike a same-site check,
/// it distinguishes different ports on localhost. It is absent from non-browser clients (curl,
/// server-to-server calls, most test hosts, older browsers); those requests pass through
/// unchecked rather than being refused, so this does not break tooling that never claimed to be a
/// browser in the first place.
/// </para>
/// <para>
/// GET and HEAD are exempt because they should not have side effects and are the shape a plain
/// cross-origin navigation or image/script tag can produce without the page itself making a
/// same-origin claim. OPTIONS is exempt for a different reason: a CORS preflight is an OPTIONS
/// request and carries Sec-Fetch-Site too, so blocking it here would not stop any real mutation --
/// the browser never sends the actual PUT/POST/DELETE without a successful preflight -- but it
/// would silently break CORS the moment anyone configures it, in a way that is confusing to debug
/// because the failure shows up as a generic preflight failure with no indication this middleware
/// was involved. Do not remove this exemption to "tighten" the check.
/// </para>
/// </remarks>
public static class SameOriginEnforcement
{
    private const string SecFetchSiteHeader = "Sec-Fetch-Site";
    private const string SameOrigin = "same-origin";

    public static IApplicationBuilder UseSameOriginEnforcement(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method)
                && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method))
            {
                var secFetchSite = context.Request.Headers[SecFetchSiteHeader];
                if (secFetchSite.Count > 0 && !string.Equals(secFetchSite, SameOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    await ApiProblems.Forbidden(
                        ErrorCodes.CrossSiteRequestRejected,
                        "This request did not report itself as same-origin and was refused.")
                        .ExecuteAsync(context);
                    return;
                }
            }

            await next(context);
        });
}
