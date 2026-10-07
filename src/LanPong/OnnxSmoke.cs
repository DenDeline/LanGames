using Microsoft.ML.OnnxRuntime;

namespace LanPong;

/// <summary>Published-binary check that the managed and native ONNX Runtime paths both execute.</summary>
internal static class OnnxSmoke
{
    internal static void Run()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "aot-smoke.onnx");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX smoke model was not packaged", modelPath);

        using var session = new InferenceSession(modelPath);
        using var input = OrtValue.CreateTensorValueFromMemory(new float[] { 2f }, new long[] { 1 });
        var inputs = new Dictionary<string, OrtValue> { ["input"] = input };
        using var options = new RunOptions();
        using var output = session.Run(options, inputs, new[] { "output" });
        var values = output[0].GetTensorDataAsSpan<float>();
        if (values.Length != 1 || values[0] != 3f)
            throw new InvalidOperationException("ONNX smoke inference did not produce the expected value 3");

        Console.WriteLine("ONNX smoke passed: 2 -> 3");
    }
}
