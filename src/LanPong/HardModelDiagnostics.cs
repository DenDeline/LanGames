using System.Diagnostics;

namespace LanPong;

/// <summary>Explicit real-model smoke and benchmark paths; normal play has no timing telemetry.</summary>
internal static class HardModelDiagnostics
{
    internal static void Smoke()
    {
        using var controller = new HardLocalOpponentController();
        controller.Reset();
        RequireOnnx(controller);
        var axis = controller.GetAxis(SampleState(99));
        if (axis is < -1 or > 1 || controller.IsFallbackActive)
            throw new InvalidOperationException("Hard ONNX smoke did not produce a legal model action.");
        Console.WriteLine($"Hard ONNX smoke passed: SHA-256 {controller.ModelSha256}, axis {axis}");
    }

    internal static void Benchmark(int decisions = 10_000)
    {
        if (decisions < 100) throw new ArgumentOutOfRangeException(nameof(decisions));
        using var controller = new HardLocalOpponentController();
        controller.Reset();
        RequireOnnx(controller);
        for (var index = 0; index < 1_000; index++)
            controller.GetAxis(SampleState((index + 1) * 9L));

        var elapsedTicks = new long[decisions];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < decisions; index++)
        {
            var state = SampleState((index + 1_001) * 9L);
            var start = Stopwatch.GetTimestamp();
            var axis = controller.GetAxis(state);
            elapsedTicks[index] = Stopwatch.GetTimestamp() - start;
            if (axis is < -1 or > 1 || controller.IsFallbackActive)
                throw new InvalidOperationException("Hard inference failed during benchmark.");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(elapsedTicks);
        double ToMilliseconds(long ticks) => ticks * 1_000.0 / Stopwatch.Frequency;
        var p95 = elapsedTicks[(int)Math.Ceiling(decisions * 0.95) - 1];
        var p99 = elapsedTicks[(int)Math.Ceiling(decisions * 0.99) - 1];
        Console.WriteLine($"Hard ONNX decisions={decisions}, p95={ToMilliseconds(p95):F4} ms, " +
            $"p99={ToMilliseconds(p99):F4} ms, worst={ToMilliseconds(elapsedTicks[^1]):F4} ms, " +
            $"managed allocated={allocated} bytes " +
            $"({allocated / (double)decisions:F2} managed bytes/decision), " +
            $"tick budget={GameConstants.FixedStepSeconds * 1_000:F4} ms, " +
            $"model SHA-256={controller.ModelSha256}");
    }

    private static void RequireOnnx(HardLocalOpponentController controller)
    {
        if (controller.IsFallbackActive || controller.ModelSha256 !=
            HardLocalOpponentController.ExpectedModelSha256)
            throw new InvalidOperationException(
                $"Hard ONNX backend unavailable: {controller.FallbackReason ?? "model hash mismatch"}");
    }

    private static GameState SampleState(long tick)
    {
        var phase = (int)(tick / 9 % 80);
        return new GameState
        {
            Phase = GamePhase.Playing,
            TickNumber = tick,
            LeftY = 0.25 + phase * 0.005,
            RightY = 0.7 - phase * 0.005,
            BallX = 0.1 + phase * 0.01,
            BallY = 0.18 + phase * 0.008,
            BallVx = 0.55,
            BallVy = phase % 2 == 0 ? 0.19 : -0.19,
            LeftScore = phase % 6,
            RightScore = phase % 5
        };
    }
}
