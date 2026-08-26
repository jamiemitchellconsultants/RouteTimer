using System.Diagnostics;
using System.Text.Json;

namespace RouteTimer.EndToEnd.Tests;

public sealed class GarminDeploymentTests
{
    private const string AdapterServiceName = "routetimer-garmin-adapter";
    private const string EgressNetworkName = "garmin-egress";
    private const string PrivateNetworkName = "routetimer-private";
    private static readonly Lazy<JsonDocument> ComposeConfig = new(RenderComposeConfig);

    [Fact]
    public void Adapter_image_uses_Python_3_12_and_runs_as_a_non_root_user()
    {
        var dockerfilePath = FindRepositoryFile("garmin-adapter", "Dockerfile");

        Assert.True(File.Exists(dockerfilePath), "The Garmin adapter Dockerfile is required.");

        var dockerfile = File.ReadAllText(dockerfilePath);
        Assert.Contains(
            "FROM python:3.12-slim@sha256:229a2c5bfa27522db7815ea81f9bed70af17ccb9de9fc7ad142b1877b5830d36",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("useradd --create-home --uid 10001 adapter", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER adapter", dockerfile, StringComparison.Ordinal);
        Assert.Contains("routetimer_garmin.api:app", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_image_installs_only_hash_locked_external_dependencies()
    {
        var dockerfile = File.ReadAllText(FindRepositoryFile("garmin-adapter", "Dockerfile"));
        var lockPath = FindRepositoryFile("garmin-adapter", "requirements.lock");

        Assert.True(File.Exists(lockPath), "The Garmin adapter dependency lock is required.");

        var dependencyLock = File.ReadAllText(lockPath);
        Assert.Contains("fastapi==0.116.1", dependencyLock, StringComparison.Ordinal);
        Assert.Contains("garminconnect==0.3.4", dependencyLock, StringComparison.Ordinal);
        Assert.Contains("uvicorn==0.35.0", dependencyLock, StringComparison.Ordinal);
        Assert.Contains("setuptools==80.9.0", dependencyLock, StringComparison.Ordinal);
        Assert.Contains("--hash=sha256:", dependencyLock, StringComparison.Ordinal);
        Assert.Contains("COPY requirements.lock ./", dockerfile, StringComparison.Ordinal);
        Assert.Contains(
            "pip install --no-cache-dir --require-hashes -r requirements.lock",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "pip install --no-cache-dir --no-deps --no-build-isolation .",
            dockerfile,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_build_context_excludes_local_and_generated_files()
    {
        var dockerIgnorePath = FindRepositoryFile("garmin-adapter", ".dockerignore");

        Assert.True(File.Exists(dockerIgnorePath), "The Garmin adapter .dockerignore is required.");

        var ignoredEntries = File.ReadAllLines(dockerIgnorePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(".venv", ignoredEntries);
        Assert.Contains("dist", ignoredEntries);
        Assert.Contains("__pycache__", ignoredEntries);
        Assert.Contains(".pytest_cache", ignoredEntries);
        Assert.Contains(".mypy_cache", ignoredEntries);
        Assert.Contains(".ruff_cache", ignoredEntries);
    }

    [Fact]
    public void Compose_hardens_the_adapter_without_exposing_ports_or_secrets()
    {
        var adapter = Service(AdapterServiceName);

        AssertMissing(adapter, "ports");
        AssertMissing(adapter, "environment");
        AssertMissing(adapter, "env_file");
        AssertMissing(adapter, "secrets");
        AssertMissing(adapter, "volumes");
        AssertMissing(adapter, "privileged");
        AssertMissing(adapter, "network_mode");
        Assert.True(adapter.GetProperty("read_only").GetBoolean());
        Assert.Contains(
            "no-new-privileges:true",
            adapter.GetProperty("security_opt").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "/tmp:size=16m,mode=1777",
            adapter.GetProperty("tmpfs").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Compose_healthcheck_calls_the_adapter_health_endpoint()
    {
        var healthcheck = Service(AdapterServiceName).GetProperty("healthcheck");
        var command = healthcheck.GetProperty("test")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        Assert.Equal(
            [
                "CMD",
                "python",
                "-c",
                "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8081/health', timeout=3)",
            ],
            command);
    }

    [Fact]
    public void Compose_gives_only_the_adapter_private_API_and_non_internal_egress_networks()
    {
        var config = ComposeConfig.Value.RootElement;
        var networks = config.GetProperty("networks");

        Assert.True(networks.GetProperty(PrivateNetworkName).GetProperty("internal").GetBoolean());
        Assert.True(
            networks.TryGetProperty(EgressNetworkName, out var egress),
            "The Garmin adapter requires a dedicated egress network.");
        Assert.False(egress.TryGetProperty("internal", out var internalValue) && internalValue.GetBoolean());
        Assert.False(egress.TryGetProperty("external", out var externalValue) && externalValue.GetBoolean());

        var adapterNetworks = NetworkNames(Service(AdapterServiceName));
        Assert.Equal(
            [EgressNetworkName, PrivateNetworkName],
            adapterNetworks.Order(StringComparer.Ordinal));

        foreach (var service in config.GetProperty("services").EnumerateObject())
        {
            if (service.NameEquals(AdapterServiceName))
            {
                continue;
            }

            Assert.DoesNotContain(EgressNetworkName, NetworkNames(service.Value));
        }
    }

    [Fact]
    public void Compose_routes_RouteTimer_to_the_healthy_internal_adapter()
    {
        var routeTimer = Service("routetimer");
        var adapterDependency = routeTimer.GetProperty("depends_on").GetProperty(AdapterServiceName);
        var environment = routeTimer.GetProperty("environment");

        Assert.Equal("service_healthy", adapterDependency.GetProperty("condition").GetString());
        Assert.Equal(
            "http://routetimer-garmin-adapter:8081",
            environment.GetProperty("GarminAdapter__BaseUrl").GetString());
        Assert.Equal(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            environment.GetProperty("Garmin__TokenEncryptionKey").GetString());
        Assert.Contains(PrivateNetworkName, NetworkNames(routeTimer));
    }

    private static JsonElement Service(string name) =>
        ComposeConfig.Value.RootElement.GetProperty("services").GetProperty(name);

    private static HashSet<string> NetworkNames(JsonElement service) =>
        service.GetProperty("networks")
            .EnumerateObject()
            .Select(network => network.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static void AssertMissing(JsonElement element, string propertyName) =>
        Assert.False(
            element.TryGetProperty(propertyName, out _),
            $"Rendered Compose property '{propertyName}' must be absent.");

    private static JsonDocument RenderComposeConfig()
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
        startInfo.Environment["ROUTETIMER_DB_PASSWORD"] = "test";
        startInfo.Environment["KEYCLOAK_AUTHORITY"] = "https://keycloak.invalid/realms/routetimer";
        startInfo.Environment["ROUTETIMER_HOSTNAME"] = "routetimer.invalid";
        startInfo.Environment["ROUTETIMER_GARMIN_TOKEN_KEY"] =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

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

        throw new DirectoryNotFoundException("Could not find the RouteTimer repository root.");
    }

    private static string FindRepositoryFile(params string[] relativePath) =>
        Path.Combine([FindRepositoryRoot(), .. relativePath]);
}
