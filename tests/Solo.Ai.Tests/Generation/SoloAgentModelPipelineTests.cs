using Solo.Ai.Visograph;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class SoloAgentModelPipelineTests
{
    [Fact]
    public async Task SendOrderedHistoryAndStrictChatSchemaWithoutCatalog()
    {
        var client = new GenerationModelClient();
        var id = Guid.NewGuid();
        var task = CreatePipeline(client).GenerateAsync(id,
            [new("user", "first"), new("assistant", "previous"), new("user", "current")], TestContext.Current.CancellationToken);
        var request = await client.NextAsync();
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, request.Messages.Select(message => message.Role));
        Assert.Equal("current", request.Messages[^1].Content);
        Assert.Equal(id, request.RequestId);
        Assert.False(request.ResponseFormat.Schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("text", request.ResponseFormat.Schema["required"]![0]!.GetValue<string>());
        Assert.Single(request.ResponseFormat.Schema["properties"]!.AsObject());
        client.Complete(id, "{\"text\":\"plain\\n<script>literal</script>\"}");
        Assert.Equal("plain\n<script>literal</script>", await task);
        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":3}")]
    [InlineData("{\"text\":\" \"}")]
    [InlineData("{\"text\":\"ok\",\"extra\":true}")]
    [InlineData("{\"text\":\"first\",\"text\":\"second\"}")]
    public async Task RejectInvalidTextWithoutRepair(string content)
    {
        var client = new GenerationModelClient();
        var id = Guid.NewGuid();
        var task = CreatePipeline(client).GenerateAsync(id, [new("user", "hello")], TestContext.Current.CancellationToken);
        await client.NextAsync();
        client.Complete(id, content);
        Assert.Equal("malformed_model_response", (await Assert.ThrowsAsync<ModelExchangeException>(() => task)).Code);
        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData("incomplete_response")]
    [InlineData("provider_error")]
    [InlineData("limit_exceeded")]
    [InlineData("configuration_failure")]
    [InlineData("timeout")]
    public async Task PreserveProviderOutcomeWithoutPartialTextOrRetry(string outcome)
    {
        var client = new GenerationModelClient();
        var id = Guid.NewGuid();
        var task = CreatePipeline(client).GenerateAsync(id, [new("user", "hello")], TestContext.Current.CancellationToken);
        await client.NextAsync();
        client.Fail(id, outcome);
        Assert.Equal(outcome, (await Assert.ThrowsAsync<ModelExchangeException>(() => task)).Code);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task RejectOversizedInputBeforeDispatchAndOutputAfterDispatch()
    {
        var client = new GenerationModelClient();
        var pipeline = CreatePipeline(client);
        var error = await Assert.ThrowsAsync<ModelExchangeException>(() => pipeline.GenerateAsync(Guid.NewGuid(),
            [new("user", new string('x', 100000))], TestContext.Current.CancellationToken));
        Assert.Equal("limit_exceeded", error.Code);
        Assert.Empty(client.Calls);
        var id = Guid.NewGuid();
        var task = pipeline.GenerateAsync(id, [new("user", "hello")], TestContext.Current.CancellationToken);
        await client.NextAsync();
        client.Complete(id, "{\"text\":\"" + new string('x', 65537) + "\"}");
        Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<ModelExchangeException>(() => task)).Code);
    }

    [Fact]
    public async Task BoundUncooperativeClientByTimeoutAndCancellation()
    {
        var client = new GenerationModelClient { IgnoreCancellation = true };
        var pipeline = CreatePipeline(client, TimeSpan.FromMilliseconds(100));
        Assert.Equal("timeout", (await Assert.ThrowsAsync<ModelExchangeException>(() => pipeline.GenerateAsync(Guid.NewGuid(),
            [new("user", "hello")], TestContext.Current.CancellationToken))).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.GenerateAsync(Guid.NewGuid(), [new("user", "hello")], cancelled.Token));
        Assert.Single(client.Calls);
    }

    private static SoloAgentModelPipeline CreatePipeline(IModelGenerationClient client, TimeSpan? timeout = null) =>
        new(client, new() { Enabled = true, MaxRequestBytes = 100000, TotalDeadline = TimeSpan.FromSeconds(10),
            ModelTimeout = timeout ?? TimeSpan.FromSeconds(10) });
}
