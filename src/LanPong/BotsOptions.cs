namespace LanPong;

/// <summary>Startup-only configuration. Changes take effect after application restart.</summary>
internal sealed class BotsOptions
{
    public const string SectionName = "Bots";

    public string DefaultBotId { get; set; } = "";
    public List<BotEntryOptions> Entries { get; set; } = [];
}

internal sealed class BotEntryOptions
{
    // IDs use lowercase ASCII words/digits separated by single hyphens, starting with a letter.
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Style { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public string Category { get; set; } = "";
    public int Order { get; set; }
    public string? Glyph { get; set; }
    public bool Enabled { get; set; } = true;
    public string StrategyId { get; set; } = "";
    public TrackerBotOptions? Tracker { get; set; }
    public OnnxBotOptions? Onnx { get; set; }
    public string? FallbackBotId { get; set; }
}

internal sealed class TrackerBotOptions
{
    // Defaults preserve the calibrated tracker policy; bounds are checked at startup.
    public int ObservationIntervalTicks { get; set; } = TrackerBotPolicy.ObservationIntervalTicks;
    public double ObservationActivationX { get; set; } = TrackerBotPolicy.ObservationActivationX;
    public double LookAheadSeconds { get; set; } = TrackerBotPolicy.LookAheadSeconds;
    public double TargetDeadZone { get; set; } = TrackerBotPolicy.TargetDeadZone;
}

internal sealed class OnnxBotOptions
{
    // Relative paths will be resolved by the runtime against the application content root.
    public string ModelPath { get; set; } = BotModelV1.RelativeModelPath;
    public string ExpectedSha256 { get; set; } = BotModelV1.ExpectedSha256;
    public int InferenceCadenceTicks { get; set; } = RightBotObservationV1.InferenceCadenceTicks;
}
