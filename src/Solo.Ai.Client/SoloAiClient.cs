namespace Solo.Ai.Client;

public sealed class SoloAiClient(IHttpClientFactory clients, SoloAiClientOptions options)
{
    public const string ClientName = "solo-ai";
    private readonly SoloAiHttpTransport _transport = new(clients, options);

    public Task<SoloAiChat?> GetCurrentChatAsync(CancellationToken cancellationToken = default) =>
        _transport.ExchangeAsync<SoloAiChat>(HttpMethod.Get, "current", null, [200, 204],
            (chat, _) => SoloAiValidation.ValidateChat(chat), cancellationToken);

    public async Task<SoloAiChat> CreateChatAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        (await _transport.ExchangeAsync<SoloAiChat>(HttpMethod.Post, "", new CreateChatRequest(SoloAiProtocol.Version, chatId),
            [200, 201], (chat, status) => SoloAiValidation.ValidateChat(chat, status == 201 ? chatId : null), cancellationToken))!;

    public async Task<SoloAiChat> GetChatAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        (await _transport.ExchangeAsync<SoloAiChat>(HttpMethod.Get, GetChatPath(chatId), null, [200],
            (chat, _) => SoloAiValidation.ValidateChat(chat, chatId), cancellationToken))!;

    public async Task<SoloAiChat> RenameChatAsync(Guid chatId, string title, CancellationToken cancellationToken = default) =>
        (await _transport.ExchangeAsync<SoloAiChat>(HttpMethod.Patch, GetChatPath(chatId),
            new RenameChatRequest(SoloAiProtocol.Version, title), [200],
            (chat, _) => SoloAiValidation.ValidateChat(chat, chatId), cancellationToken))!;

    public async Task DeleteChatAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        await _transport.ExchangeAsync<SoloAiChat>(HttpMethod.Delete, GetChatPath(chatId), null, [204], null, cancellationToken);

    public async Task<SoloAiMessagePage> GetMessagesAsync(Guid chatId, string? cursor = null,
        int limit = SoloAiProtocol.DefaultPageSize, CancellationToken cancellationToken = default)
    {
        var path = GetChatPath(chatId) + "/messages";
        if (limit is <= 0 or > SoloAiProtocol.MaximumPageSize) throw new SoloAiExchangeException("validation_failed");
        if (cursor is not null) SoloAiMessageCursor.Read(cursor, chatId);
        path += "?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (cursor is not null) path += "&cursor=" + Uri.EscapeDataString(cursor);
        return (await _transport.ExchangeAsync<SoloAiMessagePage>(HttpMethod.Get, path, null, [200],
            (page, _) => SoloAiValidation.ValidatePage(page, chatId, cursor, limit), cancellationToken))!;
    }

    public async Task<SoloAiRun> SendMessageAsync(Guid chatId, Guid messageId, string text, CancellationToken cancellationToken = default) =>
        (await _transport.ExchangeAsync<SoloAiRun>(HttpMethod.Post, GetChatPath(chatId) + "/messages",
            new SendMessageRequest(SoloAiProtocol.Version, messageId, text), [202],
            (run, _) =>
            {
                SoloAiValidation.ValidateRun(run, chatId, messageId: messageId);
                if (run.RetryId is not null) throw new SoloAiExchangeException("invalid_response");
            }, cancellationToken))!;

    public async Task<SoloAiRun> GetRunAsync(Guid chatId, Guid runId, CancellationToken cancellationToken = default) =>
        (await _transport.ExchangeAsync<SoloAiRun>(HttpMethod.Get, GetRunPath(chatId, runId), null, [200],
            (run, _) => SoloAiValidation.ValidateRun(run, chatId, runId), cancellationToken))!;

    public async Task<SoloAiRun> CancelRunAsync(Guid chatId, Guid runId, CancellationToken cancellationToken = default) =>
        (await _transport.ExchangeAsync<SoloAiRun>(HttpMethod.Post, GetRunPath(chatId, runId) + "/cancel", null, [200],
            (run, _) => SoloAiValidation.ValidateRun(run, chatId, runId), cancellationToken))!;

    public async Task<SoloAiRun> RetryRunAsync(Guid chatId, Guid messageId, Guid retryId, CancellationToken cancellationToken = default)
    {
        SoloAiValidation.ValidateId(messageId);
        return (await _transport.ExchangeAsync<SoloAiRun>(HttpMethod.Post, GetChatPath(chatId) + $"/messages/{messageId:D}/retry",
            new RetryRunRequest(SoloAiProtocol.Version, retryId), [202],
            (run, _) => SoloAiValidation.ValidateRun(run, chatId, messageId: messageId, retryId: retryId), cancellationToken))!;
    }

    private static string GetChatPath(Guid chatId)
    {
        SoloAiValidation.ValidateId(chatId);
        return chatId.ToString("D");
    }

    private static string GetRunPath(Guid chatId, Guid runId)
    {
        SoloAiValidation.ValidateId(runId);
        return GetChatPath(chatId) + $"/runs/{runId:D}";
    }
}
