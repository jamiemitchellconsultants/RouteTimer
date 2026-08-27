namespace RouteTimer.Services.RoutePacer;

// Registered whenever the handoff is disabled, so a deployment that configures no signing key still
// builds a complete container. It throws rather than returning a junk signature: a handoff that
// reached a signer while disabled is a routing bug, and RoutePacer would reject the result anyway.
public sealed class DisabledRoutePacerInvocationSigner : IRoutePacerInvocationSigner
{
    public string Sign(ReadOnlySpan<byte> canonicalBytes) =>
        throw new InvalidOperationException("RoutePacer handoff signing is disabled.");
}
