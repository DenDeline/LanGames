namespace LanPong;

internal interface IOnnxInferenceSession : IDisposable
{
    void Run(ReadOnlySpan<float> observation, Span<float> logits);
}
