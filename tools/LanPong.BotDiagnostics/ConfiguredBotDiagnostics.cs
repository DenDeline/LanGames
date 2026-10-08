using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanPong.Bots.Catalog;
using LanPong.Bots.Configuration;
using LanPong.Bots.Inference;
using LanPong.Bots.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LanPong.BotDiagnostics;

internal sealed record BotBenchmarkOptions(string? BotId = null, int Samples = 20_000, int Warmup = 20_000);
internal sealed record BotBenchmarkCommand(BotBenchmarkOptions Options, string[] ConfigurationArguments);
internal sealed record BotBenchmarkLatency(double MedianMilliseconds, double P95Milliseconds,
    double P99Milliseconds, double WorstMilliseconds, long AllocatedBytes);
internal sealed record BotBenchmarkFloor(double BatchMeanMilliseconds, long BatchAllocatedBytes,
    BotBenchmarkLatency Latency);
internal sealed record BotBenchmarkWorkload(string Name, int Count, int TickStride, long FirstTick,
    long LastTick, ulong BatchChecksum, ulong SampledChecksum, double BatchMeanMilliseconds,
    long BatchAllocatedBytes, BotBenchmarkLatency Latency, BotBenchmarkFloor TimestampFloor);
internal sealed record BotBenchmarkReport(string RequestedBotId, string EffectiveBotId, string StrategyId,
    int CadenceTicks, string? VerifiedModelSha256, bool? FrozenModelDigest, double PreparationMilliseconds,
    string Runtime, string Architecture, bool DynamicCodeSupported, int Samples, int Warmup, string Scope,
    BotBenchmarkWorkload[] Workloads);

/// <summary>A local diagnostic of the production prepared policy path; it starts no host or game peer.</summary>
internal static class ConfiguredBotDiagnostics
{
    private const ulong ChecksumSeed = 14695981039346656037UL;
    private const string Scope = "PreparedBotSession.GetAxis on deterministic varying Playing states. " +
        "Batched means include loop, state access, identity/axis checks and checksum; sampled latency includes " +
        "the timestamp pair and workload dispatch. Timestamp/no-op floors use the same harness and are not " +
        "subtracted. Allocation deltas cover only calling-thread managed allocations, not native memory, " +
        "other threads, the full game tick, preparation, arrays, formatting or disposal.";

    public static BotBenchmarkCommand? ParseCommand(string[] arguments)
    {
        string? botId = null;
        var benchmark = false;
        var samplesSeen = false;
        var warmupSeen = false;
        var samples = 20_000;
        var warmup = 20_000;
        var remaining = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            var equals = argument.IndexOf('=');
            var name = equals < 0 ? argument : argument[..equals];
            switch (name)
            {
                case "--bot-benchmark":
                    if (benchmark) throw new ArgumentException("Specify --bot-benchmark only once.");
                    benchmark = true;
                    if (equals >= 0)
                    {
                        botId = argument[(equals + 1)..];
                        if (string.IsNullOrWhiteSpace(botId)) throw new ArgumentException("The bot ID must not be blank.");
                    }
                    else if (index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-'))
                    {
                        botId = arguments[++index];
                        if (string.IsNullOrWhiteSpace(botId)) throw new ArgumentException("The bot ID must not be blank.");
                    }
                    break;
                case "--benchmark-samples":
                    if (samplesSeen) throw new ArgumentException("Specify --benchmark-samples only once.");
                    samplesSeen = true;
                    samples = ReadCount(arguments, ref index, equals, "--benchmark-samples");
                    break;
                case "--benchmark-warmup":
                    if (warmupSeen) throw new ArgumentException("Specify --benchmark-warmup only once.");
                    warmupSeen = true;
                    warmup = ReadCount(arguments, ref index, equals, "--benchmark-warmup");
                    break;
                default:
                    remaining.Add(argument);
                    break;
            }
        }
        if (!benchmark)
        {
            if (samplesSeen || warmupSeen) throw new ArgumentException("Benchmark counts require --bot-benchmark.");
            return null;
        }
        var options = new BotBenchmarkOptions(botId, samples, warmup);
        ValidateOptions(options);
        return new(options, remaining.ToArray());
    }

    private static int ReadCount(string[] arguments, ref int index, int equals, string name)
    {
        var value = equals >= 0 ? arguments[index][(equals + 1)..]
            : ++index < arguments.Length ? arguments[index] : null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new ArgumentException($"{name} requires a positive integer.");
        return count;
    }

    private static void ValidateOptions(BotBenchmarkOptions options)
    {
        if (options.Samples is < 100 or > 100_000)
            throw new ArgumentException("Benchmark samples must be between 100 and 100000.");
        if (options.Warmup is < 2 or > 100_000)
            throw new ArgumentException("Benchmark warmup must be between 2 and 100000.");
        if (options.BotId is not null && string.IsNullOrWhiteSpace(options.BotId))
            throw new ArgumentException("The bot ID must not be blank.");
    }

    public static void Run(BotBenchmarkCommand command, TextWriter output)
    {
        // The normal application providers and content root resolve the catalog and model paths.
        // The built application owns configuration watchers and file providers. It is never started;
        // synchronous host disposal also completes asynchronous service-provider cleanup.
        var builder = WebApplication.CreateBuilder(command.ConfigurationArguments);
        builder.Logging.ClearProviders();
        builder.Services.AddConfiguredBots(builder.Configuration);
        using var application = builder.Build();
        var report = Measure(application.Services.GetRequiredService<BotRuntime>(), command.Options);
        output.WriteLine(JsonSerializer.Serialize(report, BotBenchmarkJsonSerializerContext.Default.BotBenchmarkReport));
    }

    public static BotBenchmarkReport Measure(BotRuntime runtime, BotBenchmarkOptions options)
    {
        ValidateOptions(options);
        var requestedId = options.BotId ?? runtime.Catalog.DefaultBotId;
        var preparationStart = Stopwatch.GetTimestamp();
        using var session = runtime.Prepare(requestedId);
        var preparationMilliseconds = Milliseconds(Stopwatch.GetTimestamp() - preparationStart);
        var definition = session.Requested;
        RequireHealthy(session, definition, 0);
        var cadence = definition.Tracker?.ObservationIntervalTicks ?? definition.Onnx!.InferenceCadenceTicks;
        var verifiedDigest = session.VerifiedModelSha256;
        if (definition.Onnx is { } onnx && !string.Equals(verifiedDigest, onnx.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The prepared model has no verified configured digest.");

        var total = checked(options.Warmup + options.Samples * 2);
        var consecutive = new WorkloadInputs("consecutive", 1, 1, total, options);
        var nextTick = checked((consecutive.States[^1].TickNumber / cadence + 1) * cadence);
        var spaced = new WorkloadInputs("cadence-spaced", cadence, nextTick, total, options);
        var workloads = new[] { MeasureWorkload(session, definition, consecutive, options),
            MeasureWorkload(session, definition, spaced, options) };
        RequireHealthy(session, definition, 0);
        return new(definition.Id, session.Effective.Id, definition.StrategyId, cadence, verifiedDigest,
            verifiedDigest is null ? null : string.Equals(verifiedDigest,
                BotModelV1.ExpectedSha256, StringComparison.Ordinal),
            preparationMilliseconds, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeFeature.IsDynamicCodeSupported,
            options.Samples, options.Warmup, Scope, workloads);
    }

    private sealed class WorkloadInputs
    {
        public WorkloadInputs(string name, int stride, long firstTick, int count, BotBenchmarkOptions options)
        {
            Name = name;
            Stride = stride;
            States = new GameState[count];
            Samples = new long[options.Samples];
            FloorSamples = new long[options.Samples];
            WarmSamples = new long[options.Warmup - options.Warmup / 2];
            for (var index = 0; index < count; index++)
            {
                // Vary all policy-relevant positions and velocities while staying inside the arena.
                States[index] = new GameState
                {
                    LeftY = 0.14 + (index * 17 % 73) / 100.0,
                    RightY = 0.14 + (index * 29 % 73) / 100.0,
                    BallX = 0.61 + (index * 7 % 32) / 100.0,
                    BallY = 0.03 + (index * 31 % 94) / 100.0,
                    BallVx = index % 7 == 0 ? -0.45 : 0.35 + (index % 13) * 0.025,
                    BallVy = ((index * 11 % 37) - 18) / 30.0,
                    LeftScore = index % 7,
                    RightScore = index % 5,
                    Phase = GamePhase.Playing,
                    TickNumber = checked(firstTick + (long)index * stride),
                    RoundId = 1,
                    ServeDirection = 1,
                    Hits = index % 19,
                    RecentEvents = GameEventHistory.Empty
                };
            }
        }
        public string Name { get; }
        public int Stride { get; }
        public GameState[] States { get; }
        public long[] Samples { get; }
        public long[] FloorSamples { get; }
        public long[] WarmSamples { get; }
    }

    private static BotBenchmarkWorkload MeasureWorkload(PreparedBotSession session, BotDefinition expected,
        WorkloadInputs inputs, BotBenchmarkOptions options)
    {
        var warmBatch = options.Warmup / 2;
        var warmSampled = options.Warmup - warmBatch;
        // Warm the actual batch/sampled methods and both dispatch paths, including timestamp/GC hooks.
        _ = MeasureBatch(session, expected, inputs.States, 0, warmBatch, floor: true);
        _ = MeasureSampled(session, expected, inputs.States, warmBatch, warmSampled, inputs.WarmSamples, floor: true);
        _ = MeasureBatch(session, expected, inputs.States, 0, warmBatch, floor: false);
        _ = MeasureSampled(session, expected, inputs.States, warmBatch, warmSampled, inputs.WarmSamples, floor: false);

        var batch = MeasureBatch(session, expected, inputs.States, options.Warmup, options.Samples, floor: false);
        var sampled = MeasureSampled(session, expected, inputs.States, options.Warmup + options.Samples,
            options.Samples, inputs.Samples, floor: false);
        var floorBatch = MeasureBatch(session, expected, inputs.States, options.Warmup, options.Samples, floor: true);
        var floorSampled = MeasureSampled(session, expected, inputs.States, options.Warmup + options.Samples,
            options.Samples, inputs.FloorSamples, floor: true);
        return new(inputs.Name, options.Samples, inputs.Stride, inputs.States[options.Warmup].TickNumber,
            inputs.States[^1].TickNumber, batch.Checksum, sampled.Checksum,
            Milliseconds(batch.ElapsedTicks) / options.Samples, batch.AllocatedBytes,
            DescribeLatency(inputs.Samples, sampled.AllocatedBytes),
            new(Milliseconds(floorBatch.ElapsedTicks) / options.Samples, floorBatch.AllocatedBytes,
                DescribeLatency(inputs.FloorSamples, floorSampled.AllocatedBytes)));
    }

    private readonly record struct Measurement(long ElapsedTicks, long AllocatedBytes, ulong Checksum);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Measurement MeasureBatch(PreparedBotSession session, BotDefinition expected,
        GameState[] states, int start, int count, bool floor)
    {
        var checksum = ChecksumSeed;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var began = Stopwatch.GetTimestamp();
        for (var index = start; index < start + count; index++)
        {
            var state = states[index];
            var axis = floor ? TimestampFloorAxis(state) : session.GetAxis(state, PaddleSide.Right);
            RequireHealthy(session, expected, axis);
            checksum = Mix(checksum, state.TickNumber, axis);
        }
        var elapsed = Stopwatch.GetTimestamp() - began;
        return new(elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated, checksum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Measurement MeasureSampled(PreparedBotSession session, BotDefinition expected,
        GameState[] states, int start, int count, long[] samples, bool floor)
    {
        var checksum = ChecksumSeed;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            var state = states[start + index];
            var began = Stopwatch.GetTimestamp();
            var axis = floor ? TimestampFloorAxis(state) : session.GetAxis(state, PaddleSide.Right);
            samples[index] = Stopwatch.GetTimestamp() - began;
            RequireHealthy(session, expected, axis);
            checksum = Mix(checksum, state.TickNumber, axis);
        }
        return new(0, GC.GetAllocatedBytesForCurrentThread() - allocated, checksum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int TimestampFloorAxis(GameState state) => (int)(state.TickNumber % 3) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong checksum, long tick, int axis) =>
        unchecked((checksum ^ ((ulong)tick * 3 + (ulong)(axis + 1))) * 1099511628211UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RequireHealthy(PreparedBotSession session, BotDefinition expected, int axis)
    {
        if (!session.IsPlayable || !ReferenceEquals(session.Requested, expected) ||
            !ReferenceEquals(session.Effective, expected) || session.FallbackReason is not null)
            throw new InvalidOperationException("The requested bot became unavailable or used a fallback; no benchmark result was produced.");
        if (axis is < -1 or > 1)
            throw new InvalidOperationException("The requested bot returned an invalid axis; no benchmark result was produced.");
    }

    private static BotBenchmarkLatency DescribeLatency(long[] samples, long allocatedBytes)
    {
        Array.Sort(samples);
        var median = (Milliseconds(samples[(samples.Length - 1) / 2]) + Milliseconds(samples[samples.Length / 2])) / 2;
        return new(median, Milliseconds(samples[(int)Math.Ceiling(samples.Length * 0.95) - 1]),
            Milliseconds(samples[(int)Math.Ceiling(samples.Length * 0.99) - 1]),
            Milliseconds(samples[^1]), allocatedBytes);
    }

    private static double Milliseconds(long ticks) => (double)ticks * 1000 / Stopwatch.Frequency;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BotBenchmarkReport))]
internal partial class BotBenchmarkJsonSerializerContext : JsonSerializerContext;
