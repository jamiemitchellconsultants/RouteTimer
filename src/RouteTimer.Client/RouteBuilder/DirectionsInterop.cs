using System.Text.Json.Serialization;
using RouteTimer.Client.Logging;
using RouteTimer.Client.RouteBuilder.Models;
using Microsoft.JSInterop;

namespace RouteTimer.Client.RouteBuilder;

public sealed record DirectionsOutcome(
    IReadOnlyList<RoutePoint> Path,
    IReadOnlyList<GpxWaypoint> Waypoints,
    double DistanceMeters,
    double DurationSeconds,
    IReadOnlyList<string> LegSummaries);

public sealed class DirectionsInterop(IJSRuntime js, ActionLog log) : IAsyncDisposable
{
    private const int ElevationBatchSize = 256;
    private const int MaximumElevationPoints = 10_000;

    private IJSObjectReference? _module;
    private DotNetObjectReference<JsLogBridge>? _bridge;

    public bool ApiLoaded { get; private set; }

    public async Task LoadApiAsync(string key)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/gmaps.js");
        _bridge ??= DotNetObjectReference.Create(new JsLogBridge(log));

        if (ApiLoaded)
        {
            log.Warn("The Maps JavaScript API was already loaded earlier in this page session.",
                "It cannot be reloaded with a different key. If you have changed the key, press Reset first.");
            return;
        }

        await _module.InvokeVoidAsync("loadApi", key, _bridge);
        ApiLoaded = true;
    }

    public async Task<DirectionsOutcome> RouteAsync(ParsedRoute route)
    {
        if (_module is null || !ApiLoaded)
            throw new InvalidOperationException("Load the Maps JavaScript API before requesting directions.");

        if (route.IsSinglePoint)
            throw new InvalidOperationException(
                "This URL has only one point, so there is no route to request directions for.");

        var request = new RouteRequest(
            ToJs(route.Origin),
            ToJs(route.Destination!),
            route.Intermediates.Select(ToJs).ToArray(),
            route.Mode switch
            {
                TravelMode.Bicycling => "BICYCLING",
                TravelMode.Walking => "WALKING",
                TravelMode.Transit => "TRANSIT",
                _ => "DRIVING"
            });

        var result = await _module!.InvokeAsync<RouteResponse>("route", request, _bridge);

        return new DirectionsOutcome(
            result.Path.Select(p => new RoutePoint(p[0], p[1])).ToList(),
            result.Waypoints.Select(w => new GpxWaypoint(w.Lat, w.Lng, w.Name)).ToList(),
            result.DistanceMeters,
            result.DurationSeconds,
            result.LegSummaries);
    }

    public async Task<IReadOnlyList<RoutePoint>> ElevateAsync(IReadOnlyList<RoutePoint> path)
    {
        if (_module is null || !ApiLoaded)
            throw new InvalidOperationException("Load the Maps JavaScript API before requesting directions.");

        if (path.Count == 0) return path;

        var stride = path.Count <= MaximumElevationPoints
            ? 1
            : (int)Math.Ceiling(path.Count / (double)MaximumElevationPoints);

        if (stride > 1)
            log.Warn(
                $"Track has {path.Count} points; sampling elevation every {stride} points and interpolating between.",
                $"Requesting all of them would need {Math.Ceiling(path.Count / (double)ElevationBatchSize)} Elevation API calls.");

        var sampleIndices = new List<int>();
        for (var i = 0; i < path.Count; i += stride) sampleIndices.Add(i);
        if (sampleIndices[^1] != path.Count - 1) sampleIndices.Add(path.Count - 1);

        var samples = sampleIndices.Select(i => new[] { path[i].Lat, path[i].Lng }).ToArray();
        var elevations = await _module!.InvokeAsync<double[]>("elevate", samples, ElevationBatchSize, _bridge);

        if (elevations.Length != sampleIndices.Count)
        {
            log.Warn(
                $"Elevation service returned {elevations.Length} values for {sampleIndices.Count} samples; skipping elevation.");
            return path;
        }

        return Interpolate(path, sampleIndices, elevations);
    }

    public async Task ScrubKeyAsync()
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("scrub");
        log.Info("Removed the Maps API loader script element from the page.");
    }

    internal static List<RoutePoint> Interpolate(
        IReadOnlyList<RoutePoint> path, List<int> sampleIndices, double[] elevations)
    {
        var result = new List<RoutePoint>(path.Count);
        var sample = 0;

        for (var i = 0; i < path.Count; i++)
        {
            while (sample < sampleIndices.Count - 2 && sampleIndices[sample + 1] < i) sample++;

            var lowIndex = sampleIndices[sample];
            var highIndex = sampleIndices[Math.Min(sample + 1, sampleIndices.Count - 1)];

            double elevation;
            if (highIndex == lowIndex)
            {
                elevation = elevations[sample];
            }
            else
            {
                var fraction = (i - lowIndex) / (double)(highIndex - lowIndex);
                elevation = elevations[sample] +
                    (elevations[Math.Min(sample + 1, elevations.Length - 1)] - elevations[sample]) * fraction;
            }

            result.Add(path[i] with { Elevation = elevation });
        }

        return result;
    }

    // RouteTimer's predictor derives gradient from elevation, so a track missing any elevation
    // produces a confident and wrong answer. MapToGarmin tolerated this because a navigation
    // course does not need elevation; a prediction does.
    public static bool HasCompleteElevation(IReadOnlyList<RoutePoint> path) =>
        path.Count > 0 && path.All(point => point.Elevation is not null);

    private static JsWaypoint ToJs(RouteWaypoint waypoint) => waypoint switch
    {
        null => throw new ArgumentNullException(nameof(waypoint)),
        CoordinateWaypoint c => new JsWaypoint(c.Lat, c.Lng, null),
        PlaceNameWaypoint p => new JsWaypoint(0, 0, p.Name),
        _ => throw new InvalidOperationException($"Unknown waypoint type {waypoint.GetType().Name}.")
    };

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("teardown");
        }

        _bridge?.Dispose();

        if (_module is not null)
        {
            await _module.DisposeAsync();
        }
    }

    private sealed record JsWaypoint(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lng")] double Lng,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record RouteRequest(
        [property: JsonPropertyName("origin")] JsWaypoint Origin,
        [property: JsonPropertyName("destination")] JsWaypoint Destination,
        [property: JsonPropertyName("intermediates")] JsWaypoint[] Intermediates,
        [property: JsonPropertyName("mode")] string Mode);

    private sealed record RouteResponse(
        [property: JsonPropertyName("path")] double[][] Path,
        [property: JsonPropertyName("waypoints")] JsWaypointResult[] Waypoints,
        [property: JsonPropertyName("distanceMeters")] double DistanceMeters,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
        [property: JsonPropertyName("legSummaries")] string[] LegSummaries);

    private sealed record JsWaypointResult(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lng")] double Lng,
        [property: JsonPropertyName("name")] string Name);
}
