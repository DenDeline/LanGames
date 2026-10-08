using System.Security.Cryptography;
using LanPong.Bots.Catalog;
using LanPong.Bots.Inference;

namespace LanPong.Bots.Strategies;

/// <summary>A configured strict ONNX policy, prepared before admission to the game clock.</summary>
internal sealed class OnnxLocalOpponentController : ILocalOpponentController, IDisposable
{
    private readonly OnnxBotSettings _settings;
    private readonly Func<string, IOnnxInferenceSession> _createSession;
    private readonly float[] _observation = new float[RightBotObservationV1.FeatureCount];
    private readonly float[] _logits = new float[BotModelV1.ActionCount];
    private IOnnxInferenceSession? _session;
    private int _axis;
    private bool _prepared;
    private bool _disposed;

    internal OnnxLocalOpponentController(OnnxBotSettings settings)
        : this(settings, static path => new OrtOnnxInferenceSession(path)) { }

    // The factory creates a validated, warmed session. Initialization remains outside gameplay.
    internal OnnxLocalOpponentController(OnnxBotSettings settings,
        Func<string, IOnnxInferenceSession> createSession)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(createSession);
        _settings = settings;
        _createSession = createSession;
    }

    // Focused tests can inject an already prepared session without a model artifact.
    internal OnnxLocalOpponentController(OnnxBotSettings settings, IOnnxInferenceSession session)
        : this(settings)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    internal string ModelPath => _settings.ModelPath;
    internal string? ModelSha256 { get; private set; }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is null)
        {
            using var file = File.OpenRead(ModelPath);
            ModelSha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            if (!string.Equals(ModelSha256, _settings.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ONNX model SHA-256 does not match the configured artifact.");
            _session = _createSession(ModelPath);
        }
        _axis = 0;
        _prepared = true;
    }

    public int GetAxis(GameState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_prepared)
            throw new InvalidOperationException("ONNX controller must be prepared before gameplay.");
        if (state.Phase is GamePhase.Waiting or GamePhase.GameOver) return 0;
        if (state.Phase == GamePhase.Countdown)
        {
            _axis = 0;
            return 0;
        }
        if (state.TickNumber % _settings.InferenceCadenceTicks != 0)
            return _axis;

        RightBotObservationV1.Encode(state, _observation);
        _session!.Run(_observation, _logits);
        if (!float.IsFinite(_logits[0]) || !float.IsFinite(_logits[1]) || !float.IsFinite(_logits[2]))
            throw new InvalidDataException("ONNX model returned non-finite logits.");
        var bestClass = 1; // Preserve the trained policy's preference for stay on a tie.
        for (var candidate = 0; candidate < BotModelV1.ActionCount; candidate++)
            if (_logits[candidate] > _logits[bestClass]) bestClass = candidate;
        _axis = RightBotObservationV1.AxisFromClass(bestClass);
        return _axis;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Detach before native disposal so even a failed disposal is attempted only once.
        var session = _session;
        _session = null;
        session?.Dispose();
    }
}
