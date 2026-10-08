namespace LanPong.Bots.Runtime;

// Kept separate from the version 7 browser snapshot until the contract migration.
internal sealed record BotSessionIdentity(string RequestedBotId, string RequestedName,
    string EffectiveBotId, string EffectiveName, string? FallbackReason);
