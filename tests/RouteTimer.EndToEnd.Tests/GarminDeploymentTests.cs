namespace RouteTimer.EndToEnd.Tests;

public sealed class GarminDeploymentTests
{
    [Fact]
    public void Adapter_image_uses_Python_3_12_and_runs_as_a_non_root_user()
    {
        var dockerfilePath = FindRepositoryFile("garmin-adapter", "Dockerfile");

        Assert.True(File.Exists(dockerfilePath), "The Garmin adapter Dockerfile is required.");

        var dockerfile = File.ReadAllText(dockerfilePath);
        Assert.Contains("FROM python:3.12-slim", dockerfile, StringComparison.Ordinal);
        Assert.Contains("useradd --create-home --uid 10001 adapter", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER adapter", dockerfile, StringComparison.Ordinal);
        Assert.Contains("routetimer_garmin.api:app", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_build_context_excludes_local_and_generated_files()
    {
        var dockerIgnorePath = FindRepositoryFile("garmin-adapter", ".dockerignore");

        Assert.True(File.Exists(dockerIgnorePath), "The Garmin adapter .dockerignore is required.");

        var dockerIgnore = File.ReadAllText(dockerIgnorePath);
        Assert.Contains(".venv", dockerIgnore, StringComparison.Ordinal);
        Assert.Contains("dist", dockerIgnore, StringComparison.Ordinal);
        Assert.Contains("__pycache__", dockerIgnore, StringComparison.Ordinal);
        Assert.Contains(".pytest_cache", dockerIgnore, StringComparison.Ordinal);
        Assert.Contains(".mypy_cache", dockerIgnore, StringComparison.Ordinal);
        Assert.Contains(".ruff_cache", dockerIgnore, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_keeps_the_Garmin_adapter_private_and_gives_it_no_database_or_key()
    {
        var compose = File.ReadAllText(FindRepositoryFile("docker-compose.yml"));

        Assert.Contains("  routetimer-garmin-adapter:", compose, StringComparison.Ordinal);

        var adapterBlock = Between(compose, "  routetimer-garmin-adapter:", "  routetimer:");
        Assert.DoesNotContain("ports:", adapterBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__RouteTimer", adapterBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Garmin__TokenEncryptionKey", adapterBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("volumes:", adapterBlock, StringComparison.Ordinal);
        Assert.Contains("routetimer-private", adapterBlock, StringComparison.Ordinal);
        Assert.Contains("read_only: true", adapterBlock, StringComparison.Ordinal);
        Assert.Contains("/tmp:size=16m,mode=1777", adapterBlock, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges:true", adapterBlock, StringComparison.Ordinal);
        Assert.Contains("healthcheck:", adapterBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_routes_RouteTimer_to_the_healthy_internal_adapter()
    {
        var compose = File.ReadAllText(FindRepositoryFile("docker-compose.yml"));

        var routeTimerBlock = Between(compose, "  routetimer:", "volumes:");
        Assert.Contains("routetimer-garmin-adapter:", routeTimerBlock, StringComparison.Ordinal);
        Assert.Contains("condition: service_healthy", routeTimerBlock, StringComparison.Ordinal);
        Assert.Contains(
            "GarminAdapter__BaseUrl: http://routetimer-garmin-adapter:8081",
            routeTimerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Garmin__TokenEncryptionKey: ${ROUTETIMER_GARMIN_TOKEN_KEY:?set ROUTETIMER_GARMIN_TOKEN_KEY}",
            routeTimerBlock,
            StringComparison.Ordinal);
        Assert.Contains("routetimer-private:\n    internal: true", compose, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RouteTimer.slnx")))
            {
                return Path.Combine([directory.FullName, .. relativePath]);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the RouteTimer repository root.");
    }

    private static string Between(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Expected to find start marker '{start}'.");

        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Expected to find end marker '{end}'.");

        return source[startIndex..endIndex];
    }
}
