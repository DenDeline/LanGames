using System.Security.Cryptography;
using LanPong;

namespace LanPong.TrainingData;

/// <summary>Runs the same exact-engine matches through either ONNX adapter.</summary>
internal sealed class EvaluatedModelPolicy : ILocalOpponentController, IDisposable
{
    private readonly ILocalOpponentController _controller;
    private readonly IDisposable _owner;
    private readonly HardLocalOpponentController? _production;

    private EvaluatedModelPolicy(string backend, string? modelSha256,
        ILocalOpponentController controller, IDisposable owner,
        HardLocalOpponentController? production = null)
    {
        Backend = backend;
        ModelSha256 = modelSha256;
        _controller = controller;
        _owner = owner;
        _production = production;
    }

    public string Backend { get; }
    public string? ModelSha256 { get; }
    public bool IsFallbackActive => _production?.IsFallbackActive ?? false;
    public string? FallbackReason => _production?.FallbackReason;

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
            var hard = new HardLocalOpponentController(absolutePath);
            try
            {
                // Production Hard loads and warms on its first Reset. Capture
                // SHA and load fallback only after that startup transition.
                hard.Reset();
                return new EvaluatedModelPolicy(backend, hard.ModelSha256, hard, hard, hard);
            }
            catch
            {
                hard.Dispose();
                throw;
            }
        }

        throw new ArgumentException("Backend must be offline or production.", nameof(backend));
    }

    public void Reset() => _controller.Reset();
    public int GetAxis(GameState state) => _controller.GetAxis(state);
    public void Dispose() => _owner.Dispose();
}
