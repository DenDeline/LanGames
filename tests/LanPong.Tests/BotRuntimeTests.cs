using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanPong.Tests;

public sealed class BotRuntimeTests
{
    [Test]
    public async Task Discovery_IsLazyAndTracksOnlyKnownModelStatus()
    {
        var factory = new RecordingFactory(BotSettingsKind.Onnx);
        var runtime = Runtime([Onnx("model"), Tracker("tracker"), Tracker("disabled", enabled: false)], factory,
            new TrackerBotStrategyFactory());

        await Assert.That(runtime.GetAvailability("model").State).IsEqualTo(BotAvailabilityState.NotChecked);
        await Assert.That(runtime.GetAvailability("tracker").State).IsEqualTo(BotAvailabilityState.Ready);
        await Assert.That(runtime.GetAvailability("disabled").State).IsEqualTo(BotAvailabilityState.Disabled);
        await Assert.That(factory.Created.Count).IsEqualTo(0);
        using var session = runtime.Prepare("model");
        await Assert.That(factory.Created.Count).IsEqualTo(1);
        await Assert.That(runtime.GetAvailability("model").State).IsEqualTo(BotAvailabilityState.Ready);
    }

    [Test]
    public async Task Factories_ResolveContentRootAndRestrictLegacyOverrideWithoutOpeningModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "lanpong-catalog-factory");
        var environment = new TestEnvironment { ContentRootPath = root };
        var factory = new OnnxBotStrategyFactory(environment, "diagnostics/missing.onnx");
        var catalog = Catalog([Onnx("trained"), Onnx("custom", path: "Models/custom.onnx"),
            Onnx("different-hash", checksum: new string('a', 64)),
            Onnx("absolute", path: Path.Combine(root, "absolute.onnx"))]);

        using var trained = (OnnxLocalOpponentController)factory.Create(Find(catalog, "trained"));
        using var custom = (OnnxLocalOpponentController)factory.Create(Find(catalog, "custom"));
        using var differentHash = (OnnxLocalOpponentController)factory.Create(Find(catalog, "different-hash"));
        using var absolute = (OnnxLocalOpponentController)factory.Create(Find(catalog, "absolute"));
        await Assert.That(trained.ModelPath).IsEqualTo(Path.Combine(root, "diagnostics/missing.onnx"));
        await Assert.That(custom.ModelPath).IsEqualTo(Path.Combine(root, "Models/custom.onnx"));
        await Assert.That(differentHash.ModelPath).IsEqualTo(Path.Combine(root, "Models/hard-v1.onnx"));
        await Assert.That(absolute.ModelPath).IsEqualTo(Path.Combine(root, "absolute.onnx"));
        await Assert.That(trained.ModelSha256).IsNull();
        await Assert.That(factory.Descriptor).IsEqualTo(new BotStrategyDescriptor("onnx", BotSettingsKind.Onnx));
        await Assert.That(new TrackerBotStrategyFactory().Descriptor)
            .IsEqualTo(new BotStrategyDescriptor("tracker", BotSettingsKind.Tracker));
    }

    [Test]
    public async Task SharedTrackerStrategy_UsesEntryTuningAndFreshMutableState()
    {
        var fast = Tracker("fast");
        fast.Tracker = new() { ObservationIntervalTicks = 1, ObservationActivationX = 0, LookAheadSeconds = 0,
            TargetDeadZone = 0 };
        var runtime = Runtime([Tracker("steady"), fast], new TrackerBotStrategyFactory());
        using var steady = runtime.Prepare("steady");
        using var quick = runtime.Prepare("fast");
        await Assert.That(steady.GetAxis(Playing(ballX: 0.4))).IsEqualTo(0);
        await Assert.That(quick.GetAxis(Playing(ballX: 0.4))).IsEqualTo(1);
        await Assert.That(quick.GetAxis(Playing(ballX: 0.4, ballY: 0.2, tick: 101))).IsEqualTo(-1);

        using var sameEntry = runtime.Prepare("steady");
        steady.Reset();
        await Assert.That(steady.GetAxis(Playing())).IsEqualTo(1);
        await Assert.That(sameEntry.GetAxis(Playing(ballY: 0.2, tick: 101))).IsEqualTo(-1);
        await Assert.That(steady.GetAxis(Playing(ballY: 0.2, tick: 101))).IsEqualTo(1);
        steady.Reset();
        await Assert.That(steady.GetAxis(Playing(ballY: 0.2, tick: 101))).IsEqualTo(-1);
    }

    [Test]
    public async Task Preparation_WarmsOnlySelectedChainIncludingOnnxBackups()
    {
        var tracker = new RecordingFactory(BotSettingsKind.Tracker);
        var onnx = new RecordingFactory(BotSettingsKind.Onnx);
        var runtime = Runtime([Tracker("primary", fallback: "backup"), Onnx("backup", fallback: "last"),
            Tracker("last"), Onnx("unselected")], tracker, onnx);
        using var session = runtime.Prepare("primary");

        await Assert.That(tracker.Created.Select(entry => entry.Id).ToArray()).IsEquivalentTo(["primary", "last"]);
        await Assert.That(onnx.Created.Single().Id).IsEqualTo("backup");
        await Assert.That(tracker.Controllers.All(controller => controller.ResetCount == 1)).IsTrue();
        await Assert.That(onnx.Controllers.Single().ResetCount).IsEqualTo(1);
        await Assert.That(session.Requested.Id).IsEqualTo("primary");
        await Assert.That(session.Effective.Id).IsEqualTo("primary");
        await Assert.That(session.FallbackReason).IsNull();
        await Assert.That(runtime.GetAvailability("unselected").State).IsEqualTo(BotAvailabilityState.NotChecked);
    }

    [Test]
    public async Task PreparationFailure_UsesExplicitTargetIdentityAndDisposesFailedController()
    {
        var factory = new RecordingFactory(BotSettingsKind.Tracker,
            entry => new ProbeController { ThrowOnResetNumber = entry.Id == "primary" ? 1 : null });
        var runtime = Runtime([Tracker("primary", fallback: "target"), Tracker("target")], factory);
        using var session = runtime.Prepare("primary");

        await Assert.That(session.Requested.Id).IsEqualTo("primary");
        await Assert.That(session.Effective.Id).IsEqualTo("target");
        await Assert.That(session.FallbackReason).IsEqualTo("The bot could not be prepared.");
        await Assert.That(factory.Controllers[0].DisposeCount).IsEqualTo(1);
        await Assert.That(factory.Controllers[1].DisposeCount).IsEqualTo(0);
        await Assert.That(runtime.GetAvailability("primary").State).IsEqualTo(BotAvailabilityState.Unavailable);
        using var retry = runtime.Prepare("primary");
        await Assert.That(factory.Created.Count(entry => entry.Id == "primary")).IsEqualTo(1);
        await Assert.That(retry.Effective.Id).IsEqualTo("target");
    }

    [Test]
    public async Task UnusableBackup_DoesNotRejectUsableRequestedBot()
    {
        var factory = new RecordingFactory(BotSettingsKind.Tracker,
            entry => new ProbeController { ThrowOnResetNumber = entry.Id == "backup" ? 1 : null });
        var runtime = Runtime([Tracker("primary", fallback: "backup"), Tracker("backup")], factory);
        using var session = runtime.Prepare("primary");
        await Assert.That(session.Effective.Id).IsEqualTo("primary");
        await Assert.That(session.FallbackReason).IsNull();
        await Assert.That(session.IsPlayable).IsTrue();
        await Assert.That(runtime.GetAvailability("backup").State).IsEqualTo(BotAvailabilityState.Unavailable);
    }

    [Test]
    public async Task MissingAndUnverifiedModels_AreSanitizedAndDoNotLoadHiddenFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lanpong-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "wrong.onnx");
            File.WriteAllBytes(file, [1, 2, 3]);
            var runtime = Runtime([Onnx("missing", path: "secret/missing.onnx"),
                Onnx("wrong", path: "wrong.onnx", fallback: "target"), Tracker("target")],
                new OnnxBotStrategyFactory(new TestEnvironment { ContentRootPath = root }),
                new TrackerBotStrategyFactory());
            var missing = Capture(() => runtime.Prepare("missing"));
            await Assert.That(missing is BotUnavailableException).IsTrue();
            await Assert.That(missing!.Message).IsEqualTo("The configured model is missing.");
            await Assert.That(runtime.GetAvailability("missing").Reason).IsEqualTo(missing.Message);
            using var wrong = runtime.Prepare("wrong");
            await Assert.That(wrong.Effective.Id).IsEqualTo("target");
            await Assert.That(wrong.FallbackReason).IsEqualTo("The configured model could not be verified.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task RejectedSelection_DoesNotCreateAnyController()
    {
        var factory = new RecordingFactory(BotSettingsKind.Tracker);
        var runtime = Runtime([Tracker("active"), Tracker("disabled", enabled: false)], factory);
        await Assert.That(Capture(() => runtime.Prepare("unknown")) is ArgumentException).IsTrue();
        await Assert.That(Capture(() => runtime.Prepare("ACTIVE")) is ArgumentException).IsTrue();
        await Assert.That(Capture(() => runtime.Prepare("disabled")) is InvalidOperationException).IsTrue();
        await Assert.That(factory.Created.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RuntimeFailure_UsesAlreadyPreparedOnnxBackupWithoutResetOrDisposal()
    {
        var tracker = new RecordingFactory(BotSettingsKind.Tracker,
            _ => new ProbeController { ThrowOnAxis = true });
        var onnx = new RecordingFactory(BotSettingsKind.Onnx, _ => new ProbeController { Axis = -1 });
        var runtime = Runtime([Tracker("primary", fallback: "backup"), Onnx("backup")], tracker, onnx);
        using var session = runtime.Prepare("primary");
        await Assert.That(session.GetAxis(Playing())).IsEqualTo(-1);
        await Assert.That(session.Effective.Id).IsEqualTo("backup");
        await Assert.That(session.FallbackReason).IsEqualTo("The bot stopped responding.");
        await Assert.That(tracker.Controllers.Single().DisposeCount).IsEqualTo(0);
        await Assert.That(onnx.Controllers.Single().ResetCount).IsEqualTo(1);
        await Assert.That(runtime.GetAvailability("primary").Reason).IsEqualTo(session.FallbackReason);
        BotRuntime.DisposeRetired(session.TakeRetiredControllers());
        await Assert.That(tracker.Controllers.Single().DisposeCount).IsEqualTo(1);
        await Assert.That(session.TakeRetiredControllers()).IsNull();
        session.Reset();
        await Assert.That(onnx.Created.Count).IsEqualTo(1);
        await Assert.That(onnx.Controllers.Single().ResetCount).IsEqualTo(2);
        await Assert.That(session.Effective.Id).IsEqualTo("backup");
        await Assert.That(session.FallbackReason).IsEqualTo("The bot stopped responding.");
    }

    [Test]
    public async Task RuntimeFailure_ExhaustsExplicitChainAndPreservesLastIdentityWithoutThrowing()
    {
        var factory = new RecordingFactory(BotSettingsKind.Tracker, _ => new ProbeController { ThrowOnAxis = true });
        var runtime = Runtime([Tracker("primary", fallback: "last"), Tracker("last"), Tracker("unrelated")], factory);
        using var session = runtime.Prepare("primary");
        await Assert.That(session.GetAxis(Playing())).IsEqualTo(0);
        await Assert.That(session.IsPlayable).IsFalse();
        await Assert.That(session.Effective.Id).IsEqualTo("last");
        await Assert.That(session.GetAxis(Playing())).IsEqualTo(0);
        await Assert.That(factory.Created.Any(entry => entry.Id == "unrelated")).IsFalse();
        await Assert.That(factory.Controllers.All(controller => controller.DisposeCount == 0)).IsTrue();
        var retired = session.TakeRetiredControllers();
        await Assert.That(retired!.Count).IsEqualTo(2);
        BotRuntime.DisposeRetired(retired);
        session.Dispose();
        session.Dispose();
        await Assert.That(factory.Controllers.All(controller => controller.DisposeCount == 1)).IsTrue();
    }

    [Test]
    public async Task RematchResetFailure_AdvancesToResetBackupAndDefersCleanup()
    {
        var factory = new RecordingFactory(BotSettingsKind.Tracker,
            entry => new ProbeController { ThrowOnResetNumber = entry.Id == "primary" ? 2 : null });
        var runtime = Runtime([Tracker("primary", fallback: "backup"), Tracker("backup")], factory);
        using var session = runtime.Prepare("primary");
        session.Reset();
        await Assert.That(session.IsPlayable).IsTrue();
        await Assert.That(session.Effective.Id).IsEqualTo("backup");
        await Assert.That(factory.Controllers[0].DisposeCount).IsEqualTo(0);
        await Assert.That(factory.Controllers[1].ResetCount).IsEqualTo(2);
        session.Reset();
        await Assert.That(factory.Controllers[0].ResetCount).IsEqualTo(2);
        await Assert.That(factory.Controllers[1].ResetCount).IsEqualTo(3);
        BotRuntime.DisposeRetired(session.TakeRetiredControllers());
        await Assert.That(factory.Controllers[0].DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResetFailureWithoutFallback_IsStoppedAndCanDisposeSafely()
    {
        var factory = new RecordingFactory(BotSettingsKind.Tracker,
            _ => new ProbeController { ThrowOnResetNumber = 2 });
        var runtime = Runtime([Tracker("primary")], factory);
        using var session = runtime.Prepare("primary");
        session.Reset();
        await Assert.That(session.IsPlayable).IsFalse();
        await Assert.That(session.Effective.Id).IsEqualTo("primary");
        await Assert.That(session.GetAxis(Playing())).IsEqualTo(0);
        session.Dispose();
        await Assert.That(factory.Controllers.Single().DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task SessionDisposal_IsIdempotentAndContinuesAfterCleanupFailure()
    {
        var factory = new RecordingFactory(BotSettingsKind.Tracker,
            _ => new ProbeController { ThrowOnDispose = true });
        var runtime = Runtime([Tracker("primary", fallback: "backup"), Tracker("backup")], factory);
        var session = runtime.Prepare("primary");
        session.Dispose();
        session.Dispose();
        await Assert.That(factory.Controllers.All(controller => controller.DisposeCount == 1)).IsTrue();
        await Assert.That(session.IsPlayable).IsFalse();
    }

    [Test]
    public async Task Constructor_RejectsMissingDuplicateAndMismatchedFactoriesBeforePreparation()
    {
        var catalog = Catalog([Tracker("primary")]);
        await Assert.That(Capture(() => new BotRuntime(catalog, [], NullLogger<BotRuntime>.Instance)) is InvalidOperationException)
            .IsTrue();
        await Assert.That(Capture(() => new BotRuntime(catalog,
            [new TrackerBotStrategyFactory(), new TrackerBotStrategyFactory()], NullLogger<BotRuntime>.Instance))
            is InvalidOperationException).IsTrue();
        var mismatch = new RecordingFactory(BotSettingsKind.Onnx, id: "tracker");
        await Assert.That(Capture(() => new BotRuntime(catalog, [mismatch], NullLogger<BotRuntime>.Instance))
            is InvalidOperationException).IsTrue();
        await Assert.That(mismatch.Created.Count).IsEqualTo(0);
    }

    private static BotRuntime Runtime(List<BotEntryOptions> entries, params IBotStrategyFactory[] factories) =>
        new(Catalog(entries), factories, NullLogger<BotRuntime>.Instance);

    private static BotCatalog Catalog(List<BotEntryOptions> entries)
    {
        var options = new BotsOptions { DefaultBotId = entries.First(entry => entry.Enabled).Id, Entries = entries };
        var registry = new BotStrategyRegistry([new("tracker", BotSettingsKind.Tracker), new("onnx", BotSettingsKind.Onnx)]);
        var validation = new BotsOptionsValidator(registry).Validate(null, options);
        if (!validation.Succeeded) throw new InvalidOperationException(string.Join('\n', validation.Failures ?? []));
        return new(Options.Create(options));
    }

    private static BotDefinition Find(BotCatalog catalog, string id)
    {
        catalog.TryGet(id, out var definition);
        return definition!;
    }

    private static BotEntryOptions Tracker(string id, string? fallback = null, bool enabled = true) => new()
    {
        Id = id, Name = id, Description = "A configured policy", Style = "Tracker", Difficulty = "Practice",
        Category = "Original", StrategyId = "tracker", Tracker = new(), FallbackBotId = fallback, Enabled = enabled
    };

    private static BotEntryOptions Onnx(string id, string? fallback = null, string path = "Models/hard-v1.onnx",
        string checksum = HardLocalOpponentController.ExpectedModelSha256) => new()
    {
        Id = id, Name = id, Description = "A configured policy", Style = "Prediction", Difficulty = "Challenge",
        Category = "Original", StrategyId = "onnx", Onnx = new() { ModelPath = path, ExpectedSha256 = checksum },
        FallbackBotId = fallback
    };

    private static GameState Playing(double ballX = 0.8, double ballY = 0.8, long tick = 100) => new()
    {
        LeftY = 0.5, RightY = 0.5, BallX = ballX, BallY = ballY, BallVx = 0.55, BallVy = 0,
        Phase = GamePhase.Playing, TickNumber = tick, RecentEvents = GameEventHistory.Empty
    };

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class RecordingFactory(BotSettingsKind kind, Func<BotDefinition, ProbeController>? create = null,
        string? id = null) : IBotStrategyFactory
    {
        public BotStrategyDescriptor Descriptor { get; } = new(id ?? (kind == BotSettingsKind.Tracker ? "tracker" : "onnx"), kind);
        public List<BotDefinition> Created { get; } = [];
        public List<ProbeController> Controllers { get; } = [];
        public ILocalOpponentController Create(BotDefinition entry)
        {
            Created.Add(entry);
            var controller = create?.Invoke(entry) ?? new ProbeController();
            Controllers.Add(controller);
            return controller;
        }
    }

    private sealed class ProbeController : ILocalOpponentController, IDisposable
    {
        public int Axis { get; init; } = 1;
        public bool ThrowOnAxis { get; init; }
        public int? ThrowOnResetNumber { get; init; }
        public bool ThrowOnDispose { get; init; }
        public int ResetCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void Reset()
        {
            ResetCount++;
            if (ResetCount == ThrowOnResetNumber)
                throw new InvalidOperationException("Sensitive /private/model/path native detail");
        }
        public int GetAxis(GameState state) => ThrowOnAxis
            ? throw new InvalidOperationException("Sensitive /private/model/path native detail") : Axis;
        public void Dispose()
        {
            DisposeCount++;
            if (ThrowOnDispose) throw new InvalidOperationException("Native cleanup failed");
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "LanPong";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
