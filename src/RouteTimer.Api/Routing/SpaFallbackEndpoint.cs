using Microsoft.Extensions.FileProviders;

namespace RouteTimer.Api.Routing;

/// <summary>
/// Serves the compiled client's index.html for any unmapped browser navigation, so client-side
/// routes and the OIDC redirect/post-logout callbacks resolve instead of hitting the fallback
/// authorization policy or a bare 404.
/// </summary>
public static class SpaFallbackEndpoint
{
    public static IResult Handle(HttpContext context, IFileProvider webRoot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(webRoot);

        // /api and /health are exclusively this server's own surface. An unmapped path under either
        // one is a genuine 404 -- a removed endpoint, a typo -- not a client-side route, and must not
        // silently serve the app shell as HTML in place of the typed response a caller expected.
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/health"))
        {
            return Results.NotFound();
        }

        // MapFallbackToFile matches every HTTP method on any unmapped path, not only GET, and answers
        // a non-GET request with 405 rather than serving the file -- turning a POST to a typo'd or
        // legacy API path into 405 instead of the 404 several tests pin as that route's contract.
        // Restrict to GET/HEAD so every other method falls through to ordinary unmatched-route 404.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return Results.NotFound();
        }

        var indexFile = webRoot.GetFileInfo("index.html");
        if (!indexFile.Exists)
        {
            return Results.NotFound();
        }

        // A misconfigured or momentarily-broken deployment can end up routing a real static asset's
        // URL here instead of to the actual file (verified directly: a middleware-ordering bug once
        // did exactly that for every _framework/* request). Browsers cache a plain 200 with no
        // cache-control heuristically, keyed by URL -- for a fingerprinted asset path that never
        // changes on its own, that would mean the wrong (HTML) response outlives the bug that caused
        // it, breaking the app for that rider until they clear their cache even after a real fix
        // ships. This response must never be cached under someone else's URL.
        context.Response.Headers.CacheControl = "no-store";
        return Results.File(indexFile.CreateReadStream(), "text/html");
    }
}
