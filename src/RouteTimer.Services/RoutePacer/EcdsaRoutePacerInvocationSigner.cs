using System.Security.Cryptography;

namespace RouteTimer.Services.RoutePacer;

public sealed class EcdsaRoutePacerInvocationSigner : IRoutePacerInvocationSigner, IDisposable
{
    private readonly ECDsa ecdsa;

    private EcdsaRoutePacerInvocationSigner(ECDsa ecdsa) => this.ecdsa = ecdsa;

    public static EcdsaRoutePacerInvocationSigner FromPem(string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(privateKeyPem);
            // The curve is checked here rather than at first use: RoutePacer's verifier is fixed to
            // P-256, so a key on any other curve produces signatures nothing can verify, and that
            // is worth failing startup validation over rather than every rider's first handoff.
            var curveOid = ecdsa.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
            if (curveOid != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new InvalidOperationException(
                    "The RoutePacer signing key must use the P-256 curve.");
            }

            return new EcdsaRoutePacerInvocationSigner(ecdsa);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    // Fixed-width P1363 rather than the DER default: RoutePacer verifies with WebCrypto, whose
    // ECDSA implementation accepts only the raw r||s concatenation.
    public string Sign(ReadOnlySpan<byte> canonicalBytes) =>
        RoutePacerContract.ToBase64Url(ecdsa.SignData(
            canonicalBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => ecdsa.Dispose();
}
