using System.Globalization;
using System.Text;

namespace RouteTimer.Services.RoutePacer;

// Contract v1, frozen and mirrored byte-for-byte by a fixture in the RoutePacer repository. Every
// detail here -- field order, the separator, the absence of a trailing separator, the encoder -- is
// load-bearing for signature verification on the other side, so none of it may drift silently.
public static class RoutePacerContract
{
    public const string Source = "rt";
    public const int Version = 1;

    public static byte[] CanonicalBytes(Uri payloadUrl, string? name, long issuedUnixMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(payloadUrl);

        // No trailing line feed: the last field ends the message, so a signer that appended one
        // would produce signatures RoutePacer rejects for every handoff without explaining why.
        var canonical = string.Join(
            '\n',
            Source,
            Version.ToString(CultureInfo.InvariantCulture),
            payloadUrl.AbsoluteUri,
            name ?? string.Empty,
            issuedUnixMilliseconds.ToString(CultureInfo.InvariantCulture));

        return Encoding.UTF8.GetBytes(canonical);
    }

    public static Uri BuildInvocationUrl(
        Uri routePacerBaseUrl,
        Uri payloadUrl,
        string? name,
        DateTimeOffset issuedAt,
        IRoutePacerInvocationSigner signer)
    {
        ArgumentNullException.ThrowIfNull(routePacerBaseUrl);
        ArgumentNullException.ThrowIfNull(payloadUrl);
        ArgumentNullException.ThrowIfNull(signer);

        var issuedUnixMilliseconds = issuedAt.ToUnixTimeMilliseconds();
        var signature = signer.Sign(CanonicalBytes(payloadUrl, name, issuedUnixMilliseconds));

        // Built by hand rather than through QueryHelpers or a form encoder: the signed name is the
        // raw string, and a form encoder writes a space as '+', which RoutePacer would decode back
        // to '+' and then fail to verify. EscapeDataString is the only encoder that round-trips.
        var query = string.Concat(
            "?src=", Uri.EscapeDataString(Source),
            "&v=", Version.ToString(CultureInfo.InvariantCulture),
            "&payload=", Uri.EscapeDataString(payloadUrl.AbsoluteUri),
            "&name=", Uri.EscapeDataString(name ?? string.Empty),
            "&ts=", issuedUnixMilliseconds.ToString(CultureInfo.InvariantCulture),
            "&sig=", Uri.EscapeDataString(signature));

        return new Uri(new Uri(routePacerBaseUrl.GetLeftPart(UriPartial.Authority), UriKind.Absolute), "/open" + query);
    }

    public static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
