using System.Net;
using System.Text;
using System.Text.Json;
using Solo.Ai.Client;
using Solo.Ai.Visograph;
using Xunit;
using static Solo.Ai.Tests.ButlerApiFixture;

namespace Solo.Ai.Tests;

public sealed class ChatModelExchangeTests
{
    [Theory]
    [InlineData("completed", "completed", null)]
    [InlineData("incomplete_response", "incomplete", "incomplete_response")]
    [InlineData("unauthorized", "failed", "dependency_authentication_failed")]
    [InlineData("malformed", "failed", "malformed_model_response")]
    [InlineData("unsupported_contract", "failed", "configuration_failure")]
    [InlineData("limit_exceeded", "failed", "limit_exceeded")]
    public async Task ExchangeChatSchemaOverServiceAuthenticatedTransportExactlyOnce(string outcome, string state, string? code)
    {
        using var handler = new ModelHandler(outcome);
        using var client = new VisographModelClient(new()
        {
            Enabled = true, ApiKey = "service-only-marker", BaseUri = new("https://visograph.test/"),
            RequestTimeout = TimeSpan.FromSeconds(5), MaxRequestBytes = 100000, MaxResponseBytes = 100000,
        }, handler);
        var chat = Guid.NewGuid();
        using var api = await StartAsync(modelClient: client, prepareStorage: db =>
        {
            db.Chats.CreateChat(Alice, chat);
            var previous = db.Runs.SendMessage(Alice, chat, new(2, Guid.NewGuid(), "previous-user"));
            db.Transitions.TryStartRun(Alice, chat, previous.RunId);
            db.Transitions.FinishRun(Alice, chat, previous.RunId, "completed", assistantText: "previous-assistant");
        });
        using var response = await api.SendAsync("POST", $"/api/v2/chats/{chat}/messages", new SendMessageRequest(2, Guid.NewGuid(), "current-user"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await ReadDataAsync<SoloAiRun>(response);
        await GenerationFixture.WaitAsync(() => api.Storage.Runs.GetRun(Alice, chat, run.RunId).State == state);
        var result = api.Storage.Runs.GetRun(Alice, chat, run.RunId);
        Assert.Equal(code, result.OutcomeCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(run.RunId, request.RequestId);
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, request.Messages.Select(message => message.Role));
        Assert.Equal(new[] { "previous-user", "previous-assistant", "current-user" }, request.Messages.Skip(1).Select(message => message.Content));
        Assert.Equal(state == "completed" ? 4 : 3, api.Storage.Chats.GetMessages(Alice, chat).Messages.Count);
        await GenerationFixture.WaitAsync(() => api.Logs.Entries.Any(entry => entry.Contains(run.RunId.ToString())));
        Assert.DoesNotContain(api.Logs.Entries, entry => new[]
        {
            "service-only-marker", "previous-user", "previous-assistant", "current-user",
            "private-provider-body", "private-invalid-json", "answer",
        }.Any(entry.Contains));
    }

    private sealed class ModelHandler(string outcome) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<ModelGenerationRequest> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://visograph.test/api/v1/model/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("service-only-marker", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("X-Solo-User-Id"));
            var input = ModelProtocol.ParseRequest(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Requests.Enqueue(input);
            Assert.False(input.ResponseFormat.Schema["additionalProperties"]!.GetValue<bool>());
            Assert.Single(input.ResponseFormat.Schema["properties"]!.AsObject());
            if (outcome == "unauthorized") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("private-provider-body") };
            if (outcome == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("private-invalid-json") };
            var result = new ModelGenerationResponse(1, input.RequestId, outcome)
            {
                Content = outcome == "completed" ? "{\"text\":\"answer\"}" : null,
                Stage = outcome == "completed" ? null : "provider",
            };
            return new(outcome == "completed" ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(result, ModelProtocol.JsonOptions), Encoding.UTF8, "application/json"),
            };
        }
    }
}
