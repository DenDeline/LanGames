using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanPong.Tests;

public sealed class BotCatalogTests
{
    [Test]
    public async Task ProductionBinding_CopiesMetadataTypedSettingsAndOrdersIdsOrdinally()
    {
        var values = ValidConfiguration();
        values["Bots:Entries:0:Tracker:ObservationIntervalTicks"] = "13";
        values["Bots:Entries:0:Tracker:ObservationActivationX"] = "0.61";
        values["Bots:Entries:0:Tracker:LookAheadSeconds"] = "0.37";
        values["Bots:Entries:0:Tracker:TargetDeadZone"] = "0.024";
        values["Bots:Entries:1:Onnx:InferenceCadenceTicks"] = "18";
        values["Bots:Entries:1:Onnx:ExpectedSha256"] = BotModelV1.ExpectedSha256.ToUpperInvariant();
        using var services = BuildServices(values);
        var catalog = services.GetRequiredService<BotCatalog>();

        await Assert.That(string.Join(',', catalog.Entries.Select(entry => entry.Id)))
            .IsEqualTo("model,alpha,steady");
        await Assert.That(catalog.DefaultBotId).IsEqualTo("steady");
        await Assert.That(catalog.DefaultBot.Name).IsEqualTo("Спокойный");
        await Assert.That(catalog.DefaultBot.Description).IsEqualTo("Следит за поздним подходом мяча.");
        await Assert.That(catalog.DefaultBot.Style).IsEqualTo("Размеренный");
        await Assert.That(catalog.DefaultBot.Difficulty).IsEqualTo("Лёгкий");
        await Assert.That(catalog.DefaultBot.Category).IsEqualTo("Тренировка");
        await Assert.That(catalog.DefaultBot.Glyph).IsEqualTo("●");
        await Assert.That(catalog.DefaultBot.Enabled).IsTrue();
        await Assert.That(catalog.DefaultBot.Tracker)
            .IsEqualTo(new TrackerBotSettings(13, 0.61, 0.37, 0.024));
        await Assert.That(catalog.TryGet("model", out var model)).IsTrue();
        await Assert.That(model!.FallbackBotId).IsEqualTo("steady");
        await Assert.That(model.Onnx!.ExpectedSha256).IsEqualTo(BotModelV1.ExpectedSha256);
        await Assert.That(model.Onnx.InferenceCadenceTicks).IsEqualTo(18);
        await Assert.That(catalog.TryGet("STEADY", out _)).IsFalse();
        await Assert.That(catalog.TryGet("missing", out _)).IsFalse();
        await Assert.That(catalog.TryGet("alpha", out var disabled)).IsTrue();
        await Assert.That(disabled!.Enabled).IsFalse();
    }

    [Test]
    public async Task ProductionBinding_PreservesCalibratedDefaultsWhenSettingsArePartial()
    {
        using var services = BuildServices(ValidConfiguration());
        var catalog = services.GetRequiredService<BotCatalog>();
        await Assert.That(catalog.DefaultBot.Tracker).IsEqualTo(CalibratedTracker());
        await Assert.That(catalog.TryGet("model", out var model)).IsTrue();
        await Assert.That(model!.Onnx!.ExpectedSha256).IsEqualTo(BotModelV1.ExpectedSha256);
        await Assert.That(model.Onnx.InferenceCadenceTicks).IsEqualTo(RightBotObservationV1.InferenceCadenceTicks);
        await Assert.That(new OnnxBotOptions().ModelPath).IsEqualTo("Models/hard-v1.onnx");
    }

    [Test]
    public async Task ConfiguredCatalog_ContainsReusableTrackerProfilesAndCalibratedModel()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(ProjectDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        using var services = new ServiceCollection().AddBotCatalog(configuration).BuildServiceProvider();
        var catalog = services.GetRequiredService<BotCatalog>();
        var trackers = catalog.Entries.Where(entry => entry.StrategyId == BotStrategyDescriptor.TrackerId).ToArray();
        var models = catalog.Entries.Where(entry => entry.StrategyId == BotStrategyDescriptor.OnnxId).ToArray();

        await Assert.That(catalog.Entries.Length).IsGreaterThanOrEqualTo(3);
        await Assert.That(trackers.Length).IsGreaterThanOrEqualTo(2);
        await Assert.That(trackers.Select(entry => entry.Tracker).Distinct().Count()).IsGreaterThanOrEqualTo(2);
        await Assert.That(trackers.Any(entry => entry.Tracker == CalibratedTracker())).IsTrue();
        await Assert.That(models.Any(entry => entry.Onnx == new OnnxBotSettings("Models/hard-v1.onnx",
            BotModelV1.ExpectedSha256, RightBotObservationV1.InferenceCadenceTicks))).IsTrue();
        await Assert.That(catalog.DefaultBot.Enabled).IsTrue();
    }

    [Test]
    public async Task Startup_RejectsInvalidConfigurationBeforeCatalogIsResolved()
    {
        var values = ValidConfiguration();
        values["Bots:DefaultBotId"] = "alpha"; // Configured but disabled.
        using var host = BuildHost(values);
        var error = await CaptureAsync(() => host.StartAsync());

        await Assert.That(error is OptionsValidationException).IsTrue();
        await Assert.That(error!.Message).Contains("Bots:DefaultBotId");
        await Assert.That(error.Message).Contains("enabled bot");
    }

    [Test]
    public async Task Startup_AcceptsMissingModelWithoutInitializingNativeInference()
    {
        var values = ValidConfiguration();
        var missing = Path.Combine(Path.GetTempPath(), $"missing-catalog-model-{Guid.NewGuid():N}.onnx");
        values["Bots:DefaultBotId"] = "model";
        values["Bots:Entries:1:Onnx:ModelPath"] = missing;
        values.Remove("Bots:Entries:1:FallbackBotId");
        using var host = BuildHost(values);
        await host.StartAsync();
        try
        {
            var catalog = host.Services.GetRequiredService<BotCatalog>();
            await Assert.That(catalog.DefaultBot.Onnx!.ModelPath).IsEqualTo(missing);
            await Assert.That(catalog.DefaultBot.FallbackBotId).IsNull();
            await Assert.That(File.Exists(missing)).IsFalse();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Test]
    public async Task ProductionBinding_RejectsUnknownPropertiesAndMalformedConversions()
    {
        foreach (var (key, value, diagnostic) in new[]
        {
            ("Bots:Entries:0:Tracker:LookAheadSecond", "0.2", "LookAheadSecond"),
            ("Bots:Entries:0:Tracker:ObservationIntervalTicks", "every-nine", "Bots:Entries:0:Tracker:ObservationIntervalTicks"),
            ("Bots:Entries:0:Enabled", "sometimes", "Bots:Entries:0:Enabled"),
            ("Bots:DefautBotId", "steady", "DefautBotId")
        })
        {
            var values = ValidConfiguration();
            values[key] = value;
            using var services = BuildServices(values);
            var error = Capture(() => services.GetRequiredService<BotCatalog>());
            await Assert.That(error is InvalidOperationException).IsTrue();
            await Assert.That(error!.Message).Contains(diagnostic);
        }
    }

    [Test]
    public async Task Catalog_IsIsolatedFromMutableOptionsAndConfigurationReload()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidConfiguration()).Build();
        using var services = new ServiceCollection().AddBotCatalog(configuration).BuildServiceProvider();
        var catalog = services.GetRequiredService<BotCatalog>();
        var options = services.GetRequiredService<IOptions<BotsOptions>>().Value;
        options.DefaultBotId = "model";
        options.Entries[0].Id = "changed";
        options.Entries[0].Name = "Changed";
        options.Entries[0].Tracker!.TargetDeadZone = 0.5;
        options.Entries[1].Onnx!.ExpectedSha256 = new string('a', 64);
        options.Entries.Clear();
        configuration["Bots:DefaultBotId"] = "alpha";
        configuration["Bots:Entries:0:Name"] = "Reloaded";
        configuration.Reload();

        await Assert.That(ReferenceEquals(catalog, services.GetRequiredService<BotCatalog>())).IsTrue();
        await Assert.That(catalog.DefaultBotId).IsEqualTo("steady");
        await Assert.That(catalog.DefaultBot.Name).IsEqualTo("Спокойный");
        await Assert.That(catalog.DefaultBot.Tracker).IsEqualTo(CalibratedTracker());
        await Assert.That(catalog.Entries.Length).IsEqualTo(3);
        await Assert.That(catalog.TryGet("steady", out _)).IsTrue();
        await Assert.That(catalog.TryGet("changed", out _)).IsFalse();
        await Assert.That(catalog.TryGet("model", out var model)).IsTrue();
        await Assert.That(model!.Onnx!.ExpectedSha256).IsEqualTo(BotModelV1.ExpectedSha256);
    }

    [Test]
    public async Task StartupSnapshot_IgnoresInvalidConfigurationReloadForMonitorAndCatalog()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidConfiguration()).Build();
        using var host = BuildHost(configuration);
        await host.StartAsync();
        try
        {
            var catalog = host.Services.GetRequiredService<BotCatalog>();
            var monitor = host.Services.GetRequiredService<IOptionsMonitor<BotsOptions>>();
            var startupOptions = monitor.CurrentValue;
            configuration["Bots:DefaultBotId"] = "missing";
            configuration["Bots:Entries:0:Tracker:ObservationIntervalTicks"] = "0";
            configuration["Bots:Entries:0:Tracker:LookAheadSeconds"] = "not-a-number";

            var error = Capture(configuration.Reload);

            await Assert.That(error).IsNull();
            await Assert.That(ReferenceEquals(startupOptions, monitor.CurrentValue)).IsTrue();
            await Assert.That(monitor.CurrentValue.DefaultBotId).IsEqualTo("steady");
            await Assert.That(monitor.CurrentValue.Entries[0].Tracker!.ObservationIntervalTicks)
                .IsEqualTo(TrackerBotPolicy.ObservationIntervalTicks);
            await Assert.That(monitor.CurrentValue.Entries[0].Tracker!.LookAheadSeconds)
                .IsEqualTo(TrackerBotPolicy.LookAheadSeconds);
            await Assert.That(ReferenceEquals(catalog, host.Services.GetRequiredService<BotCatalog>())).IsTrue();
            await Assert.That(catalog.DefaultBotId).IsEqualTo("steady");
            await Assert.That(catalog.DefaultBot.Tracker).IsEqualTo(CalibratedTracker());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Test]
    public async Task Validation_RejectsMissingCatalogNullEntriesAndMissingDefault()
    {
        await AssertFailure(new BotsOptions(), "Bots:DefaultBotId", "Bots:Entries");
        var options = ValidOptions();
        options.Entries = null!;
        await AssertFailure(options, "Bots:Entries", "does not reference a configured bot");
        options = ValidOptions();
        options.Entries.Add(null!);
        await AssertFailure(options, "Bots:Entries:1 must contain a bot definition");
        options = ValidOptions();
        options.DefaultBotId = "absent";
        await AssertFailure(options, "Bots:DefaultBotId 'absent'", "does not reference a configured bot");

        using var services = BuildServices([]);
        var error = Capture(() => services.GetRequiredService<BotCatalog>());
        await Assert.That(error is OptionsValidationException).IsTrue();
        await Assert.That(error!.Message).Contains("Bots:Entries");
    }

    [Test]
    public async Task Validation_RejectsMalformedAndDuplicateIds()
    {
        foreach (var id in new string?[] { null, "", "Steady", "0steady", "steady ", "steady--bot",
                     "steady_bot", "steady-", "-steady", "спокойный", new string('a', 65) })
        {
            var options = ValidOptions();
            options.Entries[0].Id = id!;
            await AssertFailure(options, "Bots:Entries:0:Id", "lowercase ASCII ID");
        }
        var duplicate = ValidOptions();
        duplicate.Entries.Add(TrackerEntry("steady"));
        await AssertFailure(duplicate, "Bots:Entries:1:Id duplicates bot 'steady' at Bots:Entries:0:Id");

        var valid = ValidOptions();
        valid.Entries[0].Id = valid.DefaultBotId = "a1-b2";
        await Assert.That(Validator().Validate(null, valid).Succeeded).IsTrue();
    }

    [Test]
    public async Task Validation_RejectsBlankOrOversizedMetadataAndNegativeOrder()
    {
        var cases = new (string Field, Action<BotEntryOptions> Change)[]
        {
            ("Name", entry => entry.Name = null!),
            ("Name", entry => entry.Name = new string('n', 65)),
            ("Description", entry => entry.Description = " "),
            ("Description", entry => entry.Description = new string('d', 513)),
            ("Style", entry => entry.Style = ""),
            ("Style", entry => entry.Style = new string('s', 129)),
            ("Difficulty", entry => entry.Difficulty = ""),
            ("Difficulty", entry => entry.Difficulty = new string('d', 65)),
            ("Category", entry => entry.Category = ""),
            ("Category", entry => entry.Category = new string('c', 65)),
            ("Glyph", entry => entry.Glyph = " "),
            ("Glyph", entry => entry.Glyph = new string('g', 17)),
            ("Order", entry => entry.Order = -1)
        };
        foreach (var (field, change) in cases)
        {
            var options = ValidOptions();
            change(options.Entries[0]);
            await AssertFailure(options, $"Bots:Entries:0:{field}");
        }
        await Assert.That(Validator().Validate(null, ValidOptions()).Succeeded).IsTrue(); // Glyph is optional.
    }

    [Test]
    public async Task Validation_RejectsUnknownStrategiesAndMismatchedSettingsEvenWhenDisabled()
    {
        var options = ValidOptions();
        options.Entries[0].StrategyId = "unregistered";
        await AssertFailure(options, "Bots:Entries:0:StrategyId 'unregistered'", "Known strategies: onnx, tracker");
        options = ValidOptions();
        options.Entries[0].StrategyId = "Tracker";
        await AssertFailure(options, "Bots:Entries:0:StrategyId", "lowercase ASCII ID");
        options = ValidOptions();
        options.Entries[0].Tracker = null;
        await AssertFailure(options, "Bots:Entries:0:Tracker is required");
        options = ValidOptions();
        options.Entries[0].Onnx = new OnnxBotOptions();
        await AssertFailure(options, "Bots:Entries:0:Onnx is not valid");
        options = ValidOptions();
        options.Entries[0].StrategyId = "onnx";
        await AssertFailure(options, "Bots:Entries:0:Onnx is required", "Bots:Entries:0:Tracker is not valid");
        options = ValidOptions();
        var disabled = TrackerEntry("disabled");
        disabled.Enabled = false;
        disabled.Tracker!.ObservationIntervalTicks = 0;
        disabled.Name = "";
        options.Entries.Add(disabled);
        await AssertFailure(options, "Bots:Entries:1:Tracker:ObservationIntervalTicks", "Bots:Entries:1:Name");
    }

    [Test]
    public async Task Validation_RejectsNonfiniteAndOutOfRangeTrackerSettings()
    {
        var cases = new (string Field, Action<TrackerBotOptions> Change)[]
        {
            ("ObservationIntervalTicks", settings => settings.ObservationIntervalTicks = 0),
            ("ObservationIntervalTicks", settings => settings.ObservationIntervalTicks = 601),
            ("ObservationActivationX", settings => settings.ObservationActivationX = double.NaN),
            ("ObservationActivationX", settings => settings.ObservationActivationX = double.PositiveInfinity),
            ("ObservationActivationX", settings => settings.ObservationActivationX = -0.001),
            ("ObservationActivationX", settings => settings.ObservationActivationX = GameConstants.RightContactX),
            ("LookAheadSeconds", settings => settings.LookAheadSeconds = double.NaN),
            ("LookAheadSeconds", settings => settings.LookAheadSeconds = double.NegativeInfinity),
            ("LookAheadSeconds", settings => settings.LookAheadSeconds = -0.001),
            ("LookAheadSeconds", settings => settings.LookAheadSeconds = 2.001),
            ("TargetDeadZone", settings => settings.TargetDeadZone = double.NaN),
            ("TargetDeadZone", settings => settings.TargetDeadZone = double.PositiveInfinity),
            ("TargetDeadZone", settings => settings.TargetDeadZone = -0.001),
            ("TargetDeadZone", settings => settings.TargetDeadZone = 0.501)
        };
        foreach (var (field, change) in cases)
        {
            var options = ValidOptions();
            change(options.Entries[0].Tracker!);
            await AssertFailure(options, $"Bots:Entries:0:Tracker:{field}");
        }
        var limits = ValidOptions();
        limits.Entries[0].Tracker = new TrackerBotOptions
        {
            ObservationIntervalTicks = 600, ObservationActivationX = 0,
            LookAheadSeconds = 2, TargetDeadZone = 0.5
        };
        await Assert.That(Validator().Validate(null, limits).Succeeded).IsTrue();
        limits.Entries[0].Tracker!.ObservationIntervalTicks = 1;
        limits.Entries[0].Tracker!.LookAheadSeconds = 0;
        limits.Entries[0].Tracker!.TargetDeadZone = 0;
        await Assert.That(Validator().Validate(null, limits).Succeeded).IsTrue();
    }

    [Test]
    public async Task Validation_RejectsInvalidOnnxPathChecksumAndCadence()
    {
        var cases = new (string Field, Action<OnnxBotOptions> Change)[]
        {
            ("ModelPath", settings => settings.ModelPath = null!),
            ("ModelPath", settings => settings.ModelPath = " "),
            ("ModelPath", settings => settings.ModelPath = "model\0.onnx"),
            ("ExpectedSha256", settings => settings.ExpectedSha256 = null!),
            ("ExpectedSha256", settings => settings.ExpectedSha256 = new string('a', 63)),
            ("ExpectedSha256", settings => settings.ExpectedSha256 = new string('z', 64)),
            ("InferenceCadenceTicks", settings => settings.InferenceCadenceTicks = 0),
            ("InferenceCadenceTicks", settings => settings.InferenceCadenceTicks = 601)
        };
        foreach (var (field, change) in cases)
        {
            var options = ValidOptions();
            options.Entries[0].StrategyId = "onnx";
            options.Entries[0].Tracker = null;
            options.Entries[0].Onnx = new OnnxBotOptions();
            change(options.Entries[0].Onnx!);
            await AssertFailure(options, $"Bots:Entries:0:Onnx:{field}");
        }
    }

    [Test]
    public async Task Validation_RejectsMissingDisabledAndMalformedFallbacksAndDisabledDefault()
    {
        var options = ValidOptions();
        options.Entries[0].Enabled = false;
        await AssertFailure(options, "Bots:DefaultBotId 'steady' must reference an enabled bot");
        options = ValidOptions();
        options.Entries[0].FallbackBotId = "missing";
        await AssertFailure(options, "Bots:Entries:0:FallbackBotId 'missing'", "does not reference a configured bot");
        options = ValidOptions();
        options.Entries[0].FallbackBotId = "";
        await AssertFailure(options, "Bots:Entries:0:FallbackBotId", "lowercase ASCII ID");
        options = ValidOptions();
        options.Entries[0].FallbackBotId = "Steady";
        await AssertFailure(options, "Bots:Entries:0:FallbackBotId", "lowercase ASCII ID");
        options = ValidOptions();
        var disabled = TrackerEntry("disabled");
        disabled.Enabled = false;
        options.Entries.Add(disabled);
        options.Entries[0].FallbackBotId = "disabled";
        await AssertFailure(options, "Bots:Entries:0:FallbackBotId 'disabled' must reference an enabled bot");
    }

    [Test]
    public async Task Validation_RejectsSelfAndMultiEntryFallbackCyclesButAcceptsChains()
    {
        var options = ValidOptions();
        options.Entries[0].FallbackBotId = "steady";
        await AssertFailure(options, "Bots:Entries:0:FallbackBotId forms a cycle: steady -> steady");
        options = ValidOptions();
        options.Entries.Add(TrackerEntry("second"));
        options.Entries.Add(TrackerEntry("third"));
        options.Entries[0].FallbackBotId = "second";
        options.Entries[1].FallbackBotId = "third";
        options.Entries[2].FallbackBotId = "second";
        await AssertFailure(options, "forms a cycle: second -> third -> second");
        options.Entries[2].FallbackBotId = null;
        await Assert.That(Validator().Validate(null, options).Succeeded).IsTrue();
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> values) => new ServiceCollection()
        .AddBotCatalog(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
        .BuildServiceProvider();

    private static IHost BuildHost(Dictionary<string, string?> values) =>
        BuildHost(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    private static IHost BuildHost(IConfiguration configuration)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddBotCatalog(configuration);
        return builder.Build();
    }

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["Bots:DefaultBotId"] = "steady",
        ["Bots:Entries:0:Id"] = "steady",
        ["Bots:Entries:0:Name"] = "Спокойный",
        ["Bots:Entries:0:Description"] = "Следит за поздним подходом мяча.",
        ["Bots:Entries:0:Style"] = "Размеренный",
        ["Bots:Entries:0:Difficulty"] = "Лёгкий",
        ["Bots:Entries:0:Category"] = "Тренировка",
        ["Bots:Entries:0:Order"] = "20",
        ["Bots:Entries:0:Glyph"] = "●",
        ["Bots:Entries:0:StrategyId"] = "tracker",
        ["Bots:Entries:0:Tracker:ObservationIntervalTicks"] = "9",
        ["Bots:Entries:1:Id"] = "model",
        ["Bots:Entries:1:Name"] = "Модель",
        ["Bots:Entries:1:Description"] = "Существующая обученная модель.",
        ["Bots:Entries:1:Style"] = "Предугадывает",
        ["Bots:Entries:1:Difficulty"] = "Сложный",
        ["Bots:Entries:1:Category"] = "Испытание",
        ["Bots:Entries:1:Order"] = "10",
        ["Bots:Entries:1:StrategyId"] = "onnx",
        ["Bots:Entries:1:Onnx:ModelPath"] = "Models/missing.onnx",
        ["Bots:Entries:1:FallbackBotId"] = "steady",
        ["Bots:Entries:2:Id"] = "alpha",
        ["Bots:Entries:2:Name"] = "Альфа",
        ["Bots:Entries:2:Description"] = "Отключённый профиль.",
        ["Bots:Entries:2:Style"] = "Экспериментальный",
        ["Bots:Entries:2:Difficulty"] = "Средний",
        ["Bots:Entries:2:Category"] = "Тренировка",
        ["Bots:Entries:2:Order"] = "20",
        ["Bots:Entries:2:Enabled"] = "false",
        ["Bots:Entries:2:StrategyId"] = "tracker",
        ["Bots:Entries:2:Tracker:ObservationIntervalTicks"] = "6"
    };

    private static BotsOptions ValidOptions() => new() { DefaultBotId = "steady", Entries = [TrackerEntry("steady")] };

    private static BotEntryOptions TrackerEntry(string id) => new()
    {
        Id = id, Name = "Спокойный", Description = "Тренировочный соперник.", Style = "Размеренный",
        Difficulty = "Лёгкий", Category = "Тренировка", StrategyId = "tracker", Tracker = new TrackerBotOptions()
    };

    private static TrackerBotSettings CalibratedTracker() => new(
        TrackerBotPolicy.ObservationIntervalTicks, TrackerBotPolicy.ObservationActivationX,
        TrackerBotPolicy.LookAheadSeconds, TrackerBotPolicy.TargetDeadZone);

    private static BotsOptionsValidator Validator() => new(new BotStrategyRegistry([
        new BotStrategyDescriptor("tracker", BotSettingsKind.Tracker), new BotStrategyDescriptor("onnx", BotSettingsKind.Onnx)]));

    private static async Task AssertFailure(BotsOptions options, params string[] diagnostics)
    {
        var result = Validator().Validate(null, options);
        await Assert.That(result.Failed).IsTrue();
        var failures = string.Join('\n', result.Failures ?? []);
        foreach (var diagnostic in diagnostics) await Assert.That(failures).Contains(diagnostic);
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static string ProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "LanPong");
            if (File.Exists(Path.Combine(path, "appsettings.json"))) return path;
        }
        throw new DirectoryNotFoundException("Could not locate the LanPong appsettings.json file.");
    }
}
