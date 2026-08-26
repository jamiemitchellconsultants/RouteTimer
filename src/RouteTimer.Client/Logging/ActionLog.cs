namespace RouteTimer.Client.Logging;

public sealed class ActionLog
{
    private readonly List<LogEntry> _entries = [];
    private string? _key;

    public IReadOnlyList<LogEntry> Entries => _entries;

    public event Action? Changed;

    public void UseRedactionKey(string? key)
    {
        _key = key;

        // Redaction must not depend on call order. If entries were logged before the key
        // was known, re-redact them retroactively so the guarantee holds.
        for (int i = 0; i < _entries.Count; i++)
        {
            var existing = _entries[i];
            _entries[i] = new LogEntry(
                existing.At,
                existing.Level,
                KeyRedactor.Redact(existing.Message, key),
                KeyRedactor.Redact(existing.Detail, key));
        }

        Changed?.Invoke();
    }

    public void Info(string message, string? detail = null) => Add(ActionLevel.Info, message, detail);
    public void Success(string message, string? detail = null) => Add(ActionLevel.Success, message, detail);
    public void Warn(string message, string? detail = null) => Add(ActionLevel.Warn, message, detail);
    public void Error(string message, string? detail = null) => Add(ActionLevel.Error, message, detail);

    public void Clear()
    {
        _entries.Clear();
        Changed?.Invoke();
    }

    public string ToPlainText() =>
        string.Join(Environment.NewLine, _entries.Select(e =>
            $"{e.At:HH:mm:ss.fff} [{e.Level.ToString().ToUpperInvariant()}] {e.Message}" +
            (e.Detail is null ? "" : Environment.NewLine + "    " + e.Detail.Replace("\n", "\n    "))));

    private void Add(ActionLevel level, string message, string? detail)
    {
        _entries.Add(new LogEntry(
            DateTimeOffset.UtcNow,
            level,
            KeyRedactor.Redact(message, _key),
            KeyRedactor.Redact(detail, _key)));

        Changed?.Invoke();
    }
}
