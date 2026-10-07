using System.Security.Cryptography;

namespace LanPong.Tests;

public sealed class HardLocalOpponentControllerTests
{
    [Test]
    public async Task Constructor_DefersModelLoadUntilHardSelection()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-hard-{Guid.NewGuid():N}.onnx");
        using var hard = new HardLocalOpponentController(missing);
        await Assert.That(hard.IsFallbackActive).IsFalse();
        await Assert.That(hard.ModelSha256).IsNull();
        hard.Reset();
        await Assert.That(hard.IsFallbackActive).IsTrue();
        await Assert.That(hard.FallbackReason).Contains("FileNotFoundException");
    }

    [Test]
    public async Task RealModel_LoadsFrozenArtifactAndChoosesLegalAxis()
    {
        using var hard = new HardLocalOpponentController(ModelPath("hard-v1.onnx"));
        hard.Reset();
        await Assert.That(hard.IsFallbackActive).IsFalse();
        await Assert.That(hard.ModelSha256)
            .IsEqualTo(HardLocalOpponentController.ExpectedModelSha256);
        var axis = hard.GetAxis(Playing(99));
        await Assert.That(axis is >= -1 and <= 1).IsTrue();
        await Assert.That(hard.IsFallbackActive).IsFalse();
    }

    [Test]
    public async Task ModelAction_UsesAbsoluteNineTickCadenceAndCountdownReset()
    {
        using var session = new FakeSession(0f, 0f, 2f);
        using var hard = new HardLocalOpponentController(session);
        await Assert.That(hard.GetAxis(Playing(9))).IsEqualTo(1);
        await Assert.That(session.Runs).IsEqualTo(1);
        session.Set(2f, 0f, 0f);
        await Assert.That(hard.GetAxis(Playing(10))).IsEqualTo(1);
        await Assert.That(session.Runs).IsEqualTo(1);
        await Assert.That(hard.GetAxis(Playing(11) with { Phase = GamePhase.Countdown }))
            .IsEqualTo(0);
        await Assert.That(hard.GetAxis(Playing(12))).IsEqualTo(0);
        await Assert.That(hard.GetAxis(Playing(18))).IsEqualTo(-1);
        await Assert.That(session.Runs).IsEqualTo(2);
        hard.Reset();
        await Assert.That(hard.GetAxis(Playing(19))).IsEqualTo(0);
    }

    [Test]
    public async Task EqualLogits_PreferStay()
    {
        using var session = new FakeSession(1f, 1f, 1f);
        using var hard = new HardLocalOpponentController(session);
        await Assert.That(hard.GetAxis(Playing(9))).IsEqualTo(0);
        await Assert.That(hard.IsFallbackActive).IsFalse();
    }

    [Test]
    public async Task RuntimeFailure_LatchesSimpleFallbackAcrossRematch()
    {
        using var session = new FakeSession(0f, 0f, 1f) { ThrowOnRun = true };
        using var hard = new HardLocalOpponentController(session);
        var state = Playing(9);
        var simple = new SimpleLocalOpponentController();
        simple.Reset();
        var expected = simple.GetAxis(state);
        await Assert.That(hard.GetAxis(state)).IsEqualTo(expected);
        await Assert.That(hard.IsFallbackActive).IsTrue();
        await Assert.That(hard.FallbackReason).Contains("inference failed");
        await Assert.That(session.Disposed).IsTrue();
        hard.Reset();
        await Assert.That(hard.IsFallbackActive).IsTrue();
        await Assert.That(hard.GetAxis(Playing(18)) is >= -1 and <= 1).IsTrue();
        await Assert.That(session.Runs).IsEqualTo(1);
    }

    [Test]
    public async Task NonFiniteLogits_TriggerSimpleFallback()
    {
        using var session = new FakeSession(float.NaN, 0f, 1f);
        using var hard = new HardLocalOpponentController(session);
        var state = Playing(9);
        var simple = new SimpleLocalOpponentController();
        simple.Reset();
        await Assert.That(hard.GetAxis(state)).IsEqualTo(simple.GetAxis(state));
        await Assert.That(hard.IsFallbackActive).IsTrue();
        await Assert.That(hard.FallbackReason).Contains("non-finite");
    }

    [Test]
    public async Task WrongShapeModel_FallsBackBeforeGameplay()
    {
        var path = ModelPath("aot-smoke.onnx");
        using var stream = File.OpenRead(path);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        using var hard = new HardLocalOpponentController(path, digest);
        hard.Reset();
        await Assert.That(hard.IsFallbackActive).IsTrue();
        await Assert.That(hard.FallbackReason).Contains("observation[1,10]");
        await Assert.That(hard.GetAxis(Playing(9)) is >= -1 and <= 1).IsTrue();
    }

    private static GameState Playing(long tick) => new()
    {
        Phase = GamePhase.Playing,
        TickNumber = tick,
        LeftY = 0.5,
        RightY = 0.5,
        BallX = 0.8,
        BallY = 0.62,
        BallVx = 0.55,
        BallVy = 0.19
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
        private readonly float[] _logits = logits;
        public int Runs { get; private set; }
        public bool ThrowOnRun { get; set; }
        public bool Disposed { get; private set; }

        public void Set(float up, float stay, float down)
        {
            _logits[0] = up;
            _logits[1] = stay;
            _logits[2] = down;
        }

        public void Run(ReadOnlySpan<float> observation, Span<float> output)
        {
            Runs++;
            if (ThrowOnRun) throw new InvalidOperationException("inference failed");
            _logits.AsSpan().CopyTo(output);
        }

        public void Dispose() => Disposed = true;
    }
}
