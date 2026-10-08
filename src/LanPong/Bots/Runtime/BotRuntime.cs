using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using LanPong.Bots.Catalog;
using LanPong.Bots.Configuration;
using LanPong.Bots.Strategies;

namespace LanPong.Bots.Runtime;

internal sealed record BotAvailability(BotAvailabilityState State, string? Reason);

internal sealed class BotUnavailableException(string botId, string reason) : InvalidOperationException(reason)
{
    public string BotId { get; } = botId;
}

/// <summary>
/// Resolves only the selected entry and its explicit fallback chain. Model failures stay known until restart;
/// discovery reads status without loading a model, reading a file, or constructing a controller.
/// </summary>
internal sealed class BotRuntime
{
    private readonly FrozenDictionary<string, IBotStrategyFactory> _factories;
    private readonly Dictionary<string, BotAvailability> _availability = new(StringComparer.Ordinal);
    private readonly Lock _availabilityGate = new();
    private readonly ILogger<BotRuntime> _logger;

    public BotRuntime(BotCatalog catalog, IEnumerable<IBotStrategyFactory> factories, ILogger<BotRuntime> logger)
    {
        Catalog = catalog;
        _logger = logger;
        var registered = new Dictionary<string, IBotStrategyFactory>(StringComparer.Ordinal);
        foreach (var factory in factories)
        {
            var descriptor = factory.Descriptor;
            if (!BotsOptionsValidator.IsValidId(descriptor.Id) || !Enum.IsDefined(descriptor.SettingsKind))
                throw new InvalidOperationException("A bot factory has an invalid strategy descriptor.");
            if (!registered.TryAdd(descriptor.Id, factory))
                throw new InvalidOperationException($"Duplicate registered bot factory '{descriptor.Id}'.");
        }
        foreach (var entry in catalog.Entries)
            if (!registered.TryGetValue(entry.StrategyId, out var factory) ||
                factory.Descriptor.SettingsKind switch
                {
                    BotSettingsKind.Tracker => entry.Tracker is null || entry.Onnx is not null,
                    BotSettingsKind.Onnx => entry.Onnx is null || entry.Tracker is not null,
                    _ => true
                })
                throw new InvalidOperationException($"Bot '{entry.Id}' has no matching registered strategy factory.");
        _factories = registered.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public BotCatalog Catalog { get; }

    public BotAvailability GetAvailability(string botId)
    {
        var entry = Resolve(botId);
        if (!entry.Enabled) return new(BotAvailabilityState.Disabled, "Этот бот отключён.");
        lock (_availabilityGate)
            if (_availability.TryGetValue(botId, out var known)) return known;
        return entry.Onnx is null
            ? new(BotAvailabilityState.Ready, null)
            : new(BotAvailabilityState.NotChecked, "Модель будет проверена при выборе бота.");
    }

    public BotCatalogResponse DescribeCatalog()
    {
        // One coherent view of latched failures; this never invokes a factory or reads model files.
        lock (_availabilityGate)
        {
            var descriptors = new BotDescriptor[Catalog.Entries.Length];
            for (var index = 0; index < descriptors.Length; index++)
            {
                var entry = Catalog.Entries[index];
                var availability = GetAvailability(entry.Id);
                var canPlay = false;
                if (entry.Enabled)
                {
                    var candidate = entry;
                    while (true)
                    {
                        if (GetAvailability(candidate.Id).State is BotAvailabilityState.Ready or BotAvailabilityState.NotChecked)
                        {
                            canPlay = true;
                            break;
                        }
                        if (candidate.FallbackBotId is not { } fallbackId) break;
                        candidate = Resolve(fallbackId);
                    }
                }
                descriptors[index] = new(entry.Id, entry.Name, entry.Description, entry.Style,
                    entry.Difficulty, entry.Category, entry.Order, entry.Glyph, entry.Enabled,
                    entry.FallbackBotId, availability.State, availability.Reason, canPlay);
            }
            return new(Catalog.DefaultBotId, descriptors);
        }
    }

    // Call before entering the simulation state lock: Reset can read/hash/warm native resources here.
    public PreparedBotSession Prepare(string? botId)
    {
        var requested = Resolve(botId);
        if (!requested.Enabled) throw new InvalidOperationException("Этот бот отключён.");
        var candidates = new List<PreparedBotSession.Candidate>();
        string? firstFailure = null;
        try
        {
            var entry = requested;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(entry.Id))
            {
                var known = GetAvailability(entry.Id);
                if (known.State == BotAvailabilityState.Unavailable)
                {
                    firstFailure ??= candidates.Count == 0 ? known.Reason : null;
                }
                else if (entry.Enabled)
                {
                    ILocalOpponentController? controller = null;
                    try
                    {
                        controller = _factories[entry.StrategyId].Create(entry);
                        controller.Reset();
                        candidates.Add(new(entry, controller));
                        MarkReady(entry);
                    }
                    catch (Exception error)
                    {
                        var reason = MarkUnavailable(entry, error, duringPreparation: true);
                        firstFailure ??= candidates.Count == 0 ? reason : null;
                        if (controller is IDisposable disposable) DisposeController(disposable);
                        _logger.LogWarning(error, "Bot {BotId} could not be prepared", entry.Id);
                    }
                }
                if (entry.FallbackBotId is not { } fallbackId) break;
                entry = Resolve(fallbackId);
            }
            if (candidates.Count == 0)
                throw new BotUnavailableException(requested.Id, firstFailure ?? "Этот бот недоступен.");
            return new(this, requested, candidates, candidates[0].Definition.Id == requested.Id ? null : firstFailure);
        }
        catch
        {
            foreach (var candidate in candidates)
                if (candidate.Controller is IDisposable disposable) DisposeController(disposable);
            throw;
        }
    }

    private BotDefinition Resolve(string? botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            throw new ArgumentException("Выберите бота.");
        if (!Catalog.TryGet(botId, out var entry))
            throw new ArgumentException("Неизвестный бот.");
        return entry!;
    }

    private void MarkReady(BotDefinition entry)
    {
        lock (_availabilityGate)
        {
            // A concurrent successful preparation must not erase another session's observed failure.
            if (!_availability.TryGetValue(entry.Id, out var known) || known.State != BotAvailabilityState.Unavailable)
                _availability[entry.Id] = new(BotAvailabilityState.Ready, null);
        }
    }

    internal string MarkUnavailable(BotDefinition entry, Exception error, bool duringPreparation)
    {
        var reason = duringPreparation
            ? entry.Onnx is not null && error is (FileNotFoundException or DirectoryNotFoundException)
                ? "Модель бота не найдена."
                : entry.Onnx is not null && error is InvalidDataException
                    ? "Модель бота не прошла проверку."
                    : "Не удалось подготовить бота."
            : "Бот перестал отвечать.";
        lock (_availabilityGate)
            _availability[entry.Id] = new(BotAvailabilityState.Unavailable, reason);
        return reason;
    }

    // May invoke native cleanup; callers must drain outside the simulation state lock.
    public static void DisposeRetired(List<IDisposable>? controllers)
    {
        if (controllers is null) return;
        foreach (var controller in controllers)
            try { controller.Dispose(); }
            catch (Exception) { /* A failed native cleanup must not stop transitions or the game clock. */ }
    }

    internal void DisposeController(IDisposable controller)
    {
        try { controller.Dispose(); }
        catch (Exception error) { _logger.LogWarning(error, "A retired bot controller could not be disposed"); }
    }
}

/// <summary>
/// Owns fresh mutable policy state and every prepared fallback resource for one local session. Tick-time
/// failure advances to an already prepared policy and transfers expensive cleanup to the peer.
/// </summary>
internal sealed class PreparedBotSession : IDisposable
{
    internal sealed class Candidate(BotDefinition definition, ILocalOpponentController controller)
    {
        public BotDefinition Definition { get; } = definition;
        public ILocalOpponentController? Controller { get; set; } = controller;
    }

    private readonly BotRuntime _runtime;
    private readonly List<Candidate> _candidates;
    private List<IDisposable>? _retired;
    private int _current;
    private bool _disposed;
    private BotDefinition _effective;

    internal PreparedBotSession(BotRuntime runtime, BotDefinition requested, List<Candidate> candidates,
        string? fallbackReason)
    {
        _runtime = runtime;
        Requested = requested;
        _candidates = candidates;
        _effective = candidates[0].Definition;
        FallbackReason = fallbackReason;
    }

    public BotDefinition Requested { get; }
    public BotDefinition Effective => _effective;
    public string? FallbackReason { get; private set; }
    public bool IsPlayable => !_disposed && _current < _candidates.Count;

    // Diagnostic metadata is read after preparation, never during gameplay decisions.
    internal string? VerifiedModelSha256 => IsPlayable &&
        _candidates[_current].Controller is OnnxLocalOpponentController onnx ? onnx.ModelSha256 : null;

    public int GetAxis(GameState state, PaddleSide side)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Every prepared candidate sees the same perspective, including one selected after failure.
        // Keep the canonical policies, native ownership and model identity directly inspectable.
        var policyState = BotPolicyView.ForSide(state, side);
        while (IsPlayable)
        {
            var candidate = _candidates[_current];
            try { return candidate.Controller!.GetAxis(policyState); }
            catch (Exception error)
            {
                var reason = _runtime.MarkUnavailable(candidate.Definition, error, duringPreparation: false);
                FallbackReason ??= reason;
                Retire(candidate);
                _current++;
                AdvancePastRetired();
            }
        }
        return 0;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Never rewind to an entry that failed, and retain its prepared native sessions for rematches.
        for (var index = _current; index < _candidates.Count; index++)
        {
            var candidate = _candidates[index];
            if (candidate.Controller is null) continue;
            try { candidate.Controller.Reset(); }
            catch (Exception error)
            {
                var reason = _runtime.MarkUnavailable(candidate.Definition, error, duringPreparation: false);
                if (index == _current) FallbackReason ??= reason;
                Retire(candidate);
            }
        }
        AdvancePastRetired();
    }

    private void AdvancePastRetired()
    {
        while (_current < _candidates.Count && _candidates[_current].Controller is null) _current++;
        if (_current < _candidates.Count) _effective = _candidates[_current].Definition;
    }

    private void Retire(Candidate candidate)
    {
        if (candidate.Controller is IDisposable disposable) (_retired ??= []).Add(disposable);
        candidate.Controller = null;
    }

    public List<IDisposable>? TakeRetiredControllers()
    {
        var retired = _retired;
        _retired = null;
        return retired;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var index = _current; index < _candidates.Count; index++)
            if (_candidates[index].Controller is IDisposable disposable) _runtime.DisposeController(disposable);
        BotRuntime.DisposeRetired(TakeRetiredControllers());
    }
}
