using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using RouteTimer.Api.Routing;

namespace RouteTimer.Api.Tests.Routing;

public sealed class SpaFallbackEndpointTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("routetimer-spa-fallback-").FullName;

    [Theory]
    [InlineData("/predictions")]
    [InlineData("/authentication/login-callback")]
    [InlineData("/authentication/logout-callback")]
    [InlineData("/signin")]
    public async Task Serves_the_index_file_for_a_GET_on_an_unmapped_client_side_or_oidc_path(string path)
    {
        WriteIndexHtml("<html>RouteTimer</html>");

        var response = await ExecuteAsync("GET", path);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("text/html", response.ContentType);
        Assert.Equal("<html>RouteTimer</html>", response.Body);
    }

    [Fact]
    public async Task Serves_the_index_file_for_HEAD_too()
    {
        WriteIndexHtml("<html>RouteTimer</html>");

        var response = await ExecuteAsync("HEAD", "/predictions");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task Falls_through_to_404_for_every_method_other_than_GET_or_HEAD(string method)
    {
        // MapFallbackToFile matches every method on any unmapped path but only knows how to answer
        // GET/HEAD, so a POST would otherwise get 405 rather than the 404 several endpoint tests pin
        // as the documented contract for a route that does not exist. This is the test that would
        // have failed against the original MapFallbackToFile attempt, and did.
        WriteIndexHtml("<html>RouteTimer</html>");

        var response = await ExecuteAsync(method, "/predictions");

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
    }

    [Theory]
    [InlineData("/api/typo")]
    [InlineData("/api/training/uploads")]
    [InlineData("/health/anything")]
    public async Task Falls_through_to_404_for_an_unmapped_path_under_api_or_health_even_on_GET(string path)
    {
        // /api and /health are exclusively this server's own surface. Serving the app shell here
        // would turn a removed endpoint or a typo into a silent 200 of HTML where a caller expected
        // a typed JSON response or a real 404.
        WriteIndexHtml("<html>RouteTimer</html>");

        var response = await ExecuteAsync("GET", path);

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
    }

    [Fact]
    public async Task Falls_through_to_404_when_index_html_does_not_exist()
    {
        // The compiled client is only ever copied into wwwroot by the Docker build; this is the
        // shape every automated test in this project actually runs against.
        var response = await ExecuteAsync("GET", "/predictions");

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
    }

    private void WriteIndexHtml(string content) =>
        File.WriteAllText(Path.Combine(root, "index.html"), content);

    private async Task<(int Status, string Body, string? ContentType)> ExecuteAsync(string method, string path)
    {
        using var provider = new PhysicalFileProvider(root);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Request = { Method = method, Path = path },
            Response = { Body = new MemoryStream() }
        };

        var result = SpaFallbackEndpoint.Handle(context, provider);
        await result.ExecuteAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        var contentType = context.Response.ContentType?.Split(';')[0];
        return (context.Response.StatusCode, body, contentType);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
