using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace Solo.Ai.Client;

/// <summary>Wire invariants shared by the SDK and future HTTP producer; ownership is enforced by the producer.</summary>
public static class SoloAiValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static void ValidateRequest(object request)
    {
        var version = request switch
        {
            CreateChatRequest value => value.ContractVersion,
            RenameChatRequest value => value.ContractVersion,
            SendMessageRequest value => value.ContractVersion,
            RetryRunRequest value => value.ContractVersion,
            _ => throw new SoloAiExchangeException("validation_failed"),
        };
        if (version != SoloAiProtocol.Version)
            throw new SoloAiExchangeException("unsupported_contract");
        switch (request)
        {
            case CreateChatRequest value: ValidateId(value.ChatId); break;
            case RenameChatRequest value: ValidateText(value.Title, SoloAiProtocol.MaximumTitleBytes); break;
            case SendMessageRequest value:
                ValidateId(value.MessageId);
                ValidateText(value.Text, SoloAiProtocol.MaximumTextBytes);
                break;
            case RetryRunRequest value: ValidateId(value.RetryId); break;
        }
    }

    public static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new SoloAiExchangeException("validation_failed");
    }

    public static void ValidateText(string text, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new SoloAiExchangeException("validation_failed");
        try
        {
            if (StrictUtf8.GetByteCount(text) > maximumBytes) throw new SoloAiExchangeException("limit_exceeded");
        }
        catch (EncoderFallbackException) { throw new SoloAiExchangeException("validation_failed"); }
    }

    public static void ValidateChat(SoloAiChat chat, Guid? expectedChatId = null)
    {
        Require(chat.ChatId != Guid.Empty && (expectedChatId is null || chat.ChatId == expectedChatId) &&
            IsTextValid(chat.Title, SoloAiProtocol.MaximumTitleBytes) && IsUtc(chat.CreatedAt) &&
            (chat.LastMessageAt is null || IsUtc(chat.LastMessageAt.Value) && chat.LastMessageAt >= chat.CreatedAt));
        if (chat.LatestRun is not null)
        {
            ValidateRun(chat.LatestRun, chat.ChatId);
            Require(chat.LastMessageAt is not null && chat.LatestRun.CreatedAt >= chat.CreatedAt);
        }
    }

    public static void ValidateRun(SoloAiRun run, Guid chatId, Guid? runId = null, Guid? messageId = null, Guid? retryId = null)
    {
        Require(run.RunId != Guid.Empty && run.ChatId == chatId && chatId != Guid.Empty &&
            run.UserMessageId != Guid.Empty && run.RetryId != Guid.Empty && run.AssistantMessageId != Guid.Empty &&
            run.AssistantMessageId != run.UserMessageId &&
            (runId is null || run.RunId == runId) && (messageId is null || run.UserMessageId == messageId) &&
            (retryId is null || run.RetryId == retryId) && IsUtc(run.CreatedAt) && IsUtc(run.DeadlineAt) &&
            run.DeadlineAt > run.CreatedAt &&
            (run.StartedAt is null || IsUtc(run.StartedAt.Value) && run.StartedAt >= run.CreatedAt) &&
            (run.FinishedAt is null || IsUtc(run.FinishedAt.Value) && run.FinishedAt >= (run.StartedAt ?? run.CreatedAt)));
        var terminal = run.State is "completed" or "failed" or "timed_out" or "incomplete" or "cancelled" or "interrupted";
        Require(terminal || run.State is "pending" or "running" or "cancellation_requested");
        Require(terminal == (run.FinishedAt is not null));
        Require(run.State != "pending" || run.StartedAt is null);
        Require(run.State is not ("running" or "completed" or "incomplete" or "interrupted") || run.StartedAt is not null);
        Require((run.State == "completed") == (run.AssistantMessageId is not null));
        Require(run.State switch
        {
            "pending" or "running" or "cancellation_requested" or "completed" => run.OutcomeCode is null,
            "failed" => run.OutcomeCode is "configuration_failure" or "dependency_authentication_failed" or
                "dependency_unavailable" or "provider_error" or "transport_error" or "limit_exceeded" or "malformed_model_response",
            "timed_out" => run.OutcomeCode == "timed_out",
            "incomplete" => run.OutcomeCode == "incomplete_response",
            "cancelled" => run.OutcomeCode == "cancelled",
            "interrupted" => run.OutcomeCode == "interrupted",
            _ => false,
        });
    }

    public static void ValidatePage(SoloAiMessagePage page, Guid chatId, string? cursor, int limit)
    {
        Require(page.ChatId == chatId && chatId != Guid.Empty && page.Messages is not null &&
            page.Messages.Count <= limit && limit is > 0 and <= SoloAiProtocol.MaximumPageSize);
        var before = cursor is null ? (long?)null : SoloAiMessageCursor.Read(cursor, chatId);
        long previous = 0;
        var ids = new HashSet<Guid>();
        foreach (var message in page.Messages!)
        {
            Require(message is not null && message.ChatId == chatId && message.MessageId != Guid.Empty &&
                ids.Add(message.MessageId) && message.Sequence > previous &&
                (before is null || message.Sequence < before) && IsUtc(message.CreatedAt) &&
                IsTextValid(message.Text, SoloAiProtocol.MaximumTextBytes) && message.Run is not null);
            ValidateRun(message!.Run, chatId);
            Require(message.Role switch
            {
                "user" => message.Run.UserMessageId == message.MessageId && message.Run.CreatedAt >= message.CreatedAt,
                "assistant" => message.Run.State == "completed" && message.Run.AssistantMessageId == message.MessageId &&
                    message.CreatedAt >= message.Run.CreatedAt,
                _ => false,
            });
            previous = message.Sequence;
        }
        if (page.NextCursor is not null)
            Require(page.Messages.Count > 0 && page.NextCursor == SoloAiMessageCursor.Create(chatId, page.Messages[0].Sequence));
    }

    public static bool IsHttpErrorValid(int status, string? code) => (status, code) switch
    {
        (400, "validation_failed" or "unsupported_contract") or (401, "authentication_required") or
        (403, "forbidden_actor") or (404, "not_found") or
        (409, "chat_busy" or "idempotency_conflict" or "retry_not_allowed") or
        (413, "limit_exceeded") or (429, "capacity_exceeded") or (503, "unavailable") => true,
        _ => false,
    };

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool IsTextValid(string text, int maximumBytes)
    {
        try { return !string.IsNullOrWhiteSpace(text) && StrictUtf8.GetByteCount(text) <= maximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }
    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new SoloAiExchangeException("invalid_response");
    }
}
