using RouteTimer.Domain.Adjustments;

namespace RouteTimer.Services.Adjustments;

/// <summary>
/// Resolves the one registered <see cref="IPacingStrategyHandler"/> for a given
/// <see cref="PacingStrategyType"/>. Fails fast at construction if a required (enabled) type has no
/// handler, or if more than one handler claims the same type - a startup misconfiguration, not a
/// per-request failure.
/// </summary>
public sealed class PacingStrategyDispatcher
{
    private readonly IReadOnlyDictionary<PacingStrategyType, IPacingStrategyHandler> _handlers;

    public PacingStrategyDispatcher(IEnumerable<IPacingStrategyHandler> handlers, IReadOnlyCollection<PacingStrategyType> enabledTypes)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(enabledTypes);

        var grouped = handlers.GroupBy(handler => handler.Type).ToList();
        var duplicates = grouped.Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate pacing strategy handlers registered for: {string.Join(", ", duplicates)}.");
        }

        _handlers = grouped.ToDictionary(group => group.Key, group => group.Single());
        var missing = enabledTypes.Where(type => !_handlers.ContainsKey(type)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"No pacing strategy handler registered for enabled type(s): {string.Join(", ", missing)}.");
        }

        EnabledTypes = enabledTypes.ToHashSet();
    }

    public IReadOnlySet<PacingStrategyType> EnabledTypes { get; }

    public bool IsEnabled(PacingStrategyType type) => EnabledTypes.Contains(type);

    /// <summary>Used when creating a new adjustment: a disabled type resolves to no handler, even if one happens to be registered.</summary>
    public IPacingStrategyHandler? TryGetHandlerForCreation(PacingStrategyType type) =>
        IsEnabled(type) ? _handlers.GetValueOrDefault(type) : null;

    /// <summary>
    /// Used when processing an already-queued job: disabling a strategy blocks new creation but must
    /// not strand an adjustment that was already accepted, so this ignores the enabled set.
    /// </summary>
    public IPacingStrategyHandler GetHandlerForProcessing(PacingStrategyType type) =>
        _handlers.TryGetValue(type, out var handler)
            ? handler
            : throw new InvalidOperationException($"No pacing strategy handler registered for type {type}.");
}
