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
        new TrackerBotPolicy(entry.Tracker ??
            throw new InvalidOperationException("A tracker strategy requires tracker settings."));
}

internal sealed class OnnxBotStrategyFactory(IHostEnvironment environment)
    : IBotStrategyFactory
{
    public BotStrategyDescriptor Descriptor { get; } = new(BotStrategyDescriptor.OnnxId, BotSettingsKind.Onnx);

    public ILocalOpponentController Create(BotDefinition entry)
    {
        var settings = entry.Onnx ??
            throw new InvalidOperationException("An ONNX strategy requires ONNX settings.");
        var path = Path.GetFullPath(settings.ModelPath, environment.ContentRootPath);
        return new OnnxLocalOpponentController(settings with { ModelPath = path });
    }
}
