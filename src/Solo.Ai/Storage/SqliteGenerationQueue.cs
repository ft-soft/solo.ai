using System.Text;
using Solo.Ai.Visograph;
using static Solo.Ai.Storage.SqliteChatQueries;

namespace Solo.Ai.Storage;

/// <summary>Single-process scheduler queries; all work and history remain in SQLite.</summary>
public sealed class SqliteGenerationQueue(SqliteChatDatabase database)
{
    public void Recover()
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE GenerationRun SET
                OutcomeCode=CASE State WHEN 'running' THEN 'interrupted' ELSE 'cancelled' END,
                State=CASE State WHEN 'running' THEN 'interrupted' ELSE 'cancelled' END,
                FinishedAt=MAX($now, COALESCE(StartedAt, CreatedAt))
            WHERE State IN ('running', 'cancellation_requested');
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.Ticks);
        command.ExecuteNonQuery();
        transaction.Commit();
        SettleWaitingRuns();
    }

    public void SettleWaitingRuns()
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE GenerationRun SET
                OutcomeCode=CASE State WHEN 'pending' THEN 'timed_out' ELSE 'cancelled' END,
                State=CASE State WHEN 'pending' THEN 'timed_out' ELSE 'cancelled' END,
                FinishedAt=MAX($now, CreatedAt)
            WHERE (State='pending' AND DeadlineAt <= $now)
                OR (State='cancellation_requested' AND StartedAt IS NULL);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.Ticks);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<StoredGenerationRun> GetPending(int limit)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT OwnerId, {RunColumns} FROM GenerationRun WHERE State='pending' ORDER BY Number LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<StoredGenerationRun>();
        while (reader.Read()) runs.Add(new(reader.GetString(0), ReadRun(reader, 1)));
        return runs;
    }

    public IReadOnlyList<ModelMessage> ReadHistory(StoredGenerationRun work, long maximumBytes)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        var run = ReadRun(connection, transaction, work.Owner, work.Run.ChatId, work.Run.RunId);
        if (run.State != "running") throw new OperationCanceledException();
        using var command = CreateCommand(connection, transaction, work.Owner, """
            SELECT Role, Text FROM Message m
            WHERE OwnerId=$owner AND ChatId=$chat
                AND Sequence <= (SELECT Sequence FROM Message WHERE OwnerId=$owner AND ChatId=$chat AND Id=$message)
                AND (Role='user' OR EXISTS (SELECT 1 FROM GenerationRun r
                    WHERE r.OwnerId=m.OwnerId AND r.ChatId=m.ChatId AND r.AssistantMessageId=m.Id AND r.State='completed'))
            ORDER BY Sequence;
            """, ("$chat", run.ChatId), ("$message", run.UserMessageId));
        using var reader = command.ExecuteReader();
        var history = new List<ModelMessage>();
        long bytes = 0;
        while (reader.Read())
        {
            var text = reader.GetString(1);
            bytes += Encoding.UTF8.GetByteCount(text);
            if (bytes > maximumBytes) throw new ModelExchangeException("limit_exceeded", "input");
            history.Add(new(reader.GetString(0), text));
        }
        return history;
    }
}
