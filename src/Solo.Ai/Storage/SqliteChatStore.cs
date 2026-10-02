using Solo.Ai.Client;
using static Solo.Ai.Storage.SqliteChatQueries;

namespace Solo.Ai.Storage;

public sealed class SqliteChatStore(SqliteChatDatabase database)
{
    public (SoloAiChat Chat, bool Created) CreateChat(string owner, Guid chatId)
    {
        SoloAiValidation.ValidateId(chatId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var rejected = CreateCommand(connection, transaction, owner, """
            SELECT 1 FROM DeletedChat WHERE ChatId=$chat
            UNION ALL SELECT 1 FROM Chat WHERE Id=$chat AND OwnerId!=$owner LIMIT 1;
            """, ("$chat", chatId));
        if (rejected.ExecuteScalar() is not null) throw new SoloAiExchangeException("not_found");
        using var current = CreateCommand(connection, transaction, owner, "SELECT Id FROM Chat WHERE OwnerId=$owner;");
        var existing = current.ExecuteScalar() as string;
        if (existing is null)
        {
            using var insert = CreateCommand(connection, transaction, owner, """
                INSERT INTO Chat (Id, OwnerId, Title, CreatedAt) VALUES ($chat, $owner, 'Новый чат', $now);
                """, ("$chat", chatId), ("$now", DateTimeOffset.UtcNow.Ticks));
            insert.ExecuteNonQuery();
        }
        var chat = ReadChat(connection, transaction, owner, existing is null ? chatId : Guid.Parse(existing));
        transaction.Commit();
        return (chat, existing is null);
    }

    public SoloAiChat? GetCurrentChat(string owner)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = CreateCommand(connection, transaction, owner, "SELECT Id FROM Chat WHERE OwnerId=$owner;");
        var id = command.ExecuteScalar() as string;
        return id is null ? null : ReadChat(connection, transaction, owner, Guid.Parse(id));
    }

    public SoloAiChat GetChat(string owner, Guid chatId)
    {
        SoloAiValidation.ValidateId(chatId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadChat(connection, transaction, owner, chatId);
    }

    public SoloAiChat RenameChat(string owner, Guid chatId, string title)
    {
        SoloAiValidation.ValidateId(chatId);
        SoloAiValidation.ValidateText(title, SoloAiProtocol.MaximumTitleBytes);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = CreateCommand(connection, transaction, owner,
            "UPDATE Chat SET Title=$title WHERE Id=$chat AND OwnerId=$owner;", ("$title", title), ("$chat", chatId));
        if (command.ExecuteNonQuery() == 0) throw new SoloAiExchangeException("not_found");
        var chat = ReadChat(connection, transaction, owner, chatId);
        transaction.Commit();
        return chat;
    }

    public void DeleteChat(string owner, Guid chatId)
    {
        SoloAiValidation.ValidateId(chatId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        // The BEFORE DELETE trigger records the tombstone before cascading removal, in this same commit.
        using var command = CreateCommand(connection, transaction, owner,
            "DELETE FROM Chat WHERE Id=$chat AND OwnerId=$owner;", ("$chat", chatId));
        if (command.ExecuteNonQuery() == 0) throw new SoloAiExchangeException("not_found");
        transaction.Commit();
    }

    public SoloAiMessagePage GetMessages(string owner, Guid chatId, string? cursor = null, int limit = SoloAiProtocol.DefaultPageSize)
    {
        SoloAiValidation.ValidateId(chatId);
        if (limit is < 1 or > SoloAiProtocol.MaximumPageSize) throw new SoloAiExchangeException("validation_failed");
        var before = cursor is null ? (long?)null : SoloAiMessageCursor.Read(cursor, chatId);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        ReadChat(connection, transaction, owner, chatId);
        using var command = CreateCommand(connection, transaction, owner, $"""
            SELECT m.Id, m.Sequence, m.Role, m.Text, m.CreatedAt, r.{RunColumns.Replace(", ", ", r.")}
            FROM Message m JOIN GenerationRun r ON r.Number = (
                SELECT Number FROM GenerationRun
                WHERE OwnerId=$owner AND ChatId=$chat AND
                    ((m.Role='user' AND UserMessageId=m.Id) OR (m.Role='assistant' AND AssistantMessageId=m.Id))
                ORDER BY Number DESC LIMIT 1)
            WHERE m.OwnerId=$owner AND m.ChatId=$chat AND ($before IS NULL OR m.Sequence < $before)
            ORDER BY m.Sequence DESC LIMIT $limit;
            """, ("$chat", chatId), ("$before", before), ("$limit", limit + 1));
        using var reader = command.ExecuteReader();
        var messages = new List<SoloAiMessage>();
        while (reader.Read())
            messages.Add(new(reader.GetGuid(0), chatId, reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                ReadTime(reader, 4), ReadRun(reader, 5)));
        var hasEarlier = messages.Count > limit;
        if (hasEarlier) messages.RemoveAt(messages.Count - 1);
        messages.Reverse();
        return new(chatId, messages, hasEarlier ? SoloAiMessageCursor.Create(chatId, messages[0].Sequence) : null);
    }
}
