using LanPong;
using LanPong.Bots.Inference;
using LanPong.Bots.Strategies;
using Microsoft.ML.OnnxRuntime;

namespace LanPong.TrainingData;

/// <summary>Optional Step 6 behavior policy; visited states are still labeled by the teacher.</summary>
internal sealed class OnnxStudentPolicy : ILocalOpponentController, IDisposable
{
    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _observation = new float[RightBotObservationV1.FeatureCount];
    private readonly long[] _inputShape = [1, RightBotObservationV1.FeatureCount];
    private readonly string[] _outputNames = [RightBotObservationV1.OutputName];
    private int _axis;

    public OnnxStudentPolicy(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Student ONNX model was not found.", modelPath);
        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1
        };
        _session = new InferenceSession(modelPath, options);
        if (!_session.InputNames.Contains(RightBotObservationV1.InputName) ||
            !_session.OutputNames.Contains(RightBotObservationV1.OutputName))
            throw new InvalidDataException("Student model must expose observation input and logits output.");
    }

    public void Reset()
    {
        _axis = 0;
    }

    public int GetAxis(GameState state)
    {
        if (state.Phase is GamePhase.Waiting or GamePhase.GameOver) return 0;
        if (state.Phase == GamePhase.Countdown)
        {
            _axis = 0;
            return 0;
        }
        if (state.TickNumber % RightBotObservationV1.InferenceCadenceTicks != 0) return _axis;

        RightBotObservationV1.Encode(state, _observation);
        using var input = OrtValue.CreateTensorValueFromMemory(_observation, _inputShape);
        var inputs = new Dictionary<string, OrtValue> { [RightBotObservationV1.InputName] = input };
        using var output = _session.Run(_runOptions, inputs, _outputNames);
        var logits = output[0].GetTensorDataAsSpan<float>();
        if (logits.Length != 3 || !float.IsFinite(logits[0]) ||
            !float.IsFinite(logits[1]) || !float.IsFinite(logits[2]))
            throw new InvalidDataException("Student model must return three finite logits.");

        var bestClass = 1;
        for (var candidate = 0; candidate < 3; candidate++)
            if (logits[candidate] > logits[bestClass]) bestClass = candidate;
        _axis = RightBotObservationV1.AxisFromClass(bestClass);
        return _axis;
    }

    public void Dispose()
    {
        _session.Dispose();
        _runOptions.Dispose();
    }
}
