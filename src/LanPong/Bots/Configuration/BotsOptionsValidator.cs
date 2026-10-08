using Microsoft.Extensions.Options;
using LanPong.Bots.Strategies;

namespace LanPong.Bots.Configuration;

internal sealed class BotsOptionsValidator(BotStrategyRegistry strategies) : IValidateOptions<BotsOptions>
{
    public ValidateOptionsResult Validate(string? name, BotsOptions options)
    {
        var errors = new List<string>();
        var entries = new Dictionary<string, (BotEntryOptions Entry, int Index)>(StringComparer.Ordinal);
        CheckId(options.DefaultBotId, "Bots:DefaultBotId", errors);
        if (options.Entries is null || options.Entries.Count == 0)
            errors.Add("Bots:Entries must contain at least one bot.");

        for (var index = 0; index < (options.Entries?.Count ?? 0); index++)
        {
            var entry = options.Entries![index];
            var path = $"Bots:Entries:{index}";
            if (entry is null)
            {
                errors.Add($"{path} must contain a bot definition.");
                continue;
            }
            if (CheckId(entry.Id, $"{path}:Id", errors) && !entries.TryAdd(entry.Id, (entry, index)))
                errors.Add($"{path}:Id duplicates bot '{entry.Id}' at Bots:Entries:{entries[entry.Id].Index}:Id.");
            CheckText(entry.Name, $"{path}:Name", 64, errors);
            CheckText(entry.Description, $"{path}:Description", 512, errors);
            CheckText(entry.Style, $"{path}:Style", 128, errors);
            CheckText(entry.Difficulty, $"{path}:Difficulty", 64, errors);
            CheckText(entry.Category, $"{path}:Category", 64, errors);
            if (entry.Glyph is not null) CheckText(entry.Glyph, $"{path}:Glyph", 16, errors);
            if (entry.Order < 0) errors.Add($"{path}:Order must be nonnegative.");
            if (CheckId(entry.StrategyId, $"{path}:StrategyId", errors))
            {
                if (!strategies.TryGet(entry.StrategyId, out var strategy))
                    errors.Add($"{path}:StrategyId '{entry.StrategyId}' is not registered. Known strategies: " +
                        string.Join(", ", strategies.Descriptors.Select(descriptor => descriptor.Id)) + ".");
                else
                    CheckSettings(entry, strategy!.SettingsKind, path, errors);
            }
            // Validate supplied settings even on disabled/unknown-strategy entries.
            if (entry.Tracker is not null) CheckTracker(entry.Tracker, $"{path}:Tracker", errors);
            if (entry.Onnx is not null) CheckOnnx(entry.Onnx, $"{path}:Onnx", errors);
            if (entry.FallbackBotId is not null) CheckId(entry.FallbackBotId, $"{path}:FallbackBotId", errors);
        }

        if (IsValidId(options.DefaultBotId))
        {
            if (!entries.TryGetValue(options.DefaultBotId, out var selected))
                errors.Add($"Bots:DefaultBotId '{options.DefaultBotId}' does not reference a configured bot.");
            else if (!selected.Entry.Enabled)
                errors.Add($"Bots:DefaultBotId '{options.DefaultBotId}' must reference an enabled bot.");
        }
        foreach (var (id, (entry, index)) in entries)
        {
            if (entry.FallbackBotId is not { } fallback || !IsValidId(fallback)) continue;
            if (!entries.TryGetValue(fallback, out var target))
                errors.Add($"Bots:Entries:{index}:FallbackBotId '{fallback}' for bot '{id}' does not reference a configured bot.");
            else if (!target.Entry.Enabled)
                errors.Add($"Bots:Entries:{index}:FallbackBotId '{fallback}' must reference an enabled bot.");
        }
        CheckFallbackCycles(entries, errors);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    internal static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id[0] is < 'a' or > 'z') return false;
        var previousHyphen = false;
        foreach (var character in id)
        {
            if (character == '-')
            {
                if (previousHyphen) return false;
                previousHyphen = true;
            }
            else
            {
                if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')) return false;
                previousHyphen = false;
            }
        }
        return !previousHyphen;
    }

    private static bool CheckId(string? id, string path, List<string> errors)
    {
        if (IsValidId(id)) return true;
        errors.Add($"{path} must be a 1–64 character lowercase ASCII ID starting with a letter, " +
            "using letters/digits separated by single hyphens (for example 'steady-tracker').");
        return false;
    }

    private static void CheckText(string? value, string path, int maximumLength, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            errors.Add($"{path} must contain nonblank text of at most {maximumLength} characters.");
    }

    private static void CheckSettings(BotEntryOptions entry, BotSettingsKind kind, string path, List<string> errors)
    {
        switch (kind)
        {
            case BotSettingsKind.Tracker:
                if (entry.Tracker is null) errors.Add($"{path}:Tracker is required for strategy '{entry.StrategyId}'.");
                if (entry.Onnx is not null) errors.Add($"{path}:Onnx is not valid for strategy '{entry.StrategyId}'.");
                break;
            case BotSettingsKind.Onnx:
                if (entry.Onnx is null) errors.Add($"{path}:Onnx is required for strategy '{entry.StrategyId}'.");
                if (entry.Tracker is not null) errors.Add($"{path}:Tracker is not valid for strategy '{entry.StrategyId}'.");
                break;
        }
    }

    private static void CheckTracker(TrackerBotOptions settings, string path, List<string> errors)
    {
        CheckCadence(settings.ObservationIntervalTicks, $"{path}:ObservationIntervalTicks", errors);
        if (!double.IsFinite(settings.ObservationActivationX) ||
            settings.ObservationActivationX < 0 || settings.ObservationActivationX >= GameConstants.RightContactX)
            errors.Add($"{path}:ObservationActivationX must be finite, at least 0 and less than {GameConstants.RightContactX} (right paddle contact).");
        CheckRange(settings.LookAheadSeconds, 0, 2, $"{path}:LookAheadSeconds", errors);
        CheckRange(settings.TargetDeadZone, 0, 0.5, $"{path}:TargetDeadZone", errors);
    }

    private static void CheckOnnx(OnnxBotOptions settings, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelPath) || settings.ModelPath.Contains('\0'))
            errors.Add($"{path}:ModelPath must be a nonblank path without null characters; relative paths use the application content root.");
        if (settings.ExpectedSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            errors.Add($"{path}:ExpectedSha256 must contain exactly 64 hexadecimal SHA-256 characters.");
        CheckCadence(settings.InferenceCadenceTicks, $"{path}:InferenceCadenceTicks", errors);
    }

    private static void CheckCadence(int value, string path, List<string> errors)
    {
        if (value is < 1 or > 600) errors.Add($"{path} must be between 1 and 600 ticks (up to 10 seconds at 60 Hz).");
    }

    private static void CheckRange(double value, double minimum, double maximum, string path, List<string> errors)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            errors.Add($"{path} must be finite and between {minimum} and {maximum} inclusive.");
    }

    private static void CheckFallbackCycles(Dictionary<string, (BotEntryOptions Entry, int Index)> entries,
        List<string> errors)
    {
        // Each entry has one outgoing edge. Iteration avoids recursion for long configured chains.
        var states = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var id in entries.Keys)
        {
            if (states.ContainsKey(id)) continue;
            var path = new List<string>();
            var current = id;
            while (current is not null && entries.TryGetValue(current, out var item))
            {
                if (states.TryGetValue(current, out var state))
                {
                    if (state == 1)
                        errors.Add($"Bots:Entries:{item.Index}:FallbackBotId forms a cycle: " +
                            string.Join(" -> ", path.Skip(path.IndexOf(current)).Append(current)) + ".");
                    break;
                }
                states[current] = 1;
                path.Add(current);
                current = item.Entry.FallbackBotId;
            }
            foreach (var visited in path) states[visited] = 2;
        }
    }
}
