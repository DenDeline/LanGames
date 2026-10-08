using System.Security.Cryptography;

namespace LanPong.Tests;

public sealed class ConfiguredControllerTests
{
    [Test]
    public async Task TrackerProfiles_UseOwnActivationAndObservationCadence()
    {
        var fast = new SimpleLocalOpponentController(new TrackerBotSettings(2, 0.6, 0, 0.01));
        var slow = new SimpleLocalOpponentController(new TrackerBotSettings(12, 0.9, 0, 0.01));
        var approach = Playing(100) with { BallX = 0.7, BallY = 0.8 };

        await Assert.That(fast.GetAxis(approach)).IsEqualTo(1);
        await Assert.That(slow.GetAxis(approach)).IsEqualTo(0);
        var changed = approach with { TickNumber = 102, BallX = 0.92, BallY = 0.2 };
        await Assert.That(fast.GetAxis(changed)).IsEqualTo(-1);
        await Assert.That(slow.GetAxis(changed)).IsEqualTo(0);
        await Assert.That(slow.GetAxis(changed with { TickNumber = 112 })).IsEqualTo(-1);

        // Resetting one profile cannot disturb the sampled state of the other.
        fast.Reset();
        await Assert.That(fast.GetAxis(approach with { TickNumber = 113 })).IsEqualTo(1);
        await Assert.That(slow.GetAxis(approach with { TickNumber = 113 })).IsEqualTo(-1);
    }

    [Test]
    public async Task TrackerSettings_ApplyLookaheadAndDeadZone()
    {
        var immediate = new SimpleLocalOpponentController(new TrackerBotSettings(1, 0.6, 0, 0.01));
        var predictive = new SimpleLocalOpponentController(new TrackerBotSettings(1, 0.6, 0.25, 0.01));
        var approach = Playing(10) with { RightY = 0.5, BallY = 0.45, BallVy = 0.4 };
        await Assert.That(immediate.GetAxis(approach)).IsEqualTo(-1);
        await Assert.That(predictive.GetAxis(approach)).IsEqualTo(1);

        var tolerant = new SimpleLocalOpponentController(new TrackerBotSettings(1, 0.6, 0, 0.1));
        await Assert.That(tolerant.GetAxis(approach)).IsEqualTo(0);
    }

    [Test]
    public async Task OnnxPreparation_IsLazyAndResetRetainsWarmedSession()
    {
        var settings = ModelSettings();
        var creations = 0;
        var session = new FakeSession(0, 0, 1);
        using var bot = new OnnxLocalOpponentController(settings, path =>
        {
            creations++;
            if (path != settings.ModelPath) throw new InvalidOperationException("Unexpected model path.");
            return session;
        });

        await Assert.That(creations).IsEqualTo(0);
        await Assert.That(bot.ModelSha256).IsNull();
        await Assert.That(() => bot.GetAxis(Playing(9))).Throws<InvalidOperationException>();
        await Assert.That(creations).IsEqualTo(0);

        bot.Reset();
        await Assert.That(creations).IsEqualTo(1);
        await Assert.That(bot.ModelSha256).IsEqualTo(settings.ExpectedSha256);
        await Assert.That(bot.GetAxis(Playing(9))).IsEqualTo(1);
        bot.Reset();
        await Assert.That(creations).IsEqualTo(1);
        await Assert.That(session.Disposals).IsEqualTo(0);
        await Assert.That(bot.GetAxis(Playing(10))).IsEqualTo(0);
        await Assert.That(session.Runs).IsEqualTo(1);

        bot.Dispose();
        bot.Dispose();
        await Assert.That(session.Disposals).IsEqualTo(1);
        await Assert.That(() => bot.Reset()).Throws<ObjectDisposedException>();
        await Assert.That(() => bot.GetAxis(Playing(18))).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task OnnxConfiguredCadence_UsesGlobalTicksAndCountdownClearsHeldAxis()
    {
        var session = new FakeSession(0, 0, 2);
        using var bot = new OnnxLocalOpponentController(InjectedSettings(cadence: 4), session);
        bot.Reset();

        await Assert.That(bot.GetAxis(Playing(3))).IsEqualTo(0);
        await Assert.That(bot.GetAxis(Playing(4))).IsEqualTo(1);
        session.Set(2, 0, 0);
        await Assert.That(bot.GetAxis(Playing(5))).IsEqualTo(1);
        await Assert.That(bot.GetAxis(Playing(6) with { Phase = GamePhase.Countdown })).IsEqualTo(0);
        await Assert.That(bot.GetAxis(Playing(7))).IsEqualTo(0);
        await Assert.That(bot.GetAxis(Playing(8))).IsEqualTo(-1);
        await Assert.That(session.Runs).IsEqualTo(2);
        await Assert.That(session.LastObservation!.SequenceEqual(RightBotObservationV1.Encode(Playing(8))))
            .IsTrue();

        await Assert.That(bot.GetAxis(Playing(12) with { Phase = GamePhase.GameOver })).IsEqualTo(0);
        await Assert.That(bot.GetAxis(Playing(16) with { Phase = GamePhase.Waiting })).IsEqualTo(0);
        await Assert.That(session.Runs).IsEqualTo(2);
    }

    [Test]
    public async Task OnnxEqualLogits_PreserveTrainedStayPreference()
    {
        var session = new FakeSession(1, 1, 1);
        using var bot = new OnnxLocalOpponentController(InjectedSettings(), session);
        bot.Reset();
        await Assert.That(bot.GetAxis(Playing(9))).IsEqualTo(0);
    }

    [Test]
    public async Task OnnxDefaultProfile_ExactlyMatchesLegacyTrainedActionsAcrossReplayAndReset()
    {
        var settings = ModelSettings();
        using var configured = new OnnxLocalOpponentController(settings);
        using var legacy = new HardLocalOpponentController(settings.ModelPath);
        for (var match = 0; match < 2; match++)
        {
            configured.Reset();
            legacy.Reset();
            var game = new GameEngine();
            game.StartMatch();
            for (var tick = 0; tick < 600; tick++)
            {
                var state = game.Capture();
                var actual = configured.GetAxis(state);
                await Assert.That(actual).IsEqualTo(legacy.GetAxis(state));
                game.Advance(GameConstants.FixedStepSeconds, tick % 36 < 18 ? 1 : -1, actual);
            }
        }
        await Assert.That(legacy.IsFallbackActive).IsFalse();
        await Assert.That(configured.ModelSha256).IsEqualTo(HardLocalOpponentController.ExpectedModelSha256);
    }

    [Test]
    public async Task OnnxMissingModelAndWrongChecksum_FailBeforeCreatingNativeSession()
    {
        var creations = 0;
        IHardInferenceSession CreateSession(string _) { creations++; return new FakeSession(0, 1, 0); }
        var missing = Path.Combine(Path.GetTempPath(), $"missing-bot-{Guid.NewGuid():N}.onnx");
        using var missingBot = new OnnxLocalOpponentController(ModelSettings() with { ModelPath = missing },
            CreateSession);
        await Assert.That(() => missingBot.Reset()).Throws<FileNotFoundException>();
        await Assert.That(() => missingBot.GetAxis(Playing(9))).Throws<InvalidOperationException>();

        using var wrongDigest = new OnnxLocalOpponentController(ModelSettings() with
            { ExpectedSha256 = new string('0', 64) }, CreateSession);
        await Assert.That(() => wrongDigest.Reset()).Throws<InvalidDataException>();
        await Assert.That(() => wrongDigest.GetAxis(Playing(9))).Throws<InvalidOperationException>();
        await Assert.That(creations).IsEqualTo(0);
    }

    [Test]
    public async Task OnnxWrongShape_FailsPreparationWithoutHiddenFallback()
    {
        var path = ModelPath("aot-smoke.onnx");
        using var file = File.OpenRead(path);
        var checksum = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        using var bot = new OnnxLocalOpponentController(new OnnxBotSettings(path, checksum, 9));
        await Assert.That(() => bot.Reset()).Throws<InvalidDataException>();
        await Assert.That(() => bot.GetAxis(Playing(9))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task OnnxRuntimeFailure_ThrowsAndLeavesNativeCleanupToOwner()
    {
        var session = new FakeSession(0, 0, 1) { ThrowOnRun = true };
        using var bot = new OnnxLocalOpponentController(InjectedSettings(), session);
        bot.Reset();
        await Assert.That(() => bot.GetAxis(Playing(9))).Throws<InvalidOperationException>();
        // Gameplay runs under the state lock; native disposal belongs to the owner's transition path.
        await Assert.That(session.Disposals).IsEqualTo(0);
        bot.Dispose();
        bot.Dispose();
        await Assert.That(session.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task OnnxNonFiniteLogits_ThrowWithoutChoosingAnotherPolicy()
    {
        var session = new FakeSession(float.NaN, 0, 1);
        using var bot = new OnnxLocalOpponentController(InjectedSettings(), session);
        bot.Reset();
        await Assert.That(() => bot.GetAxis(Playing(9))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task OnnxDispose_WhenSessionDisposalThrows_DetachesAndNeverRetries()
    {
        var session = new FakeSession(0, 1, 0) { ThrowOnDispose = true };
        var bot = new OnnxLocalOpponentController(InjectedSettings(), session);
        bot.Reset();
        await Assert.That(() => bot.Dispose()).Throws<InvalidOperationException>();
        bot.Dispose();
        await Assert.That(session.Disposals).IsEqualTo(1);
        await Assert.That(() => bot.Reset()).Throws<ObjectDisposedException>();
    }

    private static OnnxBotSettings ModelSettings() => new(ModelPath("hard-v1.onnx"),
        HardLocalOpponentController.ExpectedModelSha256, RightBotObservationV1.InferenceCadenceTicks);

    private static OnnxBotSettings InjectedSettings(int cadence = 9) => new("<injected>",
        HardLocalOpponentController.ExpectedModelSha256, cadence);

    private static GameState Playing(long tick) => new()
    {
        Phase = GamePhase.Playing, TickNumber = tick, LeftY = 0.5, RightY = 0.5,
        BallX = 0.8, BallY = 0.62, BallVx = 0.55, BallVy = 0.19
    };

    private static string ModelPath(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "LanPong", "Models", fileName);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"Test model {fileName} was not found.");
    }

    private sealed class FakeSession(params float[] logits) : IHardInferenceSession
    {
        public int Runs { get; private set; }
        public int Disposals { get; private set; }
        public float[]? LastObservation { get; private set; }
        public bool ThrowOnRun { get; set; }
        public bool ThrowOnDispose { get; set; }

        public void Set(float up, float stay, float down)
        {
            logits[0] = up;
            logits[1] = stay;
            logits[2] = down;
        }

        public void Run(ReadOnlySpan<float> observation, Span<float> output)
        {
            Runs++;
            if (ThrowOnRun) throw new InvalidOperationException("inference failed");
            LastObservation = observation.ToArray();
            logits.AsSpan().CopyTo(output);
        }

        public void Dispose()
        {
            Disposals++;
            if (ThrowOnDispose) throw new InvalidOperationException("dispose failed");
        }
    }
}
