namespace RouteTimer.Services.RoutePacer;

public interface IRoutePacerInvocationSigner
{
    string Sign(ReadOnlySpan<byte> canonicalBytes);
}
