using System.Globalization;
using Microsoft.AspNetCore.Http;
using Solo.Ai.Client;
using Solo.Ai.Storage;

namespace Solo.Ai.Api;

internal static class SoloAiChatEndpoints
{
    public static async Task<IResult> CreateAsync(HttpContext context, SqliteChatStore chats, SoloAiApiOptions options)
    {
        var request = await SoloAiHttp.ReadRequestAsync<CreateChatRequest>(context, options);
        var result = chats.CreateChat(ButlerIdentity.GetOwner(context.User), request.ChatId);
        return SoloAiHttp.Reply(result.Chat, options, result.Created ? 201 : 200);
    }

    public static IResult GetCurrent(HttpContext context, SqliteChatStore chats, SoloAiApiOptions options)
    {
        var chat = chats.GetCurrentChat(ButlerIdentity.GetOwner(context.User));
        return chat is null ? Results.NoContent() : SoloAiHttp.Reply(chat, options);
    }

    public static IResult GetChat(HttpContext context, string chatId, SqliteChatStore chats, SoloAiApiOptions options) =>
        SoloAiHttp.Reply(chats.GetChat(ButlerIdentity.GetOwner(context.User), SoloAiHttp.ParseId(chatId)), options);

    public static async Task<IResult> RenameAsync(HttpContext context, string chatId, SqliteChatStore chats, SoloAiApiOptions options)
    {
        var request = await SoloAiHttp.ReadRequestAsync<RenameChatRequest>(context, options);
        return SoloAiHttp.Reply(chats.RenameChat(ButlerIdentity.GetOwner(context.User), SoloAiHttp.ParseId(chatId), request.Title), options);
    }

    public static IResult Delete(HttpContext context, string chatId, SqliteChatStore chats, SoloAgentRunRuntime runtime)
    {
        var id = SoloAiHttp.ParseId(chatId);
        lock (runtime.Gate)
        {
            chats.DeleteChat(ButlerIdentity.GetOwner(context.User), id);
            runtime.CancelChat(id);
        }
        return Results.NoContent();
    }

    public static IResult GetMessages(HttpContext context, string chatId, SqliteChatStore chats, SoloAiApiOptions options)
    {
        var id = SoloAiHttp.ParseId(chatId);
        var query = context.Request.Query;
        if (query.Keys.Any(key => key is not ("cursor" or "limit")) || query.Any(pair => pair.Value.Count != 1))
            throw new SoloAiExchangeException("validation_failed");
        var limit = SoloAiProtocol.DefaultPageSize;
        if (query.TryGetValue("limit", out var value) && !int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit))
            throw new SoloAiExchangeException("validation_failed");
        var page = chats.GetMessages(ButlerIdentity.GetOwner(context.User), id, query["cursor"].FirstOrDefault(), limit);
        while (true)
        {
            try { return SoloAiHttp.Reply(page, options); }
            catch (SoloAiExchangeException error) when (error.Code == "limit_exceeded" && page.Messages.Count > 1)
            {
                // At most 200 rows; keep the newest half and an exclusive cursor without another DB snapshot.
                var messages = page.Messages.Skip(page.Messages.Count / 2).ToArray();
                page = new(id, messages, SoloAiMessageCursor.Create(id, messages[0].Sequence));
            }
        }
    }

    public static IResult GetRun(HttpContext context, string chatId, string runId, SqliteRunStore runs, SoloAiApiOptions options) =>
        SoloAiHttp.Reply(runs.GetRun(ButlerIdentity.GetOwner(context.User), SoloAiHttp.ParseId(chatId), SoloAiHttp.ParseId(runId)), options);

    public static IResult Cancel(HttpContext context, string chatId, string runId, SqliteRunTransitions runs,
        SoloAiApiOptions options, SoloAgentRunRuntime runtime)
    {
        lock (runtime.Gate)
        {
            var run = runs.RequestCancellation(ButlerIdentity.GetOwner(context.User), SoloAiHttp.ParseId(chatId), SoloAiHttp.ParseId(runId));
            if (run.State == "cancellation_requested") runtime.CancelRun(run.RunId);
            return SoloAiHttp.Reply(run, options);
        }
    }

    public static async Task<IResult> SendAsync(HttpContext context, string chatId, SqliteRunStore runs,
        SoloAiApiOptions options, SoloAiGenerationOptions generation, SoloAgentRunRuntime runtime)
    {
        var request = await SoloAiHttp.ReadRequestAsync<SendMessageRequest>(context, options);
        lock (runtime.Gate)
            return SoloAiHttp.Reply(runs.SendMessage(ButlerIdentity.GetOwner(context.User), SoloAiHttp.ParseId(chatId),
                request, generation.MaximumPendingRuns, runtime.IsReady), options, 202);
    }

    public static async Task<IResult> RetryAsync(HttpContext context, string chatId, string messageId,
        SqliteRunStore runs, SoloAiApiOptions options, SoloAiGenerationOptions generation, SoloAgentRunRuntime runtime)
    {
        var request = await SoloAiHttp.ReadRequestAsync<RetryRunRequest>(context, options);
        lock (runtime.Gate)
            return SoloAiHttp.Reply(runs.RetryRun(ButlerIdentity.GetOwner(context.User), SoloAiHttp.ParseId(chatId),
                SoloAiHttp.ParseId(messageId), request, generation.MaximumPendingRuns, runtime.IsReady), options, 202);
    }
}
