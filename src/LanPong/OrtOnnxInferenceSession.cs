using Microsoft.ML.OnnxRuntime;
using OrtSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace LanPong;

/// <summary>One warmed CPU session with preallocated, reusable input and output OrtValues.</summary>
internal sealed class OrtOnnxInferenceSession : IOnnxInferenceSession
{
    private readonly float[] _inputBuffer = new float[RightBotObservationV1.FeatureCount];
    private readonly float[] _outputBuffer = new float[BotModelV1.ActionCount];
    private InferenceSession? _session;
    private RunOptions? _runOptions;
    private OrtValue? _inputValue;
    private OrtValue? _outputValue;
    private OrtIoBinding? _binding;

    internal OrtOnnxInferenceSession(string path)
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
            _outputValue = OrtValue.CreateTensorValueFromMemory(_outputBuffer, [1, BotModelV1.ActionCount]);
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
                throw new InvalidDataException("ONNX warmup returned non-finite logits.");
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
            !output.Dimensions.SequenceEqual([1, BotModelV1.ActionCount]))
            throw new InvalidDataException(
                "ONNX model must expose float32 observation[1,10] and logits[1,3].");
    }

    public void Run(ReadOnlySpan<float> observation, Span<float> logits)
    {
        if (observation.Length != _inputBuffer.Length || logits.Length != _outputBuffer.Length)
            throw new ArgumentException("ONNX inference requires ten inputs and three outputs.");
        observation.CopyTo(_inputBuffer);
        RunCore();
        _outputBuffer.CopyTo(logits);
    }

    private void RunCore()
    {
        if (_session is null) throw new ObjectDisposedException(nameof(OrtOnnxInferenceSession));
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
