using System.Globalization;

namespace Solo.Ai.Client;

public static class SoloAiMessageCursor
{
    public static string Create(Guid chatId, long beforeSequence)
    {
        if (chatId == Guid.Empty || beforeSequence <= 0)
            throw new SoloAiExchangeException("validation_failed");
        return $"{chatId:N}:{beforeSequence.ToString(CultureInfo.InvariantCulture)}";
    }

    public static long Read(string cursor, Guid chatId)
    {
        if (chatId == Guid.Empty || cursor is null || cursor.Length is < 34 or > 52 || cursor[32] != ':' ||
            !Guid.TryParseExact(cursor.AsSpan(0, 32), "N", out var ownerChat) || ownerChat != chatId ||
            !long.TryParse(cursor.AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            sequence <= 0 || cursor != Create(chatId, sequence))
            throw new SoloAiExchangeException("validation_failed");
        return sequence;
    }
}
