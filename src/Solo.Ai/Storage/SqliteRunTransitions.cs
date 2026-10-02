using Solo.Ai.Client;
using static Solo.Ai.Storage.SqliteChatQueries;

namespace Solo.Ai.Storage;

/// <summary>Atomic run transitions; model calls and local cancellation signals belong to the worker.</summary>
public sealed class SqliteRunTransitions(SqliteChatDatabase database)
{
    public bool TryStartRun(string owner, Guid chatId, Guid runId)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateId(runId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = ReadRun(connection, transaction, owner, chatId, runId);
        var now = GetUtcTicks(run.CreatedAt);
        using var command = CreateCommand(connection, transaction, owner, """
            UPDATE GenerationRun SET State='running', StartedAt=$now
            WHERE OwnerId=$owner AND ChatId=$chat AND Id=$run AND State='pending' AND DeadlineAt > $now;
            """, ("$chat", chatId), ("$run", runId), ("$now", now));
        var started = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return started;
    }

    public SoloAiRun RequestCancellation(string owner, Guid chatId, Guid runId)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateId(runId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = CreateCommand(connection, transaction, owner, """
            UPDATE GenerationRun SET State='cancellation_requested'
            WHERE OwnerId=$owner AND ChatId=$chat AND Id=$run AND State IN ('pending', 'running');
            """, ("$chat", chatId), ("$run", runId));
        command.ExecuteNonQuery();
        var result = ReadRun(connection, transaction, owner, chatId, runId);
        transaction.Commit();
        return result;
    }

    // The worker validates the model response first. This operation persists only a full successful text.
    public SoloAiRun FinishRun(string owner, Guid chatId, Guid runId, string state, string? outcomeCode = null, string? assistantText = null)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateId(runId);
        if (state is not ("completed" or "failed" or "timed_out" or "incomplete" or "cancelled" or "interrupted") ||
            (state == "completed") != (assistantText is not null))
            throw new SoloAiExchangeException("validation_failed");
        if (assistantText is not null) SoloAiValidation.ValidateText(assistantText, SoloAiProtocol.MaximumTextBytes);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = ReadRun(connection, transaction, owner, chatId, runId);
        if (run.FinishedAt is not null || run.State == "cancellation_requested" && state != "cancelled") return run;
        if (state == "cancelled" && run.State != "cancellation_requested" ||
            run.State == "pending" && state is not ("failed" or "timed_out"))
            throw new SoloAiExchangeException("validation_failed");
        var chat = ReadChat(connection, transaction, owner, chatId);
        var now = GetUtcTicks(run.StartedAt ?? run.CreatedAt);
        now = Math.Max(now, (chat.LastMessageAt ?? chat.CreatedAt).Ticks);
        if (state == "completed" && now >= run.DeadlineAt.Ticks)
        {
            state = "timed_out";
            outcomeCode = "timed_out";
            assistantText = null;
        }
        var result = run with
        {
            State = state, OutcomeCode = outcomeCode, FinishedAt = new DateTimeOffset(now, TimeSpan.Zero),
            AssistantMessageId = state == "completed" ? Guid.NewGuid() : null,
        };
        SoloAiValidation.ValidateRun(result, chatId);
        if (result.AssistantMessageId is { } messageId)
        {
            using var insert = CreateCommand(connection, transaction, owner, """
                INSERT INTO Message (Id, ChatId, OwnerId, Sequence, Role, Text, CreatedAt)
                SELECT $message, Id, OwnerId,
                    (SELECT COALESCE(MAX(Sequence), 0) + 1 FROM Message WHERE OwnerId=$owner AND ChatId=$chat),
                    'assistant', $text, $now FROM Chat WHERE OwnerId=$owner AND Id=$chat;
                UPDATE Chat SET LastMessageAt=$now WHERE OwnerId=$owner AND Id=$chat;
                """, ("$message", messageId), ("$chat", chatId), ("$text", assistantText), ("$now", now));
            insert.ExecuteNonQuery();
        }
        using var update = CreateCommand(connection, transaction, owner, """
            UPDATE GenerationRun SET State=$state, OutcomeCode=$code, FinishedAt=$now, AssistantMessageId=$assistant
            WHERE OwnerId=$owner AND ChatId=$chat AND Id=$run AND State=$previous;
            """, ("$state", result.State), ("$code", result.OutcomeCode), ("$now", now),
            ("$assistant", result.AssistantMessageId), ("$chat", chatId), ("$run", runId), ("$previous", run.State));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Run changed inside a write transaction.");
        transaction.Commit();
        return result;
    }
}
