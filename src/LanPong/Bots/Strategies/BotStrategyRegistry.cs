using System.Collections.Frozen;
using System.Collections.Immutable;
using LanPong.Bots.Configuration;

namespace LanPong.Bots.Strategies;

internal enum BotSettingsKind
{
    Tracker,
    Onnx
}

/// <summary>Known strategy contract; controller creation is added by the bot runtime.</summary>
internal sealed record BotStrategyDescriptor(string Id, BotSettingsKind SettingsKind)
{
    public const string TrackerId = "tracker";
    public const string OnnxId = "onnx";
}

internal sealed class BotStrategyRegistry
{
    private readonly FrozenDictionary<string, BotStrategyDescriptor> _byId;

    public BotStrategyRegistry(IEnumerable<BotStrategyDescriptor> descriptors)
    {
        var byId = new Dictionary<string, BotStrategyDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (!BotsOptionsValidator.IsValidId(descriptor.Id) || !Enum.IsDefined(descriptor.SettingsKind))
                throw new InvalidOperationException($"Invalid registered bot strategy '{descriptor.Id}'.");
            if (!byId.TryAdd(descriptor.Id, descriptor))
                throw new InvalidOperationException($"Duplicate registered bot strategy '{descriptor.Id}'.");
        }
        _byId = byId.ToFrozenDictionary(StringComparer.Ordinal);
        Descriptors = byId.Values.OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    public ImmutableArray<BotStrategyDescriptor> Descriptors { get; }
    public bool TryGet(string id, out BotStrategyDescriptor? descriptor) => _byId.TryGetValue(id, out descriptor);
}
