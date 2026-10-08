using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using OrtSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace LanPong;

/// <summary>Legacy diagnostics/training policy; a failed model stays on Simple until disposal.</summary>
internal sealed class HardLocalOpponentController : ILocalOpponentController, IDisposable
{
    internal const string ExpectedModelSha256 =
        "5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a";

    private readonly SimpleLocalOpponentController _fallback = new();
    private readonly float[] _observation = new float[RightBotObservationV1.FeatureCount];
    private readonly float[] _logits = new float[3];
    private IHardInferenceSession? _session;
    private int _axis;
    private bool _disposed;
    private bool _initialized;

    internal bool IsFallbackActive { get; private set; }
    internal string? FallbackReason { get; private set; }
    internal string? ModelSha256 { get; private set; }
    internal string ModelPath { get; }

    internal HardLocalOpponentController(string? modelPath = null)
        : this(modelPath, ExpectedModelSha256) { }

    // A test can supply its own expected digest to reach metadata validation.
    internal HardLocalOpponentController(string? modelPath, string expectedSha256)
    {
        ModelPath = modelPath ?? Path.Combine(AppContext.BaseDirectory, "Models", "hard-v1.onnx");
        _expectedSha256 = expectedSha256;
        _fallback.Reset();
    }

    private readonly string? _expectedSha256;

    private void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            using var file = File.OpenRead(ModelPath);
            ModelSha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            if (!string.Equals(ModelSha256, _expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Hard ONNX model SHA-256 does not match the frozen artifact.");
            _session = new OrtHardInferenceSession(ModelPath);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ActivateFallback(error);
        }
    }

    // Inject a deterministic inference failure or logits in focused tests.
    internal HardLocalOpponentController(IHardInferenceSession session)
    {
        ModelPath = "<injected>";
        _session = session;
        _initialized = true;
        _fallback.Reset();
    }

    public void Reset()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HardLocalOpponentController));
        Initialize();
        _axis = 0;
        _fallback.Reset();
    }

    public int GetAxis(GameState state)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HardLocalOpponentController));
        Initialize();
        if (IsFallbackActive) return _fallback.GetAxis(state);
        if (state.Phase is GamePhase.Waiting or GamePhase.GameOver) return 0;
        if (state.Phase == GamePhase.Countdown)
        {
            _axis = 0;
            return 0;
        }
        if (state.TickNumber % RightBotObservationV1.InferenceCadenceTicks != 0)
            return _axis;

        try
        {
            RightBotObservationV1.Encode(state, _observation);
            _session!.Run(_observation, _logits);
            if (_logits.Length != 3 || !float.IsFinite(_logits[0]) ||
                !float.IsFinite(_logits[1]) || !float.IsFinite(_logits[2]))
                throw new InvalidDataException("Hard ONNX model returned non-finite logits.");
            var bestClass = 1; // The frozen policy prefers stay on a logit tie.
            for (var candidate = 0; candidate < 3; candidate++)
                if (_logits[candidate] > _logits[bestClass]) bestClass = candidate;
            _axis = RightBotObservationV1.AxisFromClass(bestClass);
            return _axis;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ActivateFallback(error);
            return _fallback.GetAxis(state);
        }
    }

    private void ActivateFallback(Exception error)
    {
        IsFallbackActive = true;
        FallbackReason = $"{error.GetType().Name}: {error.Message}";
        _axis = 0;
        try { _session?.Dispose(); }
        catch (Exception) { /* A failed native session must not stop the game clock. */ }
        _session = null;
        _fallback.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _session = null;
    }
}

internal interface IHardInferenceSession : IDisposable
{
    void Run(ReadOnlySpan<float> observation, Span<float> logits);
}

/// <summary>One warmed CPU session with preallocated, reusable input and output OrtValues.</summary>
internal sealed class OrtHardInferenceSession : IHardInferenceSession
{
    private readonly float[] _inputBuffer = new float[RightBotObservationV1.FeatureCount];
    private readonly float[] _outputBuffer = new float[3];
    private InferenceSession? _session;
    private RunOptions? _runOptions;
    private OrtValue? _inputValue;
    private OrtValue? _outputValue;
    private OrtIoBinding? _binding;

    internal OrtHardInferenceSession(string path)
    {
        using var options = new OrtSessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        try
        {
            _session = new InferenceSession(path, options);
            ValidateMetadata(_session);
            _runOptions = new RunOptions();
            _inputValue = OrtValue.CreateTensorValueFromMemory(_inputBuffer,
                [1, RightBotObservationV1.FeatureCount]);
            _outputValue = OrtValue.CreateTensorValueFromMemory(_outputBuffer, [1, 3]);
            _binding = _session.CreateIoBinding();
            _binding.BindInput(RightBotObservationV1.InputName, _inputValue);
            _binding.BindOutput(RightBotObservationV1.OutputName, _outputValue);

            // Warm the exact reusable buffers once before a match can use the controller.
            _inputBuffer[0] = _inputBuffer[1] = _inputBuffer[2] = _inputBuffer[3] = 0.5f;
            _inputBuffer[8] = 0.5f;
            _inputBuffer[9] = 2f;
            RunCore();
            if (!float.IsFinite(_outputBuffer[0]) || !float.IsFinite(_outputBuffer[1]) ||
                !float.IsFinite(_outputBuffer[2]))
                throw new InvalidDataException("Hard ONNX warmup returned non-finite logits.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void ValidateMetadata(InferenceSession session)
    {
        if (session.InputMetadata.Count != 1 ||
            !session.InputMetadata.TryGetValue(RightBotObservationV1.InputName, out var input) ||
            !input.IsTensor || input.ElementType != typeof(float) ||
            !input.Dimensions.SequenceEqual([1, RightBotObservationV1.FeatureCount]) ||
            session.OutputMetadata.Count != 1 ||
            !session.OutputMetadata.TryGetValue(RightBotObservationV1.OutputName, out var output) ||
            !output.IsTensor || output.ElementType != typeof(float) ||
            !output.Dimensions.SequenceEqual([1, 3]))
            throw new InvalidDataException(
                "Hard ONNX model must expose float32 observation[1,10] and logits[1,3].");
    }

    public void Run(ReadOnlySpan<float> observation, Span<float> logits)
    {
        if (observation.Length != _inputBuffer.Length || logits.Length != _outputBuffer.Length)
            throw new ArgumentException("Hard inference requires ten inputs and three outputs.");
        observation.CopyTo(_inputBuffer);
        RunCore();
        _outputBuffer.CopyTo(logits);
    }

    private void RunCore()
    {
        if (_session is null) throw new ObjectDisposedException(nameof(OrtHardInferenceSession));
        _session.RunWithBinding(_runOptions!, _binding!);
    }

    public void Dispose()
    {
        _binding?.Dispose();
        _outputValue?.Dispose();
        _inputValue?.Dispose();
        _runOptions?.Dispose();
        _session?.Dispose();
        _outputValue = null;
        _inputValue = null;
        _binding = null;
        _runOptions = null;
        _session = null;
    }
}
