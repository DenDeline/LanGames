using System.Text.Json;
using LanPong.BotDiagnostics;
using LanPong.Bots.Catalog;
using LanPong.Bots.Configuration;
using LanPong.Bots.Runtime;
using LanPong.Bots.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanPong.Tests;

public sealed class ConfiguredBotDiagnosticsTests
{
    [Test]
    public async Task CommandParser_SelectsOptionalIdAndPreservesConfigurationArguments()
    {
        var command = ConfiguredBotDiagnostics.ParseCommand([
            "--Bots:DefaultBotId", "quick", "--benchmark-samples", "100",
            "--bot-benchmark", "steady", "--contentRoot", "/configured/root",
            "--benchmark-warmup", "2", "--Bots:Entries:0:Tracker:TargetDeadZone=0.5"
        ])!;

        await Assert.That(command.Options).IsEqualTo(new BotBenchmarkOptions("steady", 100, 2));
        await Assert.That(string.Join('|', command.ConfigurationArguments)).IsEqualTo(
            "--Bots:DefaultBotId|quick|--contentRoot|/configured/root|--Bots:Entries:0:Tracker:TargetDeadZone=0.5");
        var defaultSelection = ConfiguredBotDiagnostics.ParseCommand(["--bot-benchmark"])!;
        await Assert.That(defaultSelection.Options.BotId).IsNull();
        var equalsSelection = ConfiguredBotDiagnostics.ParseCommand([
            "--bot-benchmark=quick", "--benchmark-samples=100", "--benchmark-warmup=2",
            "--Bots:DefaultBotId=steady"
        ])!;
        await Assert.That(equalsSelection.Options).IsEqualTo(new BotBenchmarkOptions("quick", 100, 2));
        await Assert.That(string.Join('|', equalsSelection.ConfigurationArguments))
            .IsEqualTo("--Bots:DefaultBotId=steady");
        await Assert.That(ConfiguredBotDiagnostics.ParseCommand(["--urls", "http://localhost:5080"])).IsNull();
    }

    [Test]
    public async Task CommandParser_RejectsMalformedOrAmbiguousDiagnosticOptions()
    {
        string[][] cases = [
            ["--benchmark-samples", "100"],
            ["--bot-benchmark="],
            ["--bot-benchmark", "--benchmark-samples"],
            ["--bot-benchmark", "--benchmark-samples", "99"],
            ["--bot-benchmark", "--benchmark-samples", "100001"],
            ["--bot-benchmark", "--benchmark-samples", "many"],
            ["--bot-benchmark", "--benchmark-warmup", "1"],
            ["--bot-benchmark", "--benchmark-warmup", "100001"],
            ["--bot-benchmark", "--benchmark-samples", "100", "--benchmark-samples", "100"],
            ["--bot-benchmark", "steady", "--bot-benchmark", "quick"]
        ];
        foreach (var arguments in cases)
            await Assert.That(Capture(() => ConfiguredBotDiagnostics.ParseCommand(arguments)) is ArgumentException)
                .IsTrue();
    }

    [Test]
    public async Task Run_UsesNormalConfigurationContentRootAndProviderDefaultWithoutHosting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lanpong-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "appsettings.json"), """
                {"Bots":{"DefaultBotId":"steady","Entries":[
                  {"Id":"steady","Name":"Спокойный","Description":"Проверка настроек.",
                   "Style":"Размеренный","Difficulty":"Начальный","Category":"Проверка",
                   "StrategyId":"tracker","Tracker":{"ObservationIntervalTicks":5}},
                  {"Id":"quick","Name":"Быстрый","Description":"Выбран через провайдер.",
                   "Style":"Быстрый","Difficulty":"Средний","Category":"Проверка",
                   "StrategyId":"tracker","Tracker":{"ObservationIntervalTicks":7}}
                ]}}
                """);
            var rejected = new BotBenchmarkCommand(new("unknown", 100, 2),
                ["--contentRoot", root, "--urls", "not-a-listen-url"]);
            using var rejectedOutput = new StringWriter();
            await Assert.That(Capture(() => ConfiguredBotDiagnostics.Run(rejected, rejectedOutput))
                is ArgumentException).IsTrue();
            await Assert.That(rejectedOutput.ToString()).IsEqualTo("");
            // Fresh runs remain usable after the built, unstarted application unwinds an error.
            foreach (var (selected, expectedId, expectedCadence) in new[]
            {
                ((string?)null, "quick", 7), ("steady", "steady", 5)
            })
            {
                var arguments = new List<string> { "--bot-benchmark" };
                if (selected is not null) arguments.Add(selected);
                arguments.AddRange(["--benchmark-samples", "100", "--benchmark-warmup", "2",
                    "--contentRoot", root, "--Bots:DefaultBotId", "quick",
                    "--urls", "not-a-listen-url"]);
                var command = ConfiguredBotDiagnostics.ParseCommand(arguments.ToArray())!;
                using var output = new StringWriter();
                ConfiguredBotDiagnostics.Run(command, output);
                using var json = JsonDocument.Parse(output.ToString());
                var report = json.RootElement;
                await Assert.That(report.GetProperty("requestedBotId").GetString()).IsEqualTo(expectedId);
                await Assert.That(report.GetProperty("effectiveBotId").GetString()).IsEqualTo(expectedId);
                await Assert.That(report.GetProperty("strategyId").GetString()).IsEqualTo("tracker");
                await Assert.That(report.GetProperty("cadenceTicks").GetInt32()).IsEqualTo(expectedCadence);
                await Assert.That(report.GetProperty("samples").GetInt32()).IsEqualTo(100);
                await Assert.That(report.GetProperty("warmup").GetInt32()).IsEqualTo(2);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Measure_UsesConfiguredFactoryAndDefaultOrExplicitSelection()
    {
        var steady = Tracker("steady", cadence: 5);
        var quick = Tracker("quick", cadence: 7);
        quick.Tracker!.TargetDeadZone = 0.5;
        var runtime = Runtime([steady, quick], new TrackerBotStrategyFactory());
        var first = ConfiguredBotDiagnostics.Measure(runtime, new(Samples: 100, Warmup: 2));
        var repeated = ConfiguredBotDiagnostics.Measure(runtime, new(Samples: 100, Warmup: 2));
        var selected = ConfiguredBotDiagnostics.Measure(runtime, new("quick", 100, 2));

        await Assert.That(first.RequestedBotId).IsEqualTo("steady");
        await Assert.That(first.EffectiveBotId).IsEqualTo("steady");
        await Assert.That(first.StrategyId).IsEqualTo("tracker");
        await Assert.That(first.CadenceTicks).IsEqualTo(5);
        await Assert.That(selected.RequestedBotId).IsEqualTo("quick");
        await Assert.That(selected.EffectiveBotId).IsEqualTo("quick");
        await Assert.That(selected.CadenceTicks).IsEqualTo(7);
        await Assert.That(first.VerifiedModelSha256).IsNull();
        await Assert.That(first.Workloads.Length).IsEqualTo(2);
        await AssertValidReport(first);
        await AssertValidReport(selected);
        await Assert.That(string.Join(',', first.Workloads.Select(workload =>
            $"{workload.BatchChecksum}/{workload.SampledChecksum}")))
            .IsEqualTo(string.Join(',', repeated.Workloads.Select(workload =>
                $"{workload.BatchChecksum}/{workload.SampledChecksum}")));
    }

    [Test]
    public async Task Measure_SuppliesMonotonicVaryingStatesAndDisposesSuccessfulSession()
    {
        var factory = new ProbeFactory(_ => new ProbeController());
        var report = ConfiguredBotDiagnostics.Measure(Runtime([Tracker("probe", cadence: 7)], factory),
            new(Samples: 100, Warmup: 2));
        var controller = factory.Controllers.Single();
        await Assert.That(controller.DisposeCount).IsEqualTo(1);
        await Assert.That(controller.States.Count).IsGreaterThanOrEqualTo(400);
        await Assert.That(controller.States.All(sample => sample.State.Phase == GamePhase.Playing)).IsTrue();
        foreach (var epoch in controller.States.GroupBy(sample => sample.Epoch))
        {
            var ticks = epoch.Select(sample => sample.State.TickNumber).ToArray();
            await Assert.That(ticks.Zip(ticks.Skip(1), (before, after) => after > before).All(value => value))
                .IsTrue();
        }
        await Assert.That(controller.States.Select(sample => sample.State.BallX).Distinct().Count())
            .IsGreaterThan(2);
        await Assert.That(controller.States.Select(sample => sample.State.BallY).Distinct().Count())
            .IsGreaterThan(2);
        await Assert.That(controller.States.Select(sample => sample.State.RightY).Distinct().Count())
            .IsGreaterThan(1);
        await AssertValidReport(report);
        var differentActions = new ProbeFactory(_ => new ProbeController { FixedAxis = 0 });
        var differentReport = ConfiguredBotDiagnostics.Measure(
            Runtime([Tracker("probe", cadence: 7)], differentActions), new(Samples: 100, Warmup: 2));
        await Assert.That(report.Workloads[0].BatchChecksum != differentReport.Workloads[0].BatchChecksum)
            .IsTrue();
        await Assert.That(report.Workloads[1].SampledChecksum != differentReport.Workloads[1].SampledChecksum)
            .IsTrue();
    }

    [Test]
    public async Task Measure_RequiresVerifiedModelDigestRatherThanConfiguredHashAlone()
    {
        var model = new BotEntryOptions
        {
            Id = "model",
            Name = "Model",
            Description = "Unverified diagnostic model",
            Style = "Policy",
            Difficulty = "Practice",
            Category = "Tests",
            StrategyId = "onnx",
            Onnx = new()
        };
        var factory = new ProbeFactory(_ => new ProbeController(), BotSettingsKind.Onnx);
        var failure = Capture(() => ConfiguredBotDiagnostics.Measure(Runtime([model], factory),
            new(Samples: 100, Warmup: 2)));
        await Assert.That(failure is InvalidOperationException).IsTrue();
        await Assert.That(failure!.Message).Contains("verified");
        await Assert.That(factory.Controllers.Single().States.Count).IsEqualTo(0);
        await Assert.That(factory.Controllers.Single().DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task Measure_RejectsPreparationFallbackBeforeCollectingSamples()
    {
        var factory = new ProbeFactory(entry => new ProbeController { ThrowOnReset = entry.Id == "primary" });
        var runtime = Runtime([Tracker("primary", fallback: "backup"), Tracker("backup")], factory);
        var failure = Capture(() => ConfiguredBotDiagnostics.Measure(runtime, new(Samples: 100, Warmup: 2)));
        await Assert.That(failure is InvalidOperationException).IsTrue();
        await Assert.That(factory.Controllers.All(controller => controller.States.Count == 0)).IsTrue();
        await Assert.That(factory.Controllers.All(controller => controller.DisposeCount == 1)).IsTrue();
    }

    [Test]
    public async Task Measure_RejectsMeasuredIdentityChangesAndUnplayableSessions()
    {
        foreach (var fallback in new[] { true, false })
        {
            var factory = new ProbeFactory(entry => new ProbeController
            {
                ThrowOnCall = entry.Id == "primary" ? 5 : null
            });
            var entries = new List<BotEntryOptions> { Tracker("primary", fallback: fallback ? "backup" : null) };
            if (fallback) entries.Add(Tracker("backup"));
            var failure = Capture(() => ConfiguredBotDiagnostics.Measure(Runtime(entries, factory),
                new(Samples: 100, Warmup: 2)));
            await Assert.That(failure is InvalidOperationException).IsTrue();
            await Assert.That(factory.Controllers.First().States.Count).IsEqualTo(5);
            await Assert.That(factory.Controllers.All(controller => controller.DisposeCount == 1)).IsTrue();
        }
    }

    [Test]
    public async Task Measure_RejectsIllegalMeasuredAxesAndUnavailableSelections()
    {
        var factory = new ProbeFactory(_ => new ProbeController { InvalidAxisOnCall = 5 });
        var runtime = Runtime([Tracker("primary"), Tracker("disabled", enabled: false)], factory);
        var invalidAxis = Capture(() => ConfiguredBotDiagnostics.Measure(runtime, new("primary", 100, 2)));
        await Assert.That(invalidAxis is InvalidOperationException).IsTrue();
        await Assert.That(factory.Controllers.Single().DisposeCount).IsEqualTo(1);
        await Assert.That(Capture(() => ConfiguredBotDiagnostics.Measure(runtime, new("disabled", 100, 2)))
            is InvalidOperationException).IsTrue();
        await Assert.That(Capture(() => ConfiguredBotDiagnostics.Measure(runtime, new("unknown", 100, 2)))
            is ArgumentException).IsTrue();
        await Assert.That(factory.Controllers.Count).IsEqualTo(1);
    }

    private static async Task AssertValidReport(BotBenchmarkReport report)
    {
        await Assert.That(double.IsFinite(report.PreparationMilliseconds) && report.PreparationMilliseconds >= 0)
            .IsTrue();
        await Assert.That(string.Join(',', report.Workloads.Select(workload => workload.Name)))
            .IsEqualTo("consecutive,cadence-spaced");
        await Assert.That(report.Workloads[0].TickStride).IsEqualTo(1);
        await Assert.That(report.Workloads[1].TickStride).IsEqualTo(report.CadenceTicks);
        foreach (var workload in report.Workloads)
        {
            await Assert.That(workload.Count).IsEqualTo(100);
            await Assert.That(workload.FirstTick > 0 && workload.LastTick >= workload.FirstTick).IsTrue();
            await Assert.That(workload.BatchAllocatedBytes >= 0).IsTrue();
            await Assert.That(double.IsFinite(workload.BatchMeanMilliseconds) && workload.BatchMeanMilliseconds >= 0)
                .IsTrue();
            await AssertValidLatency(workload.Latency);
            await Assert.That(workload.TimestampFloor.BatchAllocatedBytes >= 0).IsTrue();
            await Assert.That(double.IsFinite(workload.TimestampFloor.BatchMeanMilliseconds) &&
                workload.TimestampFloor.BatchMeanMilliseconds >= 0).IsTrue();
            await AssertValidLatency(workload.TimestampFloor.Latency);
        }
    }

    private static async Task AssertValidLatency(BotBenchmarkLatency latency)
    {
        await Assert.That(latency.AllocatedBytes >= 0).IsTrue();
        await Assert.That(double.IsFinite(latency.MedianMilliseconds) &&
                latency.MedianMilliseconds >= 0 && latency.MedianMilliseconds <= latency.P95Milliseconds &&
                latency.P95Milliseconds <= latency.P99Milliseconds &&
                latency.P99Milliseconds <= latency.WorstMilliseconds && double.IsFinite(latency.WorstMilliseconds))
                .IsTrue();
    }

    private static BotRuntime Runtime(List<BotEntryOptions> entries, IBotStrategyFactory factory)
    {
        var options = new BotsOptions { DefaultBotId = entries.First(entry => entry.Enabled).Id, Entries = entries };
        var registry = new BotStrategyRegistry([new("tracker", BotSettingsKind.Tracker), new("onnx", BotSettingsKind.Onnx)]);
        var validation = new BotsOptionsValidator(registry).Validate(null, options);
        if (!validation.Succeeded) throw new InvalidOperationException(string.Join('\n', validation.Failures ?? []));
        return new(new BotCatalog(Options.Create(options)), [factory], NullLogger<BotRuntime>.Instance);
    }

    private static BotEntryOptions Tracker(string id, int cadence = 9, string? fallback = null, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Description = "Diagnostic policy",
        Style = "Tracker",
        Difficulty = "Practice",
        Category = "Tests",
        StrategyId = "tracker",
        Tracker = new() { ObservationIntervalTicks = cadence },
        FallbackBotId = fallback,
        Enabled = enabled
    };

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class ProbeFactory(Func<BotDefinition, ProbeController> create,
        BotSettingsKind kind = BotSettingsKind.Tracker) : IBotStrategyFactory
    {
        public BotStrategyDescriptor Descriptor { get; } = new(
            kind == BotSettingsKind.Onnx ? "onnx" : "tracker", kind);
        public List<ProbeController> Controllers { get; } = [];
        public ILocalOpponentController Create(BotDefinition entry)
        {
            var controller = create(entry);
            Controllers.Add(controller);
            return controller;
        }
    }

    private sealed class ProbeController : ILocalOpponentController, IDisposable
    {
        public bool ThrowOnReset { get; init; }
        public int? FixedAxis { get; init; }
        public int? ThrowOnCall { get; init; }
        public int? InvalidAxisOnCall { get; init; }
        public int ResetCount { get; private set; }
        public int DisposeCount { get; private set; }
        public List<(int Epoch, GameState State)> States { get; } = new(2048);
        public void Reset()
        {
            ResetCount++;
            if (ThrowOnReset) throw new InvalidOperationException("Preparation failed");
        }
        public int GetAxis(GameState state)
        {
            States.Add((ResetCount, state));
            if (States.Count == ThrowOnCall) throw new InvalidOperationException("Measured policy failed");
            return States.Count == InvalidAxisOnCall ? 2 : FixedAxis ?? (state.BallY > 0.5 ? 1 : -1);
        }
        public void Dispose() => DisposeCount++;
    }
}
