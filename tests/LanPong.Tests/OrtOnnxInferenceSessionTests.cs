using LanPong.Bots.Inference;

namespace LanPong.Tests;

public sealed class OrtOnnxInferenceSessionTests
{
    [Test]
    public async Task FrozenModel_WarmsAndReusesNativeBindingsWithFiniteDeterministicLogits()
    {
        using var session = new OrtOnnxInferenceSession(BotTestSupport.ModelPath());
        var observation = RightBotObservationV1.Encode(new GameState
        {
            Phase = GamePhase.Playing, TickNumber = 99, LeftY = 0.5, RightY = 0.5,
            BallX = 0.8, BallY = 0.62, BallVx = 0.55, BallVy = 0.19
        });
        var first = new float[BotModelV1.ActionCount];
        var repeated = new float[BotModelV1.ActionCount];
        session.Run(observation, first);
        for (var call = 0; call < 3; call++)
        {
            Array.Fill(repeated, float.NaN);
            session.Run(observation, repeated);
            await Assert.That(repeated.All(float.IsFinite)).IsTrue();
            await Assert.That(repeated.SequenceEqual(first)).IsTrue();
        }
    }

    [Test]
    public async Task WrongModelSchema_IsRejectedBeforeSessionCanBeUsed()
    {
        await Assert.That(() => new OrtOnnxInferenceSession(BotTestSupport.DiagnosticsModelPath()))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task NonFiniteWarmup_IsRejectedEvenWhenModelSchemaIsCorrect()
    {
        // ONNX IR 7/opset 13: float32 observation[1,10] -> Constant float32 logits[1,3] = [NaN,0,0].
        // Kept in the test so production model packaging never includes an intentionally invalid policy.
        const string model = "CAcSDUxhblBvbmcuVGVzdHM6gQEKNBIGbG9naXRzIghDb25zdGFudCogCgV2YWx1ZSoUCAEIAxABSgwAAMB/AAAAAAAAAACgAQQSEG5vbmZpbml0ZV93YXJtdXBaHQoLb2JzZXJ2YXRpb24SDgoMCAESCAoCCAEKAggKYhgKBmxvZ2l0cxIOCgwIARIICgIIAQoCCANCAhAN";
        var path = Path.Combine(Path.GetTempPath(), $"nonfinite-warmup-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, Convert.FromBase64String(model));
        try
        {
            var error = Capture(() => new OrtOnnxInferenceSession(path));
            await Assert.That(error is InvalidDataException).IsTrue();
            await Assert.That(error!.Message).Contains("warmup returned non-finite logits");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Run_RejectsInvalidBufferLengthsAndDisposalPreventsFurtherInference()
    {
        var session = new OrtOnnxInferenceSession(BotTestSupport.ModelPath());
        try
        {
            foreach (var (inputLength, outputLength) in new[] { (9, 3), (11, 3), (10, 2), (10, 4) })
                await Assert.That(() => session.Run(new float[inputLength], new float[outputLength]))
                    .Throws<ArgumentException>();

            var output = new float[BotModelV1.ActionCount];
            session.Run(new float[RightBotObservationV1.FeatureCount], output);
            await Assert.That(output.All(float.IsFinite)).IsTrue();
            session.Dispose();
            session.Dispose();
            await Assert.That(() => session.Run(new float[RightBotObservationV1.FeatureCount], output))
                .Throws<ObjectDisposedException>();
        }
        finally
        {
            session.Dispose();
        }
    }

    private static Exception? Capture(Func<OrtOnnxInferenceSession> create)
    {
        try
        {
            using var session = create();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
