using Microsoft.Extensions.Hosting;

namespace LanPong;

/// <summary>Creates a fresh policy for one configured entry; preparation belongs to BotRuntime.</summary>
internal interface IBotStrategyFactory
{
    BotStrategyDescriptor Descriptor { get; }
    ILocalOpponentController Create(BotDefinition entry);
}

internal sealed class TrackerBotStrategyFactory : IBotStrategyFactory
{
    public BotStrategyDescriptor Descriptor { get; } = new(BotStrategyDescriptor.TrackerId, BotSettingsKind.Tracker);

    public ILocalOpponentController Create(BotDefinition entry) =>
        new SimpleLocalOpponentController(entry.Tracker ??
            throw new InvalidOperationException("A tracker strategy requires tracker settings."));
}

internal sealed class OnnxBotStrategyFactory(IHostEnvironment environment, string? legacyModelPath = null)
    : IBotStrategyFactory
{
    public BotStrategyDescriptor Descriptor { get; } = new(BotStrategyDescriptor.OnnxId, BotSettingsKind.Onnx);

    public ILocalOpponentController Create(BotDefinition entry)
    {
        var settings = entry.Onnx ??
            throw new InvalidOperationException("An ONNX strategy requires ONNX settings.");
        // Preserve the historical diagnostic override only for the unchanged trained profile.
        var path = legacyModelPath is not null &&
                   string.Equals(settings.ModelPath, "Models/hard-v1.onnx", StringComparison.Ordinal) &&
                   string.Equals(settings.ExpectedSha256, HardLocalOpponentController.ExpectedModelSha256,
                       StringComparison.OrdinalIgnoreCase)
            ? legacyModelPath
            : settings.ModelPath;
        path = Path.GetFullPath(path, environment.ContentRootPath);
        return new OnnxLocalOpponentController(settings with { ModelPath = path });
    }
}
