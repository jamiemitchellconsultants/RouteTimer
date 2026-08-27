using System.Text.Json;

namespace RouteTimer.Api.Tests.RoutePacer;

/// <summary>
/// Static checks over the tracked tree. The handoff's two secrets -- the relay upload credential
/// and the ECDSA signing key -- are the kind that leak by being convenient: a default in
/// appsettings, an example in a Compose file, a value that ends up in the browser bundle. These
/// assert the shipped defaults stay inert and that neither secret has a home in source.
/// </summary>
public sealed class RoutePacerSecretSafetyTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RouteTimer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Read(params string[] relativePath) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. relativePath]));

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void Tracked_api_settings_disable_the_handoff_and_carry_no_secret(string fileName)
    {
        using var document = JsonDocument.Parse(Read("src", "RouteTimer.Api", fileName));
        var section = document.RootElement.GetProperty("RoutePacerHandoff");

        Assert.False(section.GetProperty("Enabled").GetBoolean());
        Assert.Equal(string.Empty, section.GetProperty("RelayUploadKey").GetString());
        Assert.Equal(string.Empty, section.GetProperty("SigningPrivateKeyPem").GetString());
        Assert.Equal("https://pacetracking.tqaentry.com", section.GetProperty("RoutePacerBaseUrl").GetString());
    }

    [Fact]
    public void Compose_passes_both_secrets_from_the_environment()
    {
        var compose = Read("deploy", "docker-compose.yml");

        Assert.Contains("RoutePacerHandoff__Enabled:", compose, StringComparison.Ordinal);
        Assert.Contains("RoutePacerHandoff__RelayUploadKey: ${ROUTEPACER_RELAY_UPLOAD_KEY:-}", compose, StringComparison.Ordinal);
        Assert.Contains("RoutePacerHandoff__SigningPrivateKeyPem: ${ROUTEPACER_SIGNING_PRIVATE_KEY_PEM:-}", compose, StringComparison.Ordinal);
    }

    // The handoff is outbound-only: RouteTimer uploads to the relay and never serves a payload.
    // Nothing it adds may open a way in, so the port surface must be exactly what it was -- the
    // public deployment publishes none at all, and local mode publishes only its loopback bind.
    [Fact]
    public void Public_compose_publishes_no_ports_at_all()
    {
        Assert.DoesNotContain("ports:", Read("deploy", "docker-compose.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Local_compose_still_publishes_only_the_single_loopback_bind()
    {
        var compose = Read("deploy", "docker-compose.local.yml");

        // Declaration lines only -- the file also discusses ports in prose.
        var portBlocks = compose
            .Split('\n')
            .Count(line => line.Trim() == "ports:");

        Assert.Equal(1, portBlocks);
        Assert.Contains("127.0.0.1:${ROUTETIMER_PORT:-49215}:8080", compose, StringComparison.Ordinal);
    }

    // No Compose file names a relay payload path: a route to one would mean RouteTimer was
    // serving handoff content itself, which is the design this feature deliberately rejected.
    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.local.yml")]
    public void Compose_routes_nothing_to_a_payload_path(string fileName)
    {
        Assert.DoesNotContain("/api/handoffs", Read("deploy", fileName), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ingress_configuration_routes_nothing_to_the_handoff()
    {
        var caddyDirectory = Path.Combine(RepositoryRoot(), "deploy", "caddy");
        foreach (var file in Directory.EnumerateFiles(caddyDirectory, "*", SearchOption.AllDirectories))
        {
            var contents = File.ReadAllText(file);
            Assert.DoesNotContain("routepacer", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("handoffs", contents, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Break caught: an earlier design exposed a public RouteTimer payload endpoint signed with a
    // shared HMAC. Those names must not reappear anywhere in the implementation.
    [Theory]
    [InlineData("RouteTimerPublicBaseUrl")]
    [InlineData("/api/routepacer/payloads")]
    [InlineData("RoutePacerHandoff__SigningKey")]
    public void Superseded_public_endpoint_design_leaves_no_trace(string forbidden)
    {
        foreach (var file in SourceFiles())
        {
            Assert.DoesNotContain(forbidden, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_private_key_or_upload_credential_is_committed_outside_tests()
    {
        foreach (var file in SourceFiles())
        {
            var contents = File.ReadAllText(file);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN EC PRIVATE KEY", contents, StringComparison.Ordinal);
        }
    }

    // The browser must never receive either secret. The client only ever learns the public origin.
    [Fact]
    public void Client_assets_contain_no_handoff_secret()
    {
        var client = Path.Combine(RepositoryRoot(), "src", "RouteTimer.Client");
        foreach (var file in Directory.EnumerateFiles(client, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var contents = File.ReadAllText(file);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("RelayUploadKey", contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Relay_client_registration_redacts_the_authorization_header()
    {
        var program = Read("src", "RouteTimer.Api", "Program.cs");

        Assert.Contains("RedactLoggedHeaders([\"Authorization\"])", program, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRoot();
        string[] roots = [Path.Combine(root, "src"), Path.Combine(root, "deploy")];
        return roots
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("package-lock.json", StringComparison.Ordinal));
    }
}
