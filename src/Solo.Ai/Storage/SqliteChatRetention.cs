namespace Solo.Ai.Storage;

public sealed class SqliteChatRetention(SqliteChatDatabase database, SqliteChatOptions options)
{
    public int DeleteExpiredChats(DateTimeOffset now)
    {
        if (options.ChatRetentionDays is not { } days) return 0;
        if (days <= 0) throw new InvalidOperationException("Invalid chat retention.");
        // No timestamp can be old enough when the duration predates the .NET UTC epoch.
        if (days > now.UtcTicks / TimeSpan.TicksPerDay) return 0;
        var cutoff = now.UtcTicks - days * TimeSpan.TicksPerDay;
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Re-evaluate age and active runs under the same write lock as deletion and its tombstone trigger.
        command.CommandText = """
            DELETE FROM Chat WHERE Id IN (
                SELECT c.Id FROM Chat c
                WHERE COALESCE(c.LastMessageAt, c.CreatedAt) <= $cutoff
                    AND NOT EXISTS (SELECT 1 FROM GenerationRun r WHERE r.ChatId=c.Id
                        AND r.State IN ('pending', 'running', 'cancellation_requested'))
                LIMIT 100);
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        var deleted = command.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }
}
