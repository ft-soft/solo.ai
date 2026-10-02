using System.Text.Json.Nodes;
using Solo.Ai.Client;
using Xunit;
using static Solo.Ai.Tests.SoloAiHttpFixture;

namespace Solo.Ai.Tests;

public sealed class SoloAiClientTests
{
    [Theory]
    [InlineData("current", "GET", "current", null, "chat-v2.json", 200)]
    [InlineData("empty", "GET", "current", null, null, 204)]
    [InlineData("create", "POST", "", "create-v2.json", "chat-v2.json", 201)]
    [InlineData("existing", "POST", "", "create-existing-v2.json", "chat-v2.json", 200)]
    [InlineData("read", "GET", "{chat}", null, "chat-v2.json", 200)]
    [InlineData("rename", "PATCH", "{chat}", "rename-v2.json", "renamed-v2.json", 200)]
    [InlineData("delete", "DELETE", "{chat}", null, null, 204)]
    [InlineData("messages", "GET", "{chat}/messages?limit=50", null, "messages-v2.json", 200)]
    [InlineData("older", "GET", "{chat}/messages?limit=50&cursor=10000000000000000000000000000001%3A10", null, "messages-v2.json", 200)]
    [InlineData("send", "POST", "{chat}/messages", "send-v2.json", "run-v2.json", 202)]
    [InlineData("send", "POST", "{chat}/messages", "send-v2.json", "completed-v2.json", 202)]
    [InlineData("status", "GET", "{chat}/runs/{run}", null, "completed-v2.json", 200)]
    [InlineData("failure", "GET", "{chat}/runs/{run}", null, "dependency-failure-v2.json", 200)]
    [InlineData("cancel", "POST", "{chat}/runs/{run}/cancel", null, "cancel-v2.json", 200)]
    [InlineData("cancel", "POST", "{chat}/runs/{run}/cancel", null, "completed-v2.json", 200)]
    [InlineData("retry", "POST", "{chat}/messages/{message}/retry", "retry-request-v2.json", "retry-v2.json", 202)]
    public async Task ExchangeIndependentContractFixtures(string operation, string method, string suffix,
        string? requestFixture, string? responseFixture, int status)
    {
        using var http = new SoloAiHttpFixture(async (request, token) =>
        {
            Assert.Equal(method, request.Method.Method);
            Assert.Equal("/api/v2/chats" + (suffix.Length == 0 ? "" : "/" + suffix.Replace("{chat}", ChatId.ToString())
                .Replace("{run}", RunId.ToString()).Replace("{message}", MessageId.ToString())), request.RequestUri!.PathAndQuery);
            Assert.Equal("2", Assert.Single(request.Headers.GetValues(SoloAiProtocol.VersionHeader)));
            Assert.NotEqual(Guid.Empty, Guid.Parse(Assert.Single(request.Headers.GetValues(SoloAiProtocol.RequestIdHeader))));
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Solo-User-Id"));
            if (requestFixture is null) Assert.Null(request.Content);
            else
            {
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ReadFixture(requestFixture)),
                    JsonNode.Parse(await request.Content.ReadAsStringAsync(token))));
            }
            return Reply(responseFixture, status);
        });
        var sdk = http.CreateSdk();
        var token = TestContext.Current.CancellationToken;
        switch (operation)
        {
            case "current": Assert.Equal(ChatId, (await sdk.GetCurrentChatAsync(token))!.ChatId); break;
            case "empty": Assert.Null(await sdk.GetCurrentChatAsync(token)); break;
            case "create": Assert.Equal(ChatId, (await sdk.CreateChatAsync(ChatId, token)).ChatId); break;
            case "existing": Assert.Equal(ChatId, (await sdk.CreateChatAsync(Guid.Parse("20000000-0000-0000-0000-000000000001"), token)).ChatId); break;
            case "read": Assert.Equal(ChatId, (await sdk.GetChatAsync(ChatId, token)).ChatId); break;
            case "rename": Assert.Equal("Рабочая переписка", (await sdk.RenameChatAsync(ChatId, "Рабочая переписка", token)).Title); break;
            case "delete": await sdk.DeleteChatAsync(ChatId, token); break;
            case "messages": case "older":
                var page = await sdk.GetMessagesAsync(ChatId, operation == "older" ? SoloAiMessageCursor.Create(ChatId, 10) : null, cancellationToken: token);
                Assert.Equal(new long[] { 5, 6 }, page.Messages.Select(message => message.Sequence));
                Assert.Equal(SoloAiMessageCursor.Create(ChatId, 5), page.NextCursor);
                break;
            case "send": Assert.Equal(MessageId, (await sdk.SendMessageAsync(ChatId, MessageId, "Привет", token)).UserMessageId); break;
            case "status": Assert.Equal("completed", (await sdk.GetRunAsync(ChatId, RunId, token)).State); break;
            case "failure": Assert.Equal("dependency_authentication_failed", (await sdk.GetRunAsync(ChatId, RunId, token)).OutcomeCode); break;
            case "cancel": Assert.Equal(RunId, (await sdk.CancelRunAsync(ChatId, RunId, token)).RunId); break;
            case "retry": Assert.Equal(RetryId, (await sdk.RetryRunAsync(ChatId, MessageId, RetryId, token)).RetryId); break;
        }
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task PreserveValidatedHttpErrorsWithoutConfusingThemWithAcceptedRuns()
    {
        foreach (var fixture in JsonNode.Parse(ReadFixture("errors-v2.json"))!.AsArray())
        {
            var status = fixture!["status"]!.GetValue<int>();
            var body = fixture["body"]!.ToJsonString();
            using var http = new SoloAiHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }));
            var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk().SendMessageAsync(ChatId, MessageId, "Привет", TestContext.Current.CancellationToken));
            Assert.Equal(fixture["body"]!["error"]!.GetValue<string>(), error.Code);
            Assert.False(error.OutcomeUnknown);
            Assert.Equal(status, (int)error.StatusCode!);
            Assert.Equal(1, http.Calls);
        }
    }

    [Fact]
    public async Task KeepIdempotencyKeysAcrossExplicitRepeatedCommands()
    {
        using var http = new SoloAiHttpFixture((_, _) => Task.FromResult(Reply("retry-v2.json", 202)));
        var sdk = http.CreateSdk();
        var first = await sdk.RetryRunAsync(ChatId, MessageId, RetryId, TestContext.Current.CancellationToken);
        var second = await sdk.RetryRunAsync(ChatId, MessageId, RetryId, TestContext.Current.CancellationToken);
        Assert.Equal(first, second);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task RejectBadInputBeforeHttp()
    {
        using var http = new SoloAiHttpFixture((_, _) => throw new InvalidOperationException("Must not send"));
        var sdk = http.CreateSdk();
        Func<Task>[] operations = [
            () => sdk.CreateChatAsync(Guid.Empty), () => sdk.GetChatAsync(Guid.Empty),
            () => sdk.RenameChatAsync(ChatId, " "), () => sdk.DeleteChatAsync(Guid.Empty),
            () => sdk.SendMessageAsync(ChatId, Guid.Empty, "text"), () => sdk.SendMessageAsync(ChatId, MessageId, ""),
            () => sdk.GetRunAsync(ChatId, Guid.Empty), () => sdk.CancelRunAsync(Guid.Empty, RunId),
            () => sdk.RetryRunAsync(ChatId, MessageId, Guid.Empty), () => sdk.RetryRunAsync(ChatId, Guid.Empty, RetryId),
            () => sdk.GetMessagesAsync(ChatId, limit: 0), () => sdk.GetMessagesAsync(ChatId, limit: 201),
            () => sdk.GetMessagesAsync(ChatId, SoloAiMessageCursor.Create(Guid.NewGuid(), 5)),
        ];
        foreach (var operation in operations)
        {
            var error = await Assert.ThrowsAsync<SoloAiExchangeException>(operation);
            Assert.Equal("validation_failed", error.Code);
            Assert.False(error.OutcomeUnknown);
        }
        Assert.Equal(0, http.Calls);
    }
}
