using System.Diagnostics;
using System.Text.Json;

namespace RouteTimer.EndToEnd.Tests;

/// <summary>
/// Rendered through <c>docker compose config</c> rather than read as text, so these assert what
/// Compose actually resolves -- variable expansion included. The failure mode they exist for is
/// silent: a handoff enabled in production whose secrets expanded to empty strings, or an
/// interpolation that quietly published a port.
/// </summary>
public sealed class RoutePacerDeploymentTests
{
    private const string ApiServiceName = "routetimer";

    [Fact]
    public void Handoff_is_disabled_and_unconfigured_when_no_relay_variables_are_set()
    {
        var environment = ApiEnvironment(new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal("false", environment["RoutePacerHandoff__Enabled"]);
        Assert.Equal(string.Empty, environment["RoutePacerHandoff__RelayUploadKey"]);
        Assert.Equal(string.Empty, environment["RoutePacerHandoff__SigningPrivateKeyPem"]);
    }

    [Fact]
    public void Handoff_secrets_come_from_the_operator_environment_when_enabled()
    {
        var environment = ApiEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ROUTEPACER_HANDOFF_ENABLED"] = "true",
            ["ROUTEPACER_RELAY_UPLOAD_KEY"] = "upload-key-from-env",
            ["ROUTEPACER_SIGNING_PRIVATE_KEY_PEM"] = "pem-from-env"
        });

        Assert.Equal("true", environment["RoutePacerHandoff__Enabled"]);
        Assert.Equal("upload-key-from-env", environment["RoutePacerHandoff__RelayUploadKey"]);
        Assert.Equal("pem-from-env", environment["RoutePacerHandoff__SigningPrivateKeyPem"]);
    }

    // The whole premise: RouteTimer stays private. Enabling the outbound handoff must not add any
    // inbound surface, so the rendered service still publishes nothing.
    [Fact]
    public void Enabling_the_handoff_publishes_no_ports()
    {
        var service = ApiService(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ROUTEPACER_HANDOFF_ENABLED"] = "true",
            ["ROUTEPACER_RELAY_UPLOAD_KEY"] = "upload-key-from-env",
            ["ROUTEPACER_SIGNING_PRIVATE_KEY_PEM"] = "pem-from-env"
        });

        Assert.False(
            service.TryGetProperty("ports", out var ports) && ports.GetArrayLength() > 0,
            "The public RouteTimer deployment must publish no ports.");
    }

    private static Dictionary<string, string> ApiEnvironment(IReadOnlyDictionary<string, string> variables)
    {
        var environment = ApiService(variables).GetProperty("environment");
        return environment.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static JsonElement ApiService(IReadOnlyDictionary<string, string> variables)
    {
        using var config = RenderComposeConfig(variables);
        return config.RootElement.GetProperty("services").GetProperty(ApiServiceName).Clone();
    }

    private static JsonDocument RenderComposeConfig(IReadOnlyDictionary<string, string> variables)
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "deploy", "docker-compose.yml"));
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        // The unrelated required variables, so rendering succeeds; none is a real credential.
        startInfo.Environment["ROUTETIMER_DB_PASSWORD"] = "test";
        startInfo.Environment["KEYCLOAK_AUTHORITY"] = "https://keycloak.invalid/realms/routetimer";
        startInfo.Environment["ROUTETIMER_HOSTNAME"] = "routetimer.invalid";
        startInfo.Environment["ROUTETIMER_GARMIN_TOKEN_KEY"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        foreach (var variable in variables)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start docker compose config.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("docker compose config did not finish within 30 seconds.");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"docker compose config failed: {error}");

        return JsonDocument.Parse(output);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RouteTimer.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
