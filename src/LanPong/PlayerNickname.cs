namespace LanPong;

/// <summary>Shared validation for names entered locally and received from the network.</summary>
internal static class PlayerNickname
{
    internal const int MaxLength = 24;

    internal static bool IsValid(string? nickname) =>
        nickname is { Length: >= 1 and <= MaxLength } &&
        nickname == nickname.Trim() &&
        !nickname.Any(char.IsControl);

    internal static string Normalize(string? requested)
    {
        var nickname = requested?.Trim();
        if (!IsValid(nickname))
            throw new ArgumentException($"Ник должен содержать от 1 до {MaxLength} символов без управляющих знаков.");
        return nickname!;
    }
}
