using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanPong.Tests;

internal static class BotTestSupport
{
    internal static BotEntryOptions Tracker(string id = "tracker", string? name = null,
        bool enabled = true, string? fallback = null, int cadence = 9, double deadZone = 0.014) => new()
    {
        Id = id, Name = name ?? id, Description = "A configured test opponent.", Style = "Measured",
        Difficulty = "Practice", Category = "Tests", Enabled = enabled,
        StrategyId = BotStrategyDescriptor.TrackerId, FallbackBotId = fallback,
        Tracker = new TrackerBotOptions { ObservationIntervalTicks = cadence, TargetDeadZone = deadZone }
    };

    internal static BotEntryOptions Onnx(string id = "model", string? fallback = null,
        string? path = null) => new()
    {
        Id = id, Name = $"Model {id}", Description = "A configured model opponent.", Style = "Trained",
        Difficulty = "Advanced", Category = "Tests", StrategyId = BotStrategyDescriptor.OnnxId,
        FallbackBotId = fallback, Onnx = new OnnxBotOptions { ModelPath = path ?? "Models/hard-v1.onnx" }
    };

    internal static BotRuntime Runtime(IEnumerable<BotEntryOptions> entries,
        params IBotStrategyFactory[] factories)
    {
        var list = entries.ToList();
        var options = new BotsOptions { DefaultBotId = list.First(entry => entry.Enabled).Id, Entries = list };
        var registry = new BotStrategyRegistry([
            new(BotStrategyDescriptor.TrackerId, BotSettingsKind.Tracker),
            new(BotStrategyDescriptor.OnnxId, BotSettingsKind.Onnx)
        ]);
        var validation = new BotsOptionsValidator(registry).Validate(null, options);
        if (validation.Failed)
            throw new InvalidOperationException(string.Join("; ", validation.Failures ?? []));
        var catalog = new BotCatalog(Options.Create(options));
        return new BotRuntime(catalog, factories, NullLogger<BotRuntime>.Instance);
    }

    internal static async Task<PongSnapshot> WaitForAsync(PongPeer peer, Func<PongSnapshot, bool> predicate,
        TimeSpan? timeoutAfter = null)
    {
        using var timeout = new CancellationTokenSource(timeoutAfter ?? TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = peer.Snapshot();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10);
        }
        throw new TimeoutException("The opponent session did not reach the expected state.");
    }

    internal static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    internal static int ReservePort()
    {
        using var reserved = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)reserved.Client.LocalEndPoint!).Port;
    }

    internal static string ModelPath(string fileName = "hard-v1.onnx")
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "LanPong", "Models", fileName);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"Test model {fileName} was not found.");
    }
}

internal sealed class TestBotFactory(string strategyId, BotSettingsKind kind,
    Func<BotDefinition, ILocalOpponentController> create) : IBotStrategyFactory
{
    public BotStrategyDescriptor Descriptor { get; } = new(strategyId, kind);
    public ConcurrentQueue<BotDefinition> Definitions { get; } = new();

    public ILocalOpponentController Create(BotDefinition entry)
    {
        Definitions.Enqueue(entry);
        return create(entry);
    }
}

internal sealed class TrackedBotController(int axis = -1) : ILocalOpponentController, IDisposable
{
    private int _resetCount;
    private int _axisCalls;
    private int _disposeCount;
    public int ResetCount => Volatile.Read(ref _resetCount);
    public int AxisCalls => Volatile.Read(ref _axisCalls);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public Func<int, bool>? FailReset { get; init; }
    public Func<int, bool>? FailAxis { get; init; }
    public Action<int>? OnReset { get; init; }
    public Action? OnDispose { get; init; }

    public void Reset()
    {
        var count = Interlocked.Increment(ref _resetCount);
        OnReset?.Invoke(count);
        if (FailReset?.Invoke(count) == true)
            throw new InvalidDataException("Synthetic reset failure at /private/internal/model.onnx.");
    }

    public int GetAxis(GameState state)
    {
        var count = Interlocked.Increment(ref _axisCalls);
        if (FailAxis?.Invoke(count) == true)
            throw new InvalidDataException("Synthetic inference failure at /private/internal/model.onnx.");
        return axis;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        OnDispose?.Invoke();
    }
}
