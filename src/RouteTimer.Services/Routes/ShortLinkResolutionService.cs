using System.Net;
using System.Text.RegularExpressions;

namespace RouteTimer.Services.Routes;

public sealed class ShortLinkCodeInvalidException() : Exception("The short-link code is not in the permitted form.");

public sealed class ShortLinkUnresolvedException() : Exception("The short link did not resolve to a Google Maps URL.");

public sealed partial class ShortLinkResolutionService(HttpClient httpClient)
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{4,64}$")]
    private static partial Regex PermittedCode();

    public async Task<string> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || !PermittedCode().IsMatch(code))
        {
            throw new ShortLinkCodeInvalidException();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{code}");
        // A browser-like User-Agent makes the endpoint answer 200 with a JavaScript interstitial
        // and no Location header; this deliberately non-browser agent is load-bearing. Set here,
        // not via HttpClient.DefaultRequestHeaders, so the guarantee holds regardless of how the
        // client is constructed.
        request.Headers.UserAgent.ParseAdd("RouteTimer/1.0");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
        {
            throw new ShortLinkUnresolvedException();
        }

        return response.Headers.Location.ToString();
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
