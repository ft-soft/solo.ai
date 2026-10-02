using Microsoft.Data.Sqlite;
using Solo.Ai.Client;
using static Solo.Ai.Storage.SqliteChatQueries;

namespace Solo.Ai.Storage;

/// <summary>Durable send/retry acceptance. The caller must supply the authenticated subject.</summary>
public sealed class SqliteRunStore(SqliteChatDatabase database)
{
    public void EnsureUserMessage(string owner, Guid chatId, Guid messageId)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateId(messageId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        ReadChat(connection, transaction, owner, chatId);
        using var command = CreateCommand(connection, transaction, owner,
            "SELECT 1 FROM Message WHERE OwnerId=$owner AND ChatId=$chat AND Id=$message AND Role='user';",
            ("$chat", chatId), ("$message", messageId));
        if (command.ExecuteScalar() is null) throw new SoloAiExchangeException("not_found");
    }

    public SoloAiRun GetRun(string owner, Guid chatId, Guid runId)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateId(runId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadRun(connection, transaction, owner, chatId, runId);
    }

    public SoloAiRun SendMessage(string owner, Guid chatId, SendMessageRequest request,
        int maximumPending = int.MaxValue, bool accepting = true)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateRequest(request);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var chat = ReadChat(connection, transaction, owner, chatId);
        using var existing = CreateCommand(connection, transaction, owner,
            "SELECT ChatId, Role, Text FROM Message WHERE OwnerId=$owner AND Id=$message;", ("$message", request.MessageId));
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetGuid(0) != chatId || reader.GetString(1) != "user" || reader.GetString(2) != request.Text)
                    throw new SoloAiExchangeException("idempotency_conflict");
                reader.Close();
                using var original = CreateCommand(connection, transaction, owner,
                    $"SELECT {RunColumns} FROM GenerationRun WHERE OwnerId=$owner AND ChatId=$chat AND UserMessageId=$message AND RetryId IS NULL;",
                    ("$chat", chatId), ("$message", request.MessageId));
                using var originalReader = original.ExecuteReader();
                if (!originalReader.Read()) throw new InvalidOperationException("Missing original run.");
                return ReadRun(originalReader);
            }
        }
        RejectActiveRun(chat);
        CheckCapacity(connection, transaction, owner, maximumPending, accepting);
        var now = GetUtcTicks(chat.LastMessageAt ?? chat.CreatedAt);
        using var insert = CreateCommand(connection, transaction, owner, """
            INSERT INTO Message (Id, ChatId, OwnerId, Sequence, Role, Text, CreatedAt)
            SELECT $message, Id, OwnerId,
                (SELECT COALESCE(MAX(Sequence), 0) + 1 FROM Message WHERE OwnerId=$owner AND ChatId=$chat),
                'user', $text, $now FROM Chat WHERE OwnerId=$owner AND Id=$chat;
            UPDATE Chat SET LastMessageAt=$now WHERE OwnerId=$owner AND Id=$chat;
            """, ("$chat", chatId), ("$message", request.MessageId), ("$text", request.Text), ("$now", now));
        insert.ExecuteNonQuery();
        var run = InsertRun(connection, transaction, owner, chatId, request.MessageId, null, now);
        transaction.Commit();
        return run;
    }

    public SoloAiRun RetryRun(string owner, Guid chatId, Guid messageId, RetryRunRequest request,
        int maximumPending = int.MaxValue, bool accepting = true)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateId(messageId);
        SoloAiValidation.ValidateRequest(request);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var chat = ReadChat(connection, transaction, owner, chatId);
        using var duplicate = CreateCommand(connection, transaction, owner,
            $"SELECT {RunColumns} FROM GenerationRun WHERE OwnerId=$owner AND RetryId=$retry;", ("$retry", request.RetryId));
        using (var reader = duplicate.ExecuteReader())
        {
            if (reader.Read())
            {
                var run = ReadRun(reader);
                if (run.ChatId != chatId || run.UserMessageId != messageId) throw new SoloAiExchangeException("idempotency_conflict");
                return run;
            }
        }
        using var message = CreateCommand(connection, transaction, owner,
            "SELECT 1 FROM Message WHERE OwnerId=$owner AND ChatId=$chat AND Id=$message AND Role='user';",
            ("$chat", chatId), ("$message", messageId));
        if (message.ExecuteScalar() is null) throw new SoloAiExchangeException("not_found");
        RejectActiveRun(chat);
        var latest = chat.LatestRun;
        if (latest is null || latest.UserMessageId != messageId || latest.OutcomeCode == "limit_exceeded" ||
            latest.State is not ("failed" or "timed_out" or "incomplete" or "cancelled" or "interrupted"))
            throw new SoloAiExchangeException("retry_not_allowed");
        CheckCapacity(connection, transaction, owner, maximumPending, accepting);
        var result = InsertRun(connection, transaction, owner, chatId, messageId, request.RetryId,
            GetUtcTicks(latest.FinishedAt!.Value));
        transaction.Commit();
        return result;
    }

    private SoloAiRun InsertRun(SqliteConnection connection, SqliteTransaction transaction, string owner,
        Guid chatId, Guid messageId, Guid? retryId, long now)
    {
        var id = Guid.NewGuid();
        using var command = CreateCommand(connection, transaction, owner, """
            INSERT INTO GenerationRun (Id, OwnerId, ChatId, UserMessageId, RetryId, State, CreatedAt, DeadlineAt)
            SELECT $run, OwnerId, Id, $message, $retry, 'pending', $now, $deadline
            FROM Chat WHERE OwnerId=$owner AND Id=$chat;
            """, ("$run", id), ("$chat", chatId), ("$message", messageId), ("$retry", retryId),
            ("$now", now), ("$deadline", checked(now + database.RunTimeout.Ticks)));
        command.ExecuteNonQuery();
        return ReadRun(connection, transaction, owner, chatId, id);
    }

    private static void RejectActiveRun(SoloAiChat chat)
    {
        if (chat.LatestRun?.State is "pending" or "running" or "cancellation_requested")
            throw new SoloAiExchangeException("chat_busy");
    }

    private static void CheckCapacity(SqliteConnection connection, SqliteTransaction transaction, string owner,
        int maximumPending, bool accepting)
    {
        if (!accepting) throw new SoloAiExchangeException("unavailable");
        using var command = CreateCommand(connection, transaction, owner,
            "SELECT COUNT(*) FROM GenerationRun WHERE State='pending';");
        if (Convert.ToInt64(command.ExecuteScalar()) >= maximumPending)
            throw new SoloAiExchangeException("capacity_exceeded");
    }
}
