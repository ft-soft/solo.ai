using Microsoft.Data.Sqlite;
using Solo.Ai.Client;

namespace Solo.Ai.Storage;

internal static class SqliteChatQueries
{
    internal const string RunColumns = "Id, ChatId, UserMessageId, RetryId, State, CreatedAt, StartedAt, FinishedAt, DeadlineAt, OutcomeCode, AssistantMessageId";

    internal static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction,
        string owner, string sql, params (string Name, object? Value)[] parameters)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new SoloAiExchangeException("validation_failed");
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$owner", owner);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        return command;
    }

    internal static SoloAiChat ReadChat(SqliteConnection connection, SqliteTransaction transaction, string owner, Guid chatId)
    {
        using var command = CreateCommand(connection, transaction, owner,
            "SELECT Title, CreatedAt, LastMessageAt FROM Chat WHERE OwnerId=$owner AND Id=$chat;", ("$chat", chatId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new SoloAiExchangeException("not_found");
        var title = reader.GetString(0);
        var created = ReadTime(reader, 1);
        var last = reader.IsDBNull(2) ? (DateTimeOffset?)null : ReadTime(reader, 2);
        reader.Close();
        using var runCommand = CreateCommand(connection, transaction, owner,
            $"SELECT {RunColumns} FROM GenerationRun WHERE OwnerId=$owner AND ChatId=$chat ORDER BY Number DESC LIMIT 1;",
            ("$chat", chatId));
        using var runReader = runCommand.ExecuteReader();
        return new(chatId, title, created, last, runReader.Read() ? ReadRun(runReader) : null);
    }

    internal static SoloAiRun ReadRun(SqliteDataReader reader, int offset = 0) => new(
        reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetGuid(offset + 2), reader.IsDBNull(offset + 3) ? null : reader.GetGuid(offset + 3),
        reader.GetString(offset + 4), ReadTime(reader, offset + 5), reader.IsDBNull(offset + 6) ? null : ReadTime(reader, offset + 6),
        reader.IsDBNull(offset + 7) ? null : ReadTime(reader, offset + 7), ReadTime(reader, offset + 8),
        reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9), reader.IsDBNull(offset + 10) ? null : reader.GetGuid(offset + 10));

    internal static SoloAiRun ReadRun(SqliteConnection connection, SqliteTransaction transaction, string owner, Guid chatId, Guid runId)
    {
        using var command = CreateCommand(connection, transaction, owner,
            $"SELECT {RunColumns} FROM GenerationRun WHERE OwnerId=$owner AND ChatId=$chat AND Id=$run;",
            ("$chat", chatId), ("$run", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new SoloAiExchangeException("not_found");
        return ReadRun(reader);
    }

    internal static DateTimeOffset ReadTime(SqliteDataReader reader, int ordinal) => new(reader.GetInt64(ordinal), TimeSpan.Zero);
    internal static long GetUtcTicks(DateTimeOffset minimum) => Math.Max(DateTimeOffset.UtcNow.Ticks, minimum.Ticks);
}
