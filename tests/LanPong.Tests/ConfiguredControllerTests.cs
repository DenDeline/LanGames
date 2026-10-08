using System.Security.Cryptography;

namespace LanPong.Tests;

public sealed class ConfiguredControllerTests
{
    [Test]
    public async Task TrackerProfiles_UseOwnActivationAndObservationCadence()
    {
        var fast = new TrackerBotPolicy(new TrackerBotSettings(2, 0.6, 0, 0.01));
        var slow = new TrackerBotPolicy(new TrackerBotSettings(12, 0.9, 0, 0.01));
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
        var immediate = new TrackerBotPolicy(new TrackerBotSettings(1, 0.6, 0, 0.01));
        var predictive = new TrackerBotPolicy(new TrackerBotSettings(1, 0.6, 0.25, 0.01));
        var approach = Playing(10) with { RightY = 0.5, BallY = 0.45, BallVy = 0.4 };
        await Assert.That(immediate.GetAxis(approach)).IsEqualTo(-1);
        await Assert.That(predictive.GetAxis(approach)).IsEqualTo(1);

        var tolerant = new TrackerBotPolicy(new TrackerBotSettings(1, 0.6, 0, 0.1));
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
    public async Task OnnxFrozenModel_PreservesGoldenActionsAndSimulationCheckpointsAcrossReset()
    {
        using var bot = new OnnxLocalOpponentController(ModelSettings());
        // Captured from the frozen trained model before the policy extraction. Each byte is axis + 1.
        const string expectedActions = "85da313e4c95896e24fb267fceee03a2d423c7f6eb445f5ff0ea224cb3e2952b";
        var expectedCheckpoints = new[]
        {
            Checkpoint(150, 0.585, 0.5425, 0.8813235327914316, 0.6618837246824985,
                -0.571471873806511, 0.1000636515762252, 0, 0, GamePhase.Playing, 0, 1, 1, 144),
            Checkpoint(300, 0.6699999999999999, 0.5425, 0.5564591126770753, 0.8221493683016924,
                0.4658459952773357, -0.3273041483968756, 0, 0, GamePhase.Playing, 0, 1, 2, 270),
            Checkpoint(450, 0.7549999999999999, 0.5, 0.5, 0.5, 0, 0,
                0, 1, GamePhase.Countdown, 1.4158790793708074, -1, 0, 439),
            Checkpoint(600, 0.6699999999999999, 0.5, 0.5, 0.5, 0, 0,
                0, 2, GamePhase.Countdown, 1.437242533916263, -1, 0, 591)
        };
        for (var match = 0; match < 2; match++)
        {
            bot.Reset();
            var game = new GameEngine();
            game.StartMatch();
            var actions = new byte[600];
            for (var tick = 0; tick < actions.Length; tick++)
            {
                var axis = bot.GetAxis(game.Capture());
                actions[tick] = checked((byte)(axis + 1));
                game.Advance(GameConstants.FixedStepSeconds, tick % 36 < 18 ? 1 : -1, axis);
                if ((tick + 1) % 150 == 0)
                    await AssertCheckpoint(game.Capture(), expectedCheckpoints[(tick + 1) / 150 - 1]);
            }
            await Assert.That(Convert.ToHexString(SHA256.HashData(actions)).ToLowerInvariant())
                .IsEqualTo(expectedActions);
            await Assert.That(actions.Count(axis => axis == 0)).IsEqualTo(45);
            await Assert.That(actions.Count(axis => axis == 1)).IsEqualTo(510);
            await Assert.That(actions.Count(axis => axis == 2)).IsEqualTo(45);
        }
        await Assert.That(bot.ModelSha256).IsEqualTo(BotModelV1.ExpectedSha256);
    }

    [Test]
    public async Task OnnxMissingModelAndWrongChecksum_FailBeforeCreatingNativeSession()
    {
        var creations = 0;
        IOnnxInferenceSession CreateSession(string _) { creations++; return new FakeSession(0, 1, 0); }
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
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var session = new FakeSession(invalid, 0, 1);
            using var bot = new OnnxLocalOpponentController(InjectedSettings(), session);
            bot.Reset();
            await Assert.That(() => bot.GetAxis(Playing(9))).Throws<InvalidDataException>();
            await Assert.That(session.Disposals).IsEqualTo(0);
        }
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
        BotModelV1.ExpectedSha256, RightBotObservationV1.InferenceCadenceTicks);

    private static OnnxBotSettings InjectedSettings(int cadence = 9) => new("<injected>",
        BotModelV1.ExpectedSha256, cadence);

    private static GameState Playing(long tick) => new()
    {
        Phase = GamePhase.Playing, TickNumber = tick, LeftY = 0.5, RightY = 0.5,
        BallX = 0.8, BallY = 0.62, BallVx = 0.55, BallVy = 0.19
    };

    private static GameState Checkpoint(long tick, double leftY, double rightY, double ballX,
        double ballY, double ballVx, double ballVy, int leftScore, int rightScore, GamePhase phase,
        double countdown, int serveDirection, int hits, long lastEventTick) => new()
    {
        TickNumber = tick, LeftY = leftY, RightY = rightY, BallX = ballX, BallY = ballY,
        BallVx = ballVx, BallVy = ballVy, LeftScore = leftScore, RightScore = rightScore,
        Phase = phase, Countdown = countdown, RoundId = 1, ServeDirection = serveDirection,
        Hits = hits, LastEventTick = lastEventTick, EventOrdinal = 1
    };

    private static async Task AssertCheckpoint(GameState actual, GameState expected)
    {
        await Assert.That(actual.TickNumber).IsEqualTo(expected.TickNumber);
        await Assert.That(actual.LeftY).IsEqualTo(expected.LeftY).Within(1e-12);
        await Assert.That(actual.RightY).IsEqualTo(expected.RightY).Within(1e-12);
        await Assert.That(actual.BallX).IsEqualTo(expected.BallX).Within(1e-12);
        await Assert.That(actual.BallY).IsEqualTo(expected.BallY).Within(1e-12);
        await Assert.That(actual.BallVx).IsEqualTo(expected.BallVx).Within(1e-12);
        await Assert.That(actual.BallVy).IsEqualTo(expected.BallVy).Within(1e-12);
        await Assert.That(actual.LeftScore).IsEqualTo(expected.LeftScore);
        await Assert.That(actual.RightScore).IsEqualTo(expected.RightScore);
        await Assert.That(actual.Phase).IsEqualTo(expected.Phase);
        await Assert.That(actual.Countdown).IsEqualTo(expected.Countdown).Within(1e-12);
        await Assert.That(actual.RoundId).IsEqualTo(expected.RoundId);
        await Assert.That(actual.ServeDirection).IsEqualTo(expected.ServeDirection);
        await Assert.That(actual.Hits).IsEqualTo(expected.Hits);
        await Assert.That(actual.LastEventTick).IsEqualTo(expected.LastEventTick);
        await Assert.That(actual.EventOrdinal).IsEqualTo(expected.EventOrdinal);
    }

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

    private sealed class FakeSession(params float[] logits) : IOnnxInferenceSession
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
