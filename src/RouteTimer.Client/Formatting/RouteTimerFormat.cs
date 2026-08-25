using System.Globalization;

namespace RouteTimer.Client.Formatting;

public static class RouteTimerFormat
{
    public static string Distance(double? metres) => metres is null
        ? "—"
        : $"{(metres.Value / 1000d).ToString("0.0", CultureInfo.CurrentCulture)} km";

    public static string Ascent(double? metres) => metres is null
        ? "—"
        : $"{metres.Value.ToString("0", CultureInfo.CurrentCulture)} m";

    public static string Duration(double? seconds) => seconds is null
        ? "—"
        : $"{Math.Round(seconds.Value / 60d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture)} min";

    public static string Speed(double? metresPerSecond) => metresPerSecond is null
        ? "—"
        : $"{metresPerSecond.Value.ToString("0.0", CultureInfo.CurrentCulture)} m/s";

    public static string SpeedKilometresPerHour(double? metresPerSecond) => metresPerSecond is null
        ? "—"
        : $"{(metresPerSecond.Value * 3.6d).ToString("0.0", CultureInfo.CurrentCulture)} km/h";

    public static string Power(double? watts) => watts is null
        ? "—"
        : $"{watts.Value.ToString("0", CultureInfo.CurrentCulture)} W";

    public static string Weight(double? kilograms) => kilograms is null
        ? "—"
        : $"{kilograms.Value.ToString("0.0", CultureInfo.CurrentCulture)} kg";

    public static string Percentage(double? value) => value is null
        ? "—"
        : $"{(value.Value * 100d).ToString("0.0", CultureInfo.CurrentCulture)}%";

    public static string DetailedDuration(double? seconds)
    {
        if (seconds is null)
        {
            return "—";
        }

        var duration = TimeSpan.FromSeconds(Math.Round(seconds.Value, MidpointRounding.AwayFromZero));
        return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    public static string Timestamp(DateTimeOffset? value) => value is null
        ? "—"
        : value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
