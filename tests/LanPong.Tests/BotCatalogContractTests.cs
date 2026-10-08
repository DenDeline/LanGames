using System.Buffers;
using System.Text.Json;
using LanPong.Bots.Catalog;
using LanPong.Bots.Runtime;
using LanPong.Bots.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPong.Tests;

public sealed class BotCatalogContractTests
{
    [Test]
    public async Task Discovery_ReturnsOrderedMetadataAndDefaultWithoutCreatingControllersOrExposingSettings()
    {
        var defaultBot = BotTestSupport.Tracker("default-bot", "Default bot");
        defaultBot.Order = 20;
        defaultBot.Glyph = "●";
        defaultBot.Description = "Description from configuration";
        defaultBot.Style = "Patient";
        defaultBot.Difficulty = "Practice";
        defaultBot.Category = "Training";
        var model = BotTestSupport.Onnx("model", "default-bot", "/private/model-secret/never-open.onnx");
        model.Order = 0;
        model.Onnx!.ExpectedSha256 = new string('a', 64);
        var alpha = BotTestSupport.Tracker("alpha");
        alpha.Order = 20;
        var disabled = BotTestSupport.Tracker("disabled", enabled: false);
        disabled.Order = 30;
        var models = Factory(BotSettingsKind.Onnx, _ => throw new InvalidOperationException("Discovery loaded a model."));
        var trackers = Factory(BotSettingsKind.Tracker, _ => throw new InvalidOperationException("Discovery created a policy."));
        var runtime = BotTestSupport.Runtime([defaultBot, model, alpha, disabled], models, trackers);

        var catalog = runtime.DescribeCatalog();
        runtime.DescribeCatalog();
        await Assert.That(catalog.Version).IsEqualTo(8);
        await Assert.That(catalog.DefaultBotId).IsEqualTo("default-bot");
        await Assert.That(string.Join(',', catalog.Bots.Select(bot => bot.Id))).IsEqualTo("model,alpha,default-bot,disabled");
        await Assert.That(models.Definitions.Count).IsEqualTo(0);
        await Assert.That(trackers.Definitions.Count).IsEqualTo(0);
        var descriptor = catalog.Bots.Single(bot => bot.Id == "default-bot");
        await Assert.That(descriptor.Name).IsEqualTo("Default bot");
        await Assert.That(descriptor.Description).IsEqualTo("Description from configuration");
        await Assert.That(descriptor.Style).IsEqualTo("Patient");
        await Assert.That(descriptor.Difficulty).IsEqualTo("Practice");
        await Assert.That(descriptor.Category).IsEqualTo("Training");
        await Assert.That(descriptor.Order).IsEqualTo(20);
        await Assert.That(descriptor.Glyph).IsEqualTo("●");
        await Assert.That(descriptor.Enabled).IsTrue();
        await Assert.That(descriptor.Availability).IsEqualTo(BotAvailabilityState.Ready);
        await Assert.That(descriptor.AvailabilityReason).IsNull();
        await Assert.That(descriptor.CanPlay).IsTrue();
        await Assert.That(catalog.Bots[0].FallbackBotId).IsEqualTo("default-bot");
        await Assert.That(catalog.Bots[0].Availability).IsEqualTo(BotAvailabilityState.NotChecked);
        await Assert.That(catalog.Bots[0].AvailabilityReason).IsEqualTo("Модель будет проверена при выборе бота.");
        await Assert.That(catalog.Bots[0].CanPlay).IsTrue();
        await Assert.That(catalog.Bots[^1].Availability).IsEqualTo(BotAvailabilityState.Disabled);
        await Assert.That(catalog.Bots[^1].CanPlay).IsFalse();

        var serialized = JsonSerializer.Serialize(catalog, AppJsonSerializerContext.Default.BotCatalogResponse);
        using var json = JsonDocument.Parse(serialized);
        await Assert.That(json.RootElement.GetProperty("version").GetInt32()).IsEqualTo(8);
        await Assert.That(json.RootElement.GetProperty("defaultBotId").GetString()).IsEqualTo("default-bot");
        var publicBots = json.RootElement.GetProperty("bots");
        await Assert.That(publicBots[0].GetProperty("availability").GetString()).IsEqualTo("notChecked");
        await Assert.That(publicBots[1].GetProperty("availability").GetString()).IsEqualTo("ready");
        await Assert.That(publicBots[3].GetProperty("availability").GetString()).IsEqualTo("disabled");
        foreach (var bot in publicBots.EnumerateArray())
        {
            var properties = bot.EnumerateObject().Select(property => property.Name).Order().ToArray();
            await Assert.That(properties).IsEquivalentTo(new[]
            {
                "id", "name", "description", "style", "difficulty", "category", "order", "glyph",
                "enabled", "fallbackBotId", "availability", "availabilityReason", "canPlay"
            });
        }
        await Assert.That(serialized.Contains("model-secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(serialized.Contains(new string('a', 64), StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Discovery_ReportsLatchedFailureAndFollowsOnlyExplicitPlayableFallbackChain()
    {
        var models = Factory(BotSettingsKind.Onnx, _ => new TrackedBotController { FailReset = _ => true });
        var trackers = Factory(BotSettingsKind.Tracker,
            _ => new TrackedBotController { FailAxis = _ => true });
        var runtime = BotTestSupport.Runtime([
            BotTestSupport.Onnx("alone"), BotTestSupport.Onnx("primary", "backup"),
            BotTestSupport.Onnx("backup", "rescue"), BotTestSupport.Tracker("rescue"),
            BotTestSupport.Tracker("unrelated")
        ], models, trackers);
        await Assert.That(runtime.DescribeCatalog().Bots.All(bot => bot.CanPlay)).IsTrue();
        var alone = await BotTestSupport.CaptureAsync(() => Task.Run(() => runtime.Prepare("alone")));
        await Assert.That(alone is BotUnavailableException).IsTrue();
        using var primary = runtime.Prepare("primary");
        var known = runtime.DescribeCatalog();
        await Assert.That(Find(known, "alone").Availability).IsEqualTo(BotAvailabilityState.Unavailable);
        await Assert.That(Find(known, "alone").CanPlay).IsFalse();
        await Assert.That(Find(known, "primary").Availability).IsEqualTo(BotAvailabilityState.Unavailable);
        await Assert.That(Find(known, "primary").CanPlay).IsTrue();
        await Assert.That(Find(known, "backup").CanPlay).IsTrue();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(known,
            AppJsonSerializerContext.Default.BotCatalogResponse));
        var unavailable = json.RootElement.GetProperty("bots").EnumerateArray()
            .Single(bot => bot.GetProperty("id").GetString() == "primary");
        await Assert.That(unavailable.GetProperty("availability").GetString()).IsEqualTo("unavailable");
        await Assert.That(unavailable.GetProperty("availabilityReason").GetString())
            .IsEqualTo("Модель бота не прошла проверку.");

        primary.GetAxis(new GameState { Phase = GamePhase.Playing });
        BotRuntime.DisposeRetired(primary.TakeRetiredControllers());
        var exhausted = runtime.DescribeCatalog();
        await Assert.That(Find(exhausted, "primary").CanPlay).IsFalse();
        await Assert.That(Find(exhausted, "backup").CanPlay).IsFalse();
        await Assert.That(Find(exhausted, "rescue").CanPlay).IsFalse();
        await Assert.That(Find(exhausted, "unrelated").CanPlay).IsTrue();
        await Assert.That(models.Definitions.Count).IsEqualTo(3);
        await Assert.That(trackers.Definitions.Count).IsEqualTo(1);
        await Assert.That(trackers.Definitions.Single().Id).IsEqualTo("rescue");
    }

    [Test]
    public async Task SelectedIdentity_UsesFullBotNamesSeparateFromHumanNicknameInGeneratedJsonAndMessagePack()
    {
        var requestedName = new string('М', 64);
        var effectiveName = new string('Т', 64);
        var humanNickname = new string('И', 24);
        var requested = BotTestSupport.Onnx("requested", "fallback");
        requested.Name = requestedName;
        var runtime = BotTestSupport.Runtime([
            requested, BotTestSupport.Tracker("fallback", effectiveName)
        ], Factory(BotSettingsKind.Onnx, _ => new TrackedBotController { FailReset = _ => true }),
            new TrackerBotStrategyFactory());
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance, runtime);
        await peer.StartBotAsync(humanNickname, "requested");
        var snapshot = peer.Snapshot();

        await Assert.That(snapshot.RequestedBotId).IsEqualTo("requested");
        await Assert.That(snapshot.RequestedBotName).IsEqualTo(requestedName);
        await Assert.That(snapshot.EffectiveBotId).IsEqualTo("fallback");
        await Assert.That(snapshot.EffectiveBotName).IsEqualTo(effectiveName);
        await Assert.That(snapshot.LocalNickname).IsEqualTo(humanNickname);
        await Assert.That(snapshot.PeerNickname).IsNull();
        await Assert.That(snapshot.OpponentMode).IsEqualTo(OpponentMode.Bot);
        await Assert.That(snapshot.OpponentFallbackActive).IsTrue();
        await Assert.That(snapshot.BotFallbackReason).IsEqualTo("Модель бота не прошла проверку.");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot,
            AppJsonSerializerContext.Default.PongSnapshot));
        await Assert.That(json.RootElement.GetProperty("requestedBotName").GetString()).IsEqualTo(requestedName);
        await Assert.That(json.RootElement.GetProperty("effectiveBotName").GetString()).IsEqualTo(effectiveName);
        await Assert.That(json.RootElement.GetProperty("localNickname").GetString()).IsEqualTo(humanNickname);
        await Assert.That(json.RootElement.GetProperty("peerNickname").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(json.RootElement.TryGetProperty("requestedOpponentMode", out _)).IsFalse();
        var buffer = new ArrayBufferWriter<byte>();
        BrowserWebSocketProtocol.WriteSnapshot(snapshot, buffer);
        var (fields, version, decoded, atEnd) = BrowserWebSocketProtocolTests.ReadSnapshot(buffer.WrittenMemory);
        await Assert.That(fields).IsEqualTo(30);
        await Assert.That(version).IsEqualTo(8);
        await Assert.That(atEnd).IsTrue();
        await Assert.That(decoded with
        {
            LocalAddresses = snapshot.LocalAddresses, RecentEvents = snapshot.RecentEvents
        }).IsEqualTo(snapshot);
    }

    [Test]
    public async Task SelectionRequest_RequiresExactBotIdAndRejectsMissingBlankDisabledAndUnknownValues()
    {
        var factory = Factory(BotSettingsKind.Tracker, _ => new TrackedBotController());
        await using var peer = new PongPeer(NullLogger<PongPeer>.Instance,
            BotTestSupport.Runtime([
                BotTestSupport.Tracker("available"), BotTestSupport.Tracker("disabled", enabled: false)
            ], factory));
        var missing = JsonSerializer.Deserialize("{\"nickname\":\"Игрок\"}",
            AppJsonSerializerContext.Default.LocalOpponentRequest)!;
        await Assert.That(missing.BotId).IsNull();
        foreach (var (id, reason) in new (string? Id, string Reason)[]
        {
            (missing.BotId, "Выберите бота."), ("", "Выберите бота."), ("  ", "Выберите бота."),
            ("unknown", "Неизвестный бот."), ("AVAILABLE", "Неизвестный бот."),
            (" available", "Неизвестный бот."), ("disabled", "Этот бот отключён.")
        })
        {
            var error = await BotTestSupport.CaptureAsync(() => peer.StartBotAsync(missing.Nickname, id));
            await Assert.That(error).IsNotNull();
            await Assert.That(error!.Message).IsEqualTo(reason);
            await Assert.That(peer.BotStatus).IsNull();
            await Assert.That(peer.Snapshot().Connection).IsEqualTo(ConnectionState.Idle);
        }
        await Assert.That(factory.Definitions.Count).IsEqualTo(0);
        var tooLong = await BotTestSupport.CaptureAsync(() => peer.StartBotAsync(new string('И', 25), "available"));
        await Assert.That(tooLong is ArgumentException).IsTrue();
        await Assert.That(factory.Definitions.Count).IsEqualTo(0);
        var selected = JsonSerializer.Deserialize("{\"nickname\":\"Игрок\",\"botId\":\"available\"}",
            AppJsonSerializerContext.Default.LocalOpponentRequest)!;
        await peer.StartBotAsync(selected.Nickname, selected.BotId);
        await Assert.That(peer.Snapshot().RequestedBotId).IsEqualTo("available");
    }

    private static BotDescriptor Find(BotCatalogResponse catalog, string id) => catalog.Bots.Single(bot => bot.Id == id);

    private static TestBotFactory Factory(BotSettingsKind kind, Func<BotDefinition, ILocalOpponentController> create) =>
        new(kind == BotSettingsKind.Onnx ? BotStrategyDescriptor.OnnxId : BotStrategyDescriptor.TrackerId, kind, create);
}
