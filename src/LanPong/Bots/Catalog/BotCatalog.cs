using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using LanPong.Bots.Configuration;

namespace LanPong.Bots.Catalog;

internal sealed record TrackerBotSettings(int ObservationIntervalTicks, double ObservationActivationX,
    double LookAheadSeconds, double TargetDeadZone);

internal sealed record OnnxBotSettings(string ModelPath, string ExpectedSha256, int InferenceCadenceTicks);

internal sealed record BotDefinition(string Id, string Name, string Description, string Style,
    string Difficulty, string Category, int Order, string? Glyph, bool Enabled, string StrategyId,
    TrackerBotSettings? Tracker, OnnxBotSettings? Onnx, string? FallbackBotId);

/// <summary>Validated immutable application-lifetime catalog; never reads configuration on a game tick.</summary>
internal sealed class BotCatalog
{
    private readonly FrozenDictionary<string, BotDefinition> _byId;

    public BotCatalog(IOptions<BotsOptions> options)
    {
        var configuration = options.Value; // Runs all registered options validation before copying.
        DefaultBotId = configuration.DefaultBotId;
        Entries = configuration.Entries.Select(entry => new BotDefinition(
                entry.Id, entry.Name, entry.Description, entry.Style, entry.Difficulty, entry.Category,
                entry.Order, entry.Glyph, entry.Enabled, entry.StrategyId,
                entry.Tracker is { } tracker ? new TrackerBotSettings(tracker.ObservationIntervalTicks,
                    tracker.ObservationActivationX, tracker.LookAheadSeconds, tracker.TargetDeadZone) : null,
                entry.Onnx is { } onnx ? new OnnxBotSettings(onnx.ModelPath,
                    onnx.ExpectedSha256.ToLowerInvariant(), onnx.InferenceCadenceTicks) : null,
                entry.FallbackBotId))
            .OrderBy(entry => entry.Order).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToImmutableArray();
        _byId = Entries.ToFrozenDictionary(entry => entry.Id, StringComparer.Ordinal);
    }

    public string DefaultBotId { get; }
    public BotDefinition DefaultBot => _byId[DefaultBotId];
    public ImmutableArray<BotDefinition> Entries { get; }
    public bool TryGet(string id, out BotDefinition? definition) => _byId.TryGetValue(id, out definition);
}
