namespace LanPong.Bots.Inference;

internal interface IOnnxInferenceSession : IDisposable
{
    void Run(ReadOnlySpan<float> observation, Span<float> logits);
}
