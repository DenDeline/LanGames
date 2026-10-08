using System.Security.Cryptography;
using LanPong.Bots.Catalog;
using LanPong.Bots.Configuration;
using LanPong.Bots.Inference;
using LanPong.Bots.Runtime;
using LanPong.Bots.Strategies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LanPong.Tests;

public sealed class BotSidePerspectiveTests
{
    [Test]
    public async Task PolicyView_ReflectsOnlyHorizontalInputsAndPreservesCanonicalWorld()
    {
        var events = GameEventHistory.FromArray([new("goal", GameEventKind.Goal, 87, 0.99, 0.7)]);
        var state = Playing(123) with
        {
            LeftY = 0.21, RightY = 0.78, BallX = 0.16, BallY = 0.67, BallVx = -0.61,
            BallVy = 0.23, LeftScore = 2, RightScore = 5, Countdown = 0.75, RoundId = 7,
            ServeDirection = -1, Hits = 4, LastEventTick = 87, EventOrdinal = 3, RecentEvents = events
        };
        var game = new GameEngine();
        game.Restore(state);

        var view = BotPolicyView.ForSide(state, PaddleSide.Left);

        await Assert.That(view).IsEqualTo(state with
        {
            LeftY = 0.78, RightY = 0.21, BallX = 0.84, BallVx = 0.61,
            LeftScore = 5, RightScore = 2, ServeDirection = 1
        });
        await Assert.That(ReferenceEquals(view.RecentEvents, events)).IsTrue();
        await Assert.That(game.Capture()).IsEqualTo(state);
        await Assert.That(BotPolicyView.ForSide(state, PaddleSide.Right)).IsEqualTo(state);
        foreach (var invalid in new[] { default(PaddleSide), (PaddleSide)(-1), (PaddleSide)3 })
            await Assert.That(() => BotPolicyView.ForSide(state, invalid))
                .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PreparedChain_UsesOnePolicyViewAndRetainsFallbackIdentityAndLifecycle()
    {
        var primary = new RecordingPolicy { Fail = true };
        var backup = new RecordingPolicy { Axis = -1 };
        var factory = new TestBotFactory("tracker", BotSettingsKind.Tracker,
            entry => entry.Id == "primary" ? primary : backup);
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Tracker("primary", fallback: "backup"), BotTestSupport.Tracker("backup")
        ], factory);
        var session = runtime.Prepare("primary");
        var world = Playing(90) with
        {
            LeftY = 0.23, RightY = 0.76, BallX = 0.19, BallVx = -0.6,
            LeftScore = 1, RightScore = 4, ServeDirection = -1
        };
        var expectedView = world with
        {
            LeftY = 0.76, RightY = 0.23, BallX = 0.81, BallVx = 0.6,
            LeftScore = 4, RightScore = 1, ServeDirection = 1
        };
        try
        {
            await Assert.That(() => session.GetAxis(world, default)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(primary.States.Count).IsEqualTo(0);
            await Assert.That(session.IsPlayable).IsTrue();
            await Assert.That(session.GetAxis(world, PaddleSide.Left)).IsEqualTo(-1);
            await Assert.That(primary.States.Single()).IsEqualTo(expectedView);
            await Assert.That(backup.States.Single()).IsEqualTo(expectedView);
            await Assert.That(session.Requested.Id).IsEqualTo("primary");
            await Assert.That(session.Effective.Id).IsEqualTo("backup");
            await Assert.That(session.FallbackReason).IsEqualTo("Бот перестал отвечать.");
            await Assert.That(session.VerifiedModelSha256).IsNull();
            await Assert.That(primary.DisposeCount).IsEqualTo(0);
            await Assert.That(backup.ResetCount).IsEqualTo(1);

            // Side selection changes inputs only; no policy is rebuilt/reset during a decision.
            await Assert.That(session.GetAxis(world with { TickNumber = 91 }, PaddleSide.Right)).IsEqualTo(-1);
            await Assert.That(backup.States.Last()).IsEqualTo(world with { TickNumber = 91 });
            await Assert.That(factory.Definitions.Count).IsEqualTo(2);
            session.Reset();
            await Assert.That(primary.ResetCount).IsEqualTo(1);
            await Assert.That(backup.ResetCount).IsEqualTo(2);
            await Assert.That(session.Effective.Id).IsEqualTo("backup");
            BotRuntime.DisposeRetired(session.TakeRetiredControllers());
            await Assert.That(primary.DisposeCount).IsEqualTo(1);
        }
        finally
        {
            session.Dispose();
            session.Dispose();
        }
        await Assert.That(primary.DisposeCount).IsEqualTo(1);
        await Assert.That(backup.DisposeCount).IsEqualTo(1);
        await Assert.That(() => session.GetAxis(world, PaddleSide.Left)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ShippedTrackerProfiles_LeftViewRetainsApproachWallsAndConfiguredCadence()
    {
        await using var app = ConfiguredApplication();
        var runtime = app.Services.GetRequiredService<BotRuntime>();
        foreach (var (id, cadence, earlyAxis) in new[] { ("lada", 9, 0), ("iskra", 5, 1) })
        {
            using var left = runtime.Prepare(id);
            using var right = runtime.Prepare(id);
            await Assert.That(left.Effective.Tracker!.ObservationIntervalTicks).IsEqualTo(cadence);
            var scenarios = new[]
            {
                (Playing(90) with { BallY = 0.8 }, 1),
                (Playing(90) with { BallX = 0.6, BallY = 0.8 }, earlyAxis),
                (Playing(90) with { RightY = 0.8, BallVx = -0.55 }, -1),
                (Playing(90) with { RightY = 0.91, BallY = 0.98, BallVy = 0.5 }, -1),
                (Playing(90) with { RightY = 0.09, BallY = 0.02, BallVy = -0.5 }, 1)
            };
            foreach (var (canonical, expectedAxis) in scenarios)
            {
                left.Reset();
                right.Reset();
                await Assert.That(right.GetAxis(canonical, PaddleSide.Right)).IsEqualTo(expectedAxis);
                await Assert.That(left.GetAxis(PhysicalLeftInput(canonical), PaddleSide.Left))
                    .IsEqualTo(expectedAxis);
            }

            left.Reset();
            right.Reset();
            for (var elapsed = 0; elapsed <= cadence; elapsed++)
            {
                var canonical = Playing(90 + elapsed) with { BallY = elapsed == 0 ? 0.8 : 0.2 };
                var expected = elapsed < cadence ? 1 : -1;
                await Assert.That(left.GetAxis(PhysicalLeftInput(canonical), PaddleSide.Left)).IsEqualTo(expected);
                await Assert.That(right.GetAxis(canonical, PaddleSide.Right)).IsEqualTo(expected);
            }
            var finished = Playing(150) with { Phase = GamePhase.GameOver };
            await Assert.That(left.GetAxis(PhysicalLeftInput(finished), PaddleSide.Left)).IsEqualTo(0);
            await Assert.That(left.Requested.Id).IsEqualTo(id);
            await Assert.That(left.Effective.Id).IsEqualTo(id);
            await Assert.That(left.FallbackReason).IsNull();
        }
    }

    [Test]
    public async Task ShippedOnnx_LeftPerspectiveMatchesFrozenRightGoldenWithoutReflectingSimulation()
    {
        OnnxRuntimeStartup.DisablePosixTelemetry();
        await using var app = ConfiguredApplication();
        var runtime = app.Services.GetRequiredService<BotRuntime>();
        using var left = runtime.Prepare("vektor");
        using var right = runtime.Prepare("vektor");
        // Same fixed right-side action trace as the pre-extraction model golden. Only policy input
        // is mirrored; the canonical engine below is always advanced with its original right axis.
        const string expectedActions = "85da313e4c95896e24fb267fceee03a2d423c7f6eb445f5ff0ea224cb3e2952b";
        for (var reset = 0; reset < 2; reset++)
        {
            left.Reset();
            right.Reset();
            var game = new GameEngine();
            game.StartMatch();
            var actions = new byte[600];
            for (var tick = 0; tick < actions.Length; tick++)
            {
                var canonical = game.Capture();
                var rightAxis = right.GetAxis(canonical, PaddleSide.Right);
                var leftAxis = left.GetAxis(PhysicalLeftInput(canonical), PaddleSide.Left);
                await Assert.That(leftAxis).IsEqualTo(rightAxis);
                await Assert.That(leftAxis is >= -1 and <= 1).IsTrue();
                await Assert.That(game.Capture()).IsEqualTo(canonical);
                actions[tick] = checked((byte)(leftAxis + 1));
                game.Advance(GameConstants.FixedStepSeconds, tick % 36 < 18 ? 1 : -1, rightAxis);
            }
            await Assert.That(Convert.ToHexString(SHA256.HashData(actions)).ToLowerInvariant())
                .IsEqualTo(expectedActions);
            foreach (var session in new[] { left, right })
            {
                await Assert.That(session.Requested.Id).IsEqualTo("vektor");
                await Assert.That(session.Effective.Id).IsEqualTo("vektor");
                await Assert.That(session.IsPlayable).IsTrue();
                await Assert.That(session.FallbackReason).IsNull();
                await Assert.That(session.VerifiedModelSha256).IsEqualTo(BotModelV1.ExpectedSha256);
            }
        }
    }

    [Test]
    public async Task OnnxSideChanges_PreserveObservationSchemaCadenceAndWarmedOwnership()
    {
        var inference = new RecordingInference();
        var creations = 0;
        var factory = new TestBotFactory("onnx", BotSettingsKind.Onnx, entry =>
            new OnnxLocalOpponentController(entry.Onnx!, _ => { creations++; return inference; }));
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Onnx(path: BotTestSupport.ModelPath())
        ], factory);
        var session = runtime.Prepare("model");
        var leftWorld = Playing(9) with
        {
            LeftY = 0.21, RightY = 0.77, BallX = 0.16, BallVx = -0.6,
            LeftScore = 2, RightScore = 5, ServeDirection = -1
        };
        try
        {
            await Assert.That(session.GetAxis(leftWorld, PaddleSide.Left)).IsEqualTo(1);
            await Assert.That(inference.Observations.Single().SequenceEqual(RightBotObservationV1.Encode(
                leftWorld with
                {
                    LeftY = 0.77, RightY = 0.21, BallX = 0.84, BallVx = 0.6,
                    LeftScore = 5, RightScore = 2, ServeDirection = 1
                }))).IsTrue();
            inference.Axis = -1;
            await Assert.That(session.GetAxis(leftWorld with { TickNumber = 10 }, PaddleSide.Left)).IsEqualTo(1);
            await Assert.That(inference.Observations.Count).IsEqualTo(1);
            var rightWorld = Playing(18);
            await Assert.That(session.GetAxis(rightWorld, PaddleSide.Right)).IsEqualTo(-1);
            await Assert.That(inference.Observations.Last().SequenceEqual(RightBotObservationV1.Encode(rightWorld)))
                .IsTrue();
            session.Reset();
            await Assert.That(session.GetAxis(leftWorld with { TickNumber = 19 }, PaddleSide.Left)).IsEqualTo(0);
            await Assert.That(session.GetAxis(leftWorld with { TickNumber = 27 }, PaddleSide.Left)).IsEqualTo(-1);
            await Assert.That(creations).IsEqualTo(1);
            await Assert.That(factory.Definitions.Count).IsEqualTo(1);
            await Assert.That(inference.DisposeCount).IsEqualTo(0);
            await Assert.That(session.VerifiedModelSha256).IsEqualTo(BotModelV1.ExpectedSha256);
        }
        finally
        {
            session.Dispose();
            session.Dispose();
        }
        await Assert.That(inference.DisposeCount).IsEqualTo(1);
    }

    private static WebApplication ConfiguredApplication()
    {
        var root = Directory.GetParent(BotTestSupport.ModelPath())!.Parent!.FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production", ContentRootPath = root, Args = []
        });
        builder.Services.AddConfiguredBots(builder.Configuration);
        return builder.Build();
    }

    // Explicit physical left scenario construction keeps the test independent of the production
    // helper; this value is passed only to a policy, never restored to an engine.
    private static GameState PhysicalLeftInput(GameState canonicalRight) => canonicalRight with
    {
        LeftY = canonicalRight.RightY, RightY = canonicalRight.LeftY,
        BallX = 1 - canonicalRight.BallX, BallVx = -canonicalRight.BallVx,
        LeftScore = canonicalRight.RightScore, RightScore = canonicalRight.LeftScore,
        ServeDirection = -canonicalRight.ServeDirection
    };

    private static GameState Playing(long tick) => new()
    {
        LeftY = 0.3, RightY = 0.5, BallX = 0.8, BallY = 0.62, BallVx = 0.55, BallVy = 0,
        Phase = GamePhase.Playing, TickNumber = tick, RoundId = 2, ServeDirection = 1,
        RecentEvents = GameEventHistory.Empty
    };

    private sealed class RecordingPolicy : ILocalOpponentController, IDisposable
    {
        public int Axis { get; init; }
        public bool Fail { get; init; }
        public List<GameState> States { get; } = [];
        public int ResetCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void Reset() => ResetCount++;
        public int GetAxis(GameState state)
        {
            States.Add(state);
            if (Fail) throw new InvalidOperationException("Synthetic policy failure.");
            return Axis;
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingInference : IOnnxInferenceSession
    {
        public int Axis { get; set; } = 1;
        public List<float[]> Observations { get; } = [];
        public int DisposeCount { get; private set; }
        public void Run(ReadOnlySpan<float> observation, Span<float> output)
        {
            Observations.Add(observation.ToArray());
            output.Clear();
            output[RightBotObservationV1.ClassFromAxis(Axis)] = 1;
        }
        public void Dispose() => DisposeCount++;
    }
}
