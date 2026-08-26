using System.Globalization;
using System.Text;

namespace RouteTimer.Client.Formatting;

public static class RouteTimerText
{
    public static string GarminActivityType(string? value) => value switch
    {
        "road-cycling" => "Road cycling",
        "gravel-cycling" => "Gravel cycling",
        _ => Sentence(value)
    };

    public static string GarminImportOutcome(string? value) => value switch
    {
        "accepted" => "Download accepted",
        "already-imported" => "Already imported",
        "duplicate" => "Duplicate FIT",
        "invalid-fit" => "Invalid FIT",
        "download-failed" => "Garmin download failed",
        _ => Sentence(value)
    };

    public static string Sentence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unavailable";
        }

        var builder = new StringBuilder(value.Length * 2);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is '-' or '_')
            {
                builder.Append(' ');
                continue;
            }

            if (index > 0
                && char.IsUpper(current)
                && builder.Length > 0
                && builder[^1] != ' '
                && char.IsLetterOrDigit(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLower(current, CultureInfo.CurrentCulture));
        }

        var normalized = string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length == 0
            ? "Unavailable"
            : string.Create(normalized.Length, normalized, static (span, source) =>
            {
                source.AsSpan().CopyTo(span);
                span[0] = char.ToUpper(span[0], CultureInfo.CurrentCulture);
            });
    }
}
