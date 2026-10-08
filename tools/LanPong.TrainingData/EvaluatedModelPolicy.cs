using System.Security.Cryptography;
using LanPong;
using LanPong.Bots.Catalog;
using LanPong.Bots.Inference;
using LanPong.Bots.Strategies;

namespace LanPong.TrainingData;

/// <summary>Runs the same exact-engine matches through either ONNX adapter.</summary>
internal sealed class EvaluatedModelPolicy : ILocalOpponentController, IDisposable
{
    private readonly ILocalOpponentController _controller;
    private readonly IDisposable _owner;

    private EvaluatedModelPolicy(string backend, string? modelSha256,
        ILocalOpponentController controller, IDisposable owner)
    {
        Backend = backend;
        ModelSha256 = modelSha256;
        _controller = controller;
        _owner = owner;
    }

    public string Backend { get; }
    public string? ModelSha256 { get; }

    public static EvaluatedModelPolicy Create(string backend, string? modelPath)
    {
        if (backend is "offline" or "production")
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("Model evaluation requires --student-model.");
            var absolutePath = Path.GetFullPath(modelPath);
            if (backend == "offline")
            {
                using var modelFile = File.OpenRead(absolutePath);
                var sha = Convert.ToHexString(SHA256.HashData(modelFile)).ToLowerInvariant();
                var student = new OnnxStudentPolicy(absolutePath);
                return new EvaluatedModelPolicy(backend, sha, student, student);
            }
            return CreateProduction(absolutePath);
        }

        throw new ArgumentException("Backend must be offline or production.", nameof(backend));
    }

    internal static EvaluatedModelPolicy CreateProduction(string modelPath,
        Func<string, IOnnxInferenceSession>? createSession = null)
    {
        var settings = new OnnxBotSettings(Path.GetFullPath(modelPath),
            BotModelV1.ExpectedSha256, RightBotObservationV1.InferenceCadenceTicks);
        var policy = createSession is null
            ? new OnnxLocalOpponentController(settings)
            : new OnnxLocalOpponentController(settings, createSession);
        try
        {
            // Use the prepared production policy itself. A failed load or
            // inference aborts evaluation rather than substituting a baseline.
            policy.Reset();
            return new EvaluatedModelPolicy("production", policy.ModelSha256, policy, policy);
        }
        catch
        {
            policy.Dispose();
            throw;
        }
    }

    public void Reset() => _controller.Reset();
    public int GetAxis(GameState state) => _controller.GetAxis(state);
    public void Dispose() => _owner.Dispose();
}
