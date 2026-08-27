using Microsoft.Extensions.Options;
using RouteTimer.Services.RoutePacer;

namespace RouteTimer.Api.RoutePacer;

// Runs with ValidateOnStart. Everything it rejects would otherwise surface as an unexplained
// failure on a rider's first handoff -- or worse, as a link RoutePacer silently refuses -- so the
// deployment is stopped at boot instead. Failure messages name the setting and never its value:
// they reach startup logs and container crash output.
public sealed class RoutePacerHandoffOptionsValidator : IValidateOptions<RoutePacerHandoffOptions>
{
    public ValidateOptionsResult Validate(string? name, RoutePacerHandoffOptions options)
    {
        // Checked even while disabled: the status endpoint publishes this origin to the browser
        // whatever the flag says, and the client refuses anything that is not a bare HTTPS origin.
        if (!Uri.TryCreate(options.RoutePacerBaseUrl, UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail(
                $"{RoutePacerHandoffOptions.SectionName}:RoutePacerBaseUrl must be an absolute https:// URL.");
        }

        if (baseUrl.AbsolutePath != "/" || baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
        {
            return ValidateOptionsResult.Fail(
                $"{RoutePacerHandoffOptions.SectionName}:RoutePacerBaseUrl must be a bare origin with no path, query, or fragment.");
        }

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.RelayUploadKey))
        {
            return ValidateOptionsResult.Fail(
                $"{RoutePacerHandoffOptions.SectionName}:RelayUploadKey is required when the handoff is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.SigningPrivateKeyPem))
        {
            return ValidateOptionsResult.Fail(
                $"{RoutePacerHandoffOptions.SectionName}:SigningPrivateKeyPem is required when the handoff is enabled.");
        }

        try
        {
            // Imported and thrown away: the point is to fail startup on a key RoutePacer could
            // never verify, not to hold one here. The real signer imports its own in DI.
            using var probe = EcdsaRoutePacerInvocationSigner.FromPem(options.SigningPrivateKeyPem);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The exception is deliberately not appended: a cryptography failure message can quote
            // the offending PEM text back at whoever is reading the logs.
            return ValidateOptionsResult.Fail(
                $"{RoutePacerHandoffOptions.SectionName}:SigningPrivateKeyPem must be a PKCS#8 ECDSA P-256 private key.");
        }

        return ValidateOptionsResult.Success;
    }
}
