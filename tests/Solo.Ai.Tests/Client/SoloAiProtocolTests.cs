using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solo.Ai.Client;
using Xunit;
using static Solo.Ai.Tests.SoloAiHttpFixture;

namespace Solo.Ai.Tests;

public sealed class SoloAiProtocolTests
{
    [Theory]
    [InlineData("create-v2.json")]
    [InlineData("rename-v2.json")]
    [InlineData("send-v2.json")]
    [InlineData("retry-request-v2.json")]
    public void RejectMissingUnknownDuplicateAndUntrustedRequestFields(string fixture)
    {
        var original = ReadFixture(fixture);
        SoloAiValidation.ValidateRequest(ReadRequest(fixture, original));
        foreach (var extra in new[] { "userId", "owner", "history", "provider", "catalogue", "tools", "attachments", "model", "systemPrompt" })
        {
            var request = JsonNode.Parse(original)!;
            request[extra] = "UNTRUSTED";
            Assert.Throws<JsonException>(() => ReadRequest(fixture, request.ToJsonString()));
        }
        var properties = JsonNode.Parse(original)!.AsObject().Select(property => property.Key).ToArray();
        foreach (var property in properties)
        {
            var request = JsonNode.Parse(original)!;
            request.AsObject().Remove(property);
            Assert.Throws<JsonException>(() => ReadRequest(fixture, request.ToJsonString()));
        }
        var compact = JsonNode.Parse(original)!.ToJsonString();
        Assert.Throws<JsonException>(() => ReadRequest(fixture, compact.Insert(1, "\"contractVersion\":2,")));
        var old = JsonNode.Parse(original)!;
        old["contractVersion"] = 1;
        Assert.Equal("unsupported_contract", Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRequest(ReadRequest(fixture, old.ToJsonString()))).Code);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("cancellation_requested")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("timed_out")]
    [InlineData("incomplete")]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    public void ValidateEveryRunStateAndRejectContradictoryOutcomes(string state)
    {
        var run = SoloAiProtocol.Deserialize<SoloAiResponse<SoloAiRun>>(Encoding.UTF8.GetBytes(ReadFixture("run-v2.json"))).Data!;
        var terminal = state is not ("pending" or "running" or "cancellation_requested");
        run = run with
        {
            State = state,
            StartedAt = state == "pending" ? null : run.CreatedAt.AddSeconds(1),
            FinishedAt = terminal ? run.CreatedAt.AddSeconds(2) : null,
            AssistantMessageId = state == "completed" ? Guid.NewGuid() : null,
            OutcomeCode = state switch
            {
                "failed" => "provider_error", "timed_out" => "timed_out", "incomplete" => "incomplete_response",
                "cancelled" => "cancelled", "interrupted" => "interrupted", _ => null,
            },
        };
        SoloAiValidation.ValidateRun(run, ChatId);
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRun(run with { OutcomeCode = "RAW-PROVIDER-SECRET" }, ChatId));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRun(run with { FinishedAt = terminal ? null : run.CreatedAt }, ChatId));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRun(run with { RunId = Guid.Empty }, ChatId));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRun(run with { DeadlineAt = run.CreatedAt }, ChatId));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRun(run with { RetryId = Guid.Empty }, ChatId));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRun(run with { AssistantMessageId = Guid.Empty }, ChatId));
    }

    [Fact]
    public void RejectInvalidPagesAndCursorCorrelations()
    {
        var page = SoloAiProtocol.Deserialize<SoloAiResponse<SoloAiMessagePage>>(Encoding.UTF8.GetBytes(ReadFixture("messages-v2.json"))).Data!;
        SoloAiValidation.ValidatePage(page, ChatId, null, 50);
        SoloAiMessagePage[] invalid = [
            page with { ChatId = Guid.NewGuid() },
            page with { Messages = [page.Messages[1], page.Messages[0]] },
            page with { Messages = [page.Messages[0], page.Messages[0]] },
            page with { Messages = [page.Messages[0] with { Sequence = 0 }] },
            page with { Messages = [page.Messages[0] with { Role = "system" }] },
            page with { Messages = [page.Messages[0] with { MessageId = Guid.NewGuid() }] },
            page with { Messages = [page.Messages[0] with { ChatId = Guid.NewGuid() }] },
            page with { Messages = [null!] },
            page with { Messages = [], NextCursor = page.NextCursor },
            page with { NextCursor = SoloAiMessageCursor.Create(Guid.NewGuid(), 5) },
            page with { NextCursor = SoloAiMessageCursor.Create(ChatId, 7) },
        ];
        foreach (var value in invalid) Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidatePage(value, ChatId, null, 50));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidatePage(page, ChatId, SoloAiMessageCursor.Create(ChatId, 6), 50));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidatePage(page, ChatId, null, 1));
        foreach (var value in new[] { "", "bad", $"{ChatId:N}:0", $"{ChatId:N}:-1", $"{ChatId:N}:01", $"{ChatId:N}:9223372036854775808" })
            Assert.Throws<SoloAiExchangeException>(() => SoloAiMessageCursor.Read(value, ChatId));
        Assert.Equal(long.MaxValue, SoloAiMessageCursor.Read(SoloAiMessageCursor.Create(ChatId, long.MaxValue), ChatId));
    }

    [Fact]
    public async Task EnforceUtf8AndCompleteJsonByteBoundsWithoutTruncation()
    {
        SoloAiValidation.ValidateRequest(new RenameChatRequest(2, new string('я', 256)));
        Assert.Equal("limit_exceeded", Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRequest(new RenameChatRequest(2, new string('я', 257)))).Code);
        SoloAiValidation.ValidateRequest(new SendMessageRequest(2, MessageId, new string('я', 32768)));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiValidation.ValidateRequest(new SendMessageRequest(2, MessageId, new string('я', 32769))));
        var value = new SendMessageRequest(2, MessageId, "Привет");
        Assert.Equal("validation_failed", Assert.Throws<SoloAiExchangeException>(() =>
            SoloAiValidation.ValidateRequest(value with { Text = "\ud800" })).Code);
        var bytes = SoloAiProtocol.SerializeBounded(value, 100_000);
        Assert.Equal(bytes, SoloAiProtocol.SerializeBounded(value, bytes.Length));
        Assert.Throws<SoloAiExchangeException>(() => SoloAiProtocol.SerializeBounded(value, bytes.Length - 1));
        using var exact = new MemoryStream(bytes);
        Assert.Equal(bytes, await SoloAiProtocol.ReadBoundedAsync(exact, bytes.Length, TestContext.Current.CancellationToken));
        using var overflow = new MemoryStream(bytes);
        await Assert.ThrowsAsync<SoloAiExchangeException>(() => SoloAiProtocol.ReadBoundedAsync(overflow, bytes.Length - 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RejectNullMissingAndExtraNestedResponseProperties()
    {
        var original = ReadFixture("chat-v2.json");
        foreach (var property in new[] { "chatId", "title", "createdAt", "lastMessageAt", "latestRun" })
        {
            var body = JsonNode.Parse(original)!;
            body["data"]!.AsObject().Remove(property);
            Assert.Throws<JsonException>(() => SoloAiProtocol.Deserialize<SoloAiResponse<SoloAiChat>>(Encoding.UTF8.GetBytes(body.ToJsonString())));
        }
        var unknown = JsonNode.Parse(original)!;
        unknown["data"]!["ownerId"] = Guid.NewGuid();
        Assert.Throws<JsonException>(() => SoloAiProtocol.Deserialize<SoloAiResponse<SoloAiChat>>(Encoding.UTF8.GetBytes(unknown.ToJsonString())));
        var nullTitle = JsonNode.Parse(original)!;
        nullTitle["data"]!["title"] = null;
        Assert.Throws<JsonException>(() => SoloAiProtocol.Deserialize<SoloAiResponse<SoloAiChat>>(Encoding.UTF8.GetBytes(nullTitle.ToJsonString())));
    }

    private static object ReadRequest(string fixture, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return fixture switch
        {
            "create-v2.json" => SoloAiProtocol.Deserialize<CreateChatRequest>(bytes),
            "rename-v2.json" => SoloAiProtocol.Deserialize<RenameChatRequest>(bytes),
            "send-v2.json" => SoloAiProtocol.Deserialize<SendMessageRequest>(bytes),
            _ => SoloAiProtocol.Deserialize<RetryRunRequest>(bytes),
        };
    }
}
