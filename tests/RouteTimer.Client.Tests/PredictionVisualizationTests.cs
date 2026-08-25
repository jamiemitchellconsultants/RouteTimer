using System.Reflection;
using Bunit;
using Bunit.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using RouteTimer.Client.Components;
using RouteTimer.Contracts.Predictions;

namespace RouteTimer.Client.Tests;

public sealed class PredictionVisualizationTests : BunitContext
{
    public PredictionVisualizationTests() => JSInterop.Mode = JSRuntimeMode.Strict;

    [Fact]
    public void PredictionVisualization_initializes_map_and_profiles_when_segments_and_tiles_are_available()
    {
        Services.AddSingleton<IConfiguration>(BuildConfiguration(includeTiles: true));
        var module = SetupVisualizationModule();

        var cut = Render<PredictionVisualization>(parameters => parameters.Add(component => component.Segments, Segments));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(module.Invocations["initializeMap"]);
            Assert.Single(module.Invocations["initializeProfiles"]);
            Assert.Contains("0.5 km", cut.Find("[data-testid=prediction-visualization-selection]").TextContent, StringComparison.Ordinal);
            Assert.Contains("29.5 km/h", cut.Find("[data-testid=prediction-visualization-selection]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PredictionVisualization_shows_a_configuration_problem_and_skips_interop_without_tile_settings()
    {
        Services.AddSingleton<IConfiguration>(BuildConfiguration(includeTiles: false));
        var module = SetupVisualizationModule();

        var cut = Render<PredictionVisualization>(parameters => parameters.Add(component => component.Segments, Segments));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Map tile configuration is unavailable.", cut.Find("[data-testid=prediction-visualization-problem]").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("initializeMap", module.Invocations.Identifiers);
            Assert.DoesNotContain("initializeProfiles", module.Invocations.Identifiers);
        });
    }

    [Fact]
    public async Task PredictionVisualization_propagates_selection_between_map_and_profiles_and_updates_the_readout()
    {
        Services.AddSingleton<IConfiguration>(BuildConfiguration(includeTiles: true));
        var module = SetupVisualizationModule();

        var cut = Render<PredictionVisualization>(parameters => parameters.Add(component => component.Segments, Segments));
        var map = cut.FindComponent<RouteMap>();
        var profiles = cut.FindComponent<RouteProfiles>();

        await map.Instance.OnSequenceSelected(2);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("1.0 km", cut.Find("[data-testid=prediction-visualization-selection]").TextContent, StringComparison.Ordinal);
            Assert.Equal(2, module.Invocations["selectProfileSequence"][^1].Arguments.Last());
        });

        await profiles.Instance.OnSequenceSelected(1);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("0.5 km", cut.Find("[data-testid=prediction-visualization-selection]").TextContent, StringComparison.Ordinal);
            Assert.Equal(1, module.Invocations["selectMapSequence"][^1].Arguments.Last());
        });
    }

    [Fact]
    public async Task PredictionVisualization_preserves_the_selected_sequence_when_parameters_rerender()
    {
        Services.AddSingleton<IConfiguration>(BuildConfiguration(includeTiles: true));
        SetupVisualizationModule();
        IReadOnlyList<PredictionSegmentResponse> segments = Segments;
        RenderFragment fragment() => builder =>
        {
            builder.OpenComponent<PredictionVisualization>(0);
            builder.AddAttribute(1, nameof(PredictionVisualization.Segments), segments);
            builder.CloseComponent();
        };

        var cut = Render(fragment());
        var visualization = cut.FindComponent<PredictionVisualization>();

        await cut.FindComponent<RouteMap>().Instance.OnSequenceSelected(2);

        segments = Segments.Select(segment =>
            new PredictionSegmentResponse(
                segment.Sequence,
                segment.Latitude,
                segment.Longitude,
                segment.ElevationMetres,
                segment.CumulativeDistanceMetres,
                segment.SegmentDistanceMetres,
                segment.Gradient,
                segment.CurvaturePerMetre,
                segment.PredictedPowerWatts,
                segment.PredictedSpeedMetresPerSecond,
                segment.SegmentMovingSeconds,
                segment.CumulativeMovingSeconds,
                segment.Confidence)).ToArray();
        cut.Render();

        visualization.WaitForAssertion(() =>
        {
            Assert.Contains("1.0 km", visualization.Find("[data-testid=prediction-visualization-selection]").TextContent, StringComparison.Ordinal);
            Assert.Contains("High confidence", visualization.Find("[data-testid=prediction-visualization-selection]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PredictionVisualization_disposes_map_profiles_and_dotnet_references()
    {
        Services.AddSingleton<IConfiguration>(BuildConfiguration(includeTiles: true));
        var module = SetupVisualizationModule();

        var cut = Render<PredictionVisualization>(parameters => parameters.Add(component => component.Segments, Segments));
        var mapReference = GetDotNetReference(cut.FindComponent<RouteMap>().Instance, "dotNetReference");
        var profileReference = GetDotNetReference(cut.FindComponent<RouteProfiles>().Instance, "dotNetReference");

        await DisposeComponentsAsync();

        Assert.Single(module.Invocations["disposeMap"]);
        Assert.Single(module.Invocations["disposeProfiles"]);
        Assert.ThrowsAny<ObjectDisposedException>(() => _ = mapReference.Value);
        Assert.ThrowsAny<ObjectDisposedException>(() => _ = profileReference.Value);
    }

    private BunitJSModuleInterop SetupVisualizationModule()
    {
        var module = JSInterop.SetupModule("./js/route-visualization.js");
        module.SetupVoid("initializeMap", _ => true).SetVoidResult();
        module.SetupVoid("initializeProfiles", _ => true).SetVoidResult();
        module.SetupVoid("selectMapSequence", _ => true).SetVoidResult();
        module.SetupVoid("selectProfileSequence", _ => true).SetVoidResult();
        module.SetupVoid("disposeMap", _ => true).SetVoidResult();
        module.SetupVoid("disposeProfiles", _ => true).SetVoidResult();
        return module;
    }

    private static IConfiguration BuildConfiguration(bool includeTiles)
    {
        var values = includeTiles
            ? new Dictionary<string, string?>
            {
                ["MapTiles:Url"] = "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
                ["MapTiles:Attribution"] = "&copy; OpenStreetMap contributors"
            }
            : new Dictionary<string, string?>();

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static DotNetObjectReference<TComponent> GetDotNetReference<TComponent>(TComponent component, string fieldName)
        where TComponent : class
    {
        var field = typeof(TComponent).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<DotNetObjectReference<TComponent>>(field.GetValue(component));
    }

    private static IReadOnlyList<PredictionSegmentResponse> Segments =>
    [
        new PredictionSegmentResponse(1, 51.5007, -0.1246, 126, 500, 500, 0.02, 0.001, 246, 8.2, 60, 60, "Medium"),
        new PredictionSegmentResponse(2, 51.5105, -0.1224, 132, 1000, 500, 0.03, 0.001, 250, 8.9, 62, 122, "High")
    ];
}
