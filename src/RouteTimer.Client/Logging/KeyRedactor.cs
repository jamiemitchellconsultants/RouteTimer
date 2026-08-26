using System.Diagnostics.CodeAnalysis;

namespace RouteTimer.Client.Logging;

public static class KeyRedactor
{
    // Real Google API keys are 39 characters. Anything shorter is not a key, and blindly
    // replacing a 3-character string would mangle unrelated text for no security benefit.
    private const int MinimumRedactableLength = 8;

    [return: NotNullIfNotNull(nameof(text))]
    public static string? Redact(string? text, string? key)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinimumRedactableLength) return text;

        return text.Replace(key, Mask(key), StringComparison.Ordinal);
    }

    public static string Mask(string key) =>
        key.Length < MinimumRedactableLength ? "…" : $"{key[..4]}…{key[^4..]}";
}
