using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Pages;

namespace RouteTimer.Client.Tests;

public sealed class UploadPageTests : BunitContext
{
    public UploadPageTests() => Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("https://example.test/") });

    [Fact]
    public void Training_accepts_multiple_fit_files()
    {
        var cut = Render<Training>();
        var input = cut.Find("input[type=file]");

        Assert.Equal(".fit", input.GetAttribute("accept"));
        Assert.NotNull(input.GetAttribute("multiple"));
    }

    [Fact]
    public void Predictions_accepts_one_gpx_file()
    {
        var cut = Render<Predictions>();
        var input = cut.Find("input[type=file]");

        Assert.Equal(".gpx", input.GetAttribute("accept"));
        Assert.Null(input.GetAttribute("multiple"));
    }
}
